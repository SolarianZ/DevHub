using DevHub.Core.Extensions;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services.Events;
using DevHub.Core.Services;
using DevHub.Core.Services.Invocation;
using DevHub.Core.Services.Rpc;
using DevHub.Core.Services.Rpc.Transport;
using Microsoft.Extensions.Logging;
using Serilog;
using System.Net.WebSockets;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace DevHub.Host
{
    /// <summary>
    /// DevHub Host 进程入口。
    /// 负责单实例控制、依赖注入装配与 HTTP RPC 服务启动。
    /// </summary>
    public class Program
    {
        private static Mutex? _singleInstanceMutex;

        /// <summary>
        /// 构建当前用户维度的单实例互斥量名称。
        /// </summary>
        private static string BuildSingleInstanceMutexName()
        {
            var userKey = ResolveCurrentUserKey();
            return $"Local\\DevHub_{userKey}";
        }

        /// <summary>
        /// 解析当前用户标识。
        /// </summary>
        private static string ResolveCurrentUserKey()
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using var identity = WindowsIdentity.GetCurrent();
                    var sid = identity.User?.Value;
                    if (!string.IsNullOrWhiteSpace(sid))
                    {
                        return NormalizeMutexUserKey(sid);
                    }
                }
                catch
                {
                }
            }

            var userName = Environment.UserName;
            if (string.IsNullOrWhiteSpace(userName))
            {
                userName = "unknown_user";
            }

            return NormalizeMutexUserKey(userName);
        }

        /// <summary>
        /// 将用户标识转换为可用于系统命名对象的安全键。
        /// </summary>
        private static string NormalizeMutexUserKey(string rawKey)
        {
            var builder = new StringBuilder(rawKey.Length);
            foreach (var ch in rawKey)
            {
                builder.Append(char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : '_');
            }

            return builder.Length > 0 ? builder.ToString() : "UNKNOWN_USER";
        }

        /// <summary>
        /// 应用程序主入口。
        /// </summary>
        /// <param name="args">命令行参数。</param>
        public static void Main(string[] args)
        {
            #region 检查是否已有实例在运行

            bool createdNew;
            var mutexName = BuildSingleInstanceMutexName();
            _singleInstanceMutex = new Mutex(true, mutexName, out createdNew);

            if (!createdNew)
            {
                // 已有实例在运行，直接退出
                Console.WriteLine("DevHub 已在运行中");
                return;
            }

            #endregion


            #region  配置 Serilog

            const string DevHubLogDir = "DEVHUB_LOG_DIR";
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(DevHubLogDir)))
            {
                var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevHub", "logs");
                Environment.SetEnvironmentVariable(DevHubLogDir, logDir);
            }

            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json")
                    .AddEnvironmentVariables()
                    .Build())
                .CreateLogger();

            #endregion


            try
            {
                Log.Information("DevHub 启动初始化...");

                var builder = WebApplication.CreateBuilder(args);

                // 使用 Serilog 替代默认日志系统
                builder.Host.UseSerilog();

                // Add services to the container.
                builder.Services.AddAuthorization();

                // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
                builder.Services.AddOpenApi();

                // Add DevHub core services
                const string DevHubAppDefsDir = "DEVHUB_APPDEFS_DIR";
                string definitionsPath;

                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(DevHubAppDefsDir)))
                {
                    definitionsPath = Environment.GetEnvironmentVariable(DevHubAppDefsDir)!;
                    Log.Debug("使用环境变量配置的应用程序定义目录: {DefinitionsPath}", definitionsPath);
                }
                else
                {
                    definitionsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevHub", "apps", "definitions");
                    Log.Debug("使用默认应用程序定义目录: {DefinitionsPath}", definitionsPath);
                }

                builder.Services.AddDevHubCore(definitionsPath);
                Log.Information("DevHub 核心服务注册完成");

                var app = builder.Build();
                var logger = app.Services.GetRequiredService<ILogger<Program>>();

                logger.LogInformation("Web 应用程序构建完成，开始初始化系统...");

                // 初始化文件系统
                var fileSystemManager = app.Services.GetRequiredService<FileSystemManager>();
                logger.LogDebug("初始化文件系统目录结构...");
                fileSystemManager.InitializeDirectories();
                logger.LogInformation("文件系统初始化完成");

                // 确保 token 文件存在
                logger.LogDebug("确保 token 文件存在...");
                fileSystemManager.GetToken();
                logger.LogInformation("Token 文件准备完成");

                // 加载应用程序定义
                var definitionLoader = app.Services.GetRequiredService<DefinitionLoader>();
                logger.LogDebug("加载应用程序定义...");
                definitionLoader.Load();
                logger.LogInformation("应用程序定义加载完成");

                // 启动 Invocation 超时扫描器
                _ = app.Services.GetRequiredService<InvocationTimeoutWorker>();
                logger.LogInformation("InvocationTimeoutWorker 已启动");

                // Configure the HTTP request pipeline.
                if (app.Environment.IsDevelopment())
                {
                    app.MapOpenApi();
                }

                app.UseAuthorization();
                app.UseWebSockets();

                var currentPort = 0;

                app.Map("/ws", async (
                    HttpContext context,
                    RpcRouter rpcRouter,
                    FileSystemManager fsManager,
                    HubEventBus eventBus,
                    ILogger<Program> endpointLogger,
                    CancellationToken cancellationToken) =>
                {
                    if (!context.WebSockets.IsWebSocketRequest)
                    {
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        return;
                    }

                    fsManager.EnsureRuntimeArtifacts(currentPort > 0 ? currentPort : null);

                    using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    await HandleWebSocketConnectionAsync(
                        webSocket,
                        rpcRouter,
                        fsManager,
                        eventBus,
                        endpointLogger,
                        cancellationToken);
                });

                // RPC endpoint
                app.MapPost("/rpc", async (HttpRequest request, RpcRouter rpcRouter, FileSystemManager fsManager, ILogger<Program> endpointLogger, CancellationToken cancellationToken) =>
                {
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    string? clientId = null;
                    object? requestId = null;
                    string? method = null;

                    try
                    {
                        // 轻量确保 runtime 产物存在，避免运行过程中被误删导致后续请求失败
                        fsManager.EnsureRuntimeArtifacts(currentPort > 0 ? currentPort : null);

                        // 获取客户端ID（用于日志上下文）
                        request.Headers.TryGetValue("X-DevHub-ClientId", out var clientIdValue);
                        clientId = clientIdValue;

                        // Read and parse JSON-RPC request
                        using var reader = new StreamReader(request.Body);
                        var body = await reader.ReadToEndAsync(cancellationToken);

                        endpointLogger.LogDebug("收到RPC请求，客户端ID: {ClientId}，请求体: {RequestBody}", clientId, body);

                        var jsonOptions = new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        };

                        JsonDocument requestDocument;
                        try
                        {
                            requestDocument = JsonDocument.Parse(body);
                        }
                        catch (JsonException ex)
                        {
                            endpointLogger.LogWarning(ex, "JSON 解析失败，返回 parse_error，ClientId: {ClientId}", clientId);
                            return Results.Json(DevHubTransportValidator.CreateErrorResponse(-32700, "parse_error", null));
                        }

                        using (requestDocument)
                        {
                            var root = requestDocument.RootElement;
                            if (root.ValueKind == JsonValueKind.Array)
                            {
                                endpointLogger.LogWarning("收到批量请求，按规范拒绝，ClientId: {ClientId}", clientId);
                                return Results.Json(DevHubTransportValidator.CreateErrorResponse(-32600, "invalid_request", null), jsonOptions);
                            }

                            if (root.ValueKind != JsonValueKind.Object)
                            {
                                endpointLogger.LogWarning("收到非对象 JSON-RPC 根节点，ClientId: {ClientId}", clientId);
                                return Results.Json(DevHubTransportValidator.CreateErrorResponse(-32600, "invalid_request", null), jsonOptions);
                            }

                            if (!DevHubTransportValidator.TryBuildRpcRequest(root, out var rpcRequest, out var requestErrorResponse))
                            {
                                endpointLogger.LogWarning("JSON-RPC 信封无效，ClientId: {ClientId}", clientId);
                                return Results.Json(requestErrorResponse, jsonOptions);
                            }

                            requestId = rpcRequest.Id;
                            method = rpcRequest.Method;

                            endpointLogger.LogInformation("处理RPC请求: {Method}, RequestId: {RequestId}, ClientId: {ClientId}",
                                rpcRequest.Method, rpcRequest.Id, clientId);

                            // 校验请求头
                            var requestHeaders = request.Headers.ToDictionary(
                                pair => pair.Key,
                                pair => pair.Value.ToString(),
                                StringComparer.OrdinalIgnoreCase);
                            if (!DevHubTransportValidator.TryValidateHttpHeaders(
                                request.ContentType,
                                requestHeaders,
                                fsManager.GetToken,
                                rpcRequest.Id,
                                out var errorResponse,
                                out var validatedClientId,
                                out var validatedClientSessionId))
                            {
                                endpointLogger.LogWarning("请求头校验失败，Method: {Method}, RequestId: {RequestId}, ClientId: {ClientId}, ErrorCode: {ErrorCode}, ErrorMessage: {ErrorMessage}",
                                    rpcRequest.Method, rpcRequest.Id, clientId, errorResponse.Error?.Code, errorResponse.Error?.Message);
                                return Results.Json(errorResponse, jsonOptions);
                            }

                            rpcRequest.ClientId = validatedClientId;
                            rpcRequest.ClientSessionId = validatedClientSessionId;

                            // Spec: 所有 hub.* 方法 params 为数组时返回 invalid_params
                            if (DevHubTransportValidator.IsHubMethodParamsArray(rpcRequest))
                            {
                                endpointLogger.LogWarning("hub.* 方法参数为数组，返回 invalid_params，Method: {Method}, RequestId: {RequestId}",
                                    rpcRequest.Method, rpcRequest.Id);
                                return Results.Json(DevHubTransportValidator.CreateErrorResponse(-32602, "invalid_params", rpcRequest.Id), jsonOptions);
                            }

                            // Route request
                            var response = await rpcRouter.RouteAsync(rpcRequest, cancellationToken);

                            stopwatch.Stop();
                            endpointLogger.LogInformation("RPC请求处理成功: {Method}, RequestId: {RequestId}, ClientId: {ClientId}, 处理时间: {ElapsedMilliseconds}ms",
                                rpcRequest.Method, rpcRequest.Id, clientId, stopwatch.ElapsedMilliseconds);

                            endpointLogger.LogDebug("RPC响应内容: {Response}", JsonSerializer.Serialize(response, jsonOptions));
                            return Results.Json(response, jsonOptions);
                        }
                    }
                    catch (Exception ex)
                    {
                        stopwatch.Stop();
                        logger.LogError(ex, "处理RPC请求时发生未捕获的异常，Method: {Method}, RequestId: {RequestId}, ClientId: {ClientId}, 处理时间: {ElapsedMilliseconds}ms",
                            method, requestId, clientId, stopwatch.ElapsedMilliseconds);
                        return Results.Json(new JsonRpcResponse
                        {
                            Id = requestId,
                            Error = new JsonRpcError
                            {
                                Code = -32603,
                                Message = "internal_error"
                            }
                        });
                    }
                });

                // 启动服务器并获取监听端口
                logger.LogDebug("启动服务器...");

                // 动态分配端口
                var url = "http://127.0.0.1:0";
                app.Urls.Add(url);

                // 启动服务器并获取实际监听端口
                app.Lifetime.ApplicationStarted.Register(() =>
                {
                    var addresses = app.Urls;
                    foreach (var address in addresses)
                    {
                        if (address.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase) ||
                            address.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase))
                        {
                            var portStartIndex = address.LastIndexOf(':') + 1;
                            var portStr = address.Substring(portStartIndex);
                            if (int.TryParse(portStr, out var parsedPort))
                            {
                                currentPort = parsedPort;
                                logger.LogInformation("服务器成功启动，监听地址: {Address}", address);
                                logger.LogDebug("写入 hub.json 文件...");
                                fileSystemManager.WriteHubJson(parsedPort);
                                logger.LogInformation("DevHub 启动成功，监听端口: {Port}", parsedPort);
                                logger.LogInformation("HTTP 地址: http://127.0.0.1:{Port}", parsedPort);
                            }
                        }
                    }
                });

                // 启动服务器
                app.Run();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "DevHub 启动过程中发生致命错误");
                return;
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        /// <summary>
        /// 处理 WebSocket 连接生命周期。
        /// </summary>
        private static async Task HandleWebSocketConnectionAsync(
            WebSocket webSocket,
            RpcRouter rpcRouter,
            FileSystemManager fileSystemManager,
            HubEventBus eventBus,
            ILogger<Program> logger,
            CancellationToken cancellationToken)
        {
            var connectionId = $"conn-{Guid.NewGuid():N}";
            var isAuthenticated = false;
            var firstMessageProcessed = false;
            string? authenticatedClientId = null;
            string? authenticatedClientSessionId = null;

            eventBus.RegisterConnection(connectionId);
            logger.LogInformation("WS 连接已建立，ConnectionId: {ConnectionId}", connectionId);

            try
            {
                while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    var receiveTask = ReceiveTextMessageAsync(webSocket, cancellationToken);

                    while (!receiveTask.IsCompleted)
                    {
                        await SendPendingHubEventsAsync(webSocket, eventBus, connectionId, cancellationToken);
                        var completedTask = await Task.WhenAny(receiveTask, Task.Delay(50, cancellationToken));
                        if (completedTask == receiveTask)
                        {
                            break;
                        }
                    }

                    var receiveEnvelope = await receiveTask;
                    if (receiveEnvelope.IsCloseFrame)
                    {
                        logger.LogInformation("WS 收到关闭帧，ConnectionId: {ConnectionId}", connectionId);
                        break;
                    }

                    if (!receiveEnvelope.IsTextFrame)
                    {
                        logger.LogWarning("WS 收到非文本帧，主动关闭连接，ConnectionId: {ConnectionId}", connectionId);
                        await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.InvalidMessageType, "text_frame_required", logger, cancellationToken);
                        break;
                    }

                    var messageText = receiveEnvelope.Text ?? string.Empty;
                    logger.LogDebug("收到 WS 消息，ConnectionId: {ConnectionId}, Message: {Message}", connectionId, messageText);

                    JsonDocument requestDocument;
                    try
                    {
                        requestDocument = JsonDocument.Parse(messageText);
                    }
                    catch (JsonException ex)
                    {
                        logger.LogWarning(ex, "WS JSON 解析失败，ConnectionId: {ConnectionId}", connectionId);
                        await SendWebSocketJsonAsync(webSocket, DevHubTransportValidator.CreateErrorResponse(-32700, "parse_error", null), cancellationToken);

                        if (!isAuthenticated)
                        {
                            await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.PolicyViolation, "parse_error", logger, cancellationToken);
                            break;
                        }

                        continue;
                    }

                    using (requestDocument)
                    {
                        var root = requestDocument.RootElement;
                        if (root.ValueKind == JsonValueKind.Array || root.ValueKind != JsonValueKind.Object)
                        {
                            await SendWebSocketJsonAsync(webSocket, DevHubTransportValidator.CreateErrorResponse(-32600, "invalid_request", null), cancellationToken);
                            if (!isAuthenticated)
                            {
                                await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.PolicyViolation, "invalid_request", logger, cancellationToken);
                                break;
                            }

                            continue;
                        }

                        if (!DevHubTransportValidator.TryBuildRpcRequest(root, out var rpcRequest, out var envelopeError))
                        {
                            await SendWebSocketJsonAsync(webSocket, envelopeError, cancellationToken);
                            if (!isAuthenticated)
                            {
                                await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.PolicyViolation, "invalid_request", logger, cancellationToken);
                                break;
                            }

                            continue;
                        }

                        if (!firstMessageProcessed)
                        {
                            firstMessageProcessed = true;

                            if (!string.Equals(rpcRequest.Method, "hub.ws.authenticate", StringComparison.Ordinal))
                            {
                                if (rpcRequest.Id is not null)
                                {
                                    await SendWebSocketJsonAsync(
                                        webSocket,
                                        DevHubTransportValidator.CreateErrorResponse(-32001, "unauthorized", rpcRequest.Id, new { reason = "missing_token" }),
                                        cancellationToken);
                                }

                                await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.PolicyViolation, "authentication_required", logger, cancellationToken);
                                break;
                            }

                            if (rpcRequest.Id is null)
                            {
                                await SendWebSocketJsonAsync(webSocket, DevHubTransportValidator.CreateErrorResponse(-32600, "invalid_request", null), cancellationToken);
                                await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.PolicyViolation, "auth_request_id_required", logger, cancellationToken);
                                break;
                            }
                        }

                        if (!isAuthenticated && !string.Equals(rpcRequest.Method, "hub.ws.authenticate", StringComparison.Ordinal))
                        {
                            if (rpcRequest.Id is not null)
                            {
                                await SendWebSocketJsonAsync(
                                    webSocket,
                                    DevHubTransportValidator.CreateErrorResponse(-32001, "unauthorized", rpcRequest.Id, new { reason = "missing_token" }),
                                    cancellationToken);
                                await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.PolicyViolation, "authentication_required", logger, cancellationToken);
                            }
                            else
                            {
                                await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.PolicyViolation, "authentication_required", logger, cancellationToken);
                            }

                            break;
                        }

                        if (DevHubTransportValidator.IsHubMethodParamsArray(rpcRequest))
                        {
                            if (rpcRequest.Id is not null)
                            {
                                await SendWebSocketJsonAsync(webSocket, DevHubTransportValidator.CreateErrorResponse(-32602, "invalid_params", rpcRequest.Id), cancellationToken);
                            }

                            continue;
                        }

                        JsonRpcResponse? response = null;
                        var closeAfterResponse = false;

                        switch (rpcRequest.Method)
                        {
                            case "hub.ws.authenticate":
                                if (isAuthenticated)
                                {
                                    response = DevHubTransportValidator.CreateErrorResponse(-32600, "invalid_request", rpcRequest.Id, new { reason = "already_authenticated" });
                                    break;
                                }

                                response = DevHubTransportValidator.HandleWsAuthenticate(
                                    rpcRequest,
                                    fileSystemManager.GetToken,
                                    (clientId, sessionId) => eventBus.TryMarkAuthenticated(connectionId, clientId, sessionId),
                                    out var authenticated,
                                    out var nextClientId,
                                    out var nextClientSessionId,
                                    out closeAfterResponse);

                                if (authenticated)
                                {
                                    isAuthenticated = true;
                                    authenticatedClientId = nextClientId;
                                    authenticatedClientSessionId = nextClientSessionId;
                                    logger.LogInformation(
                                        "WS 鉴权成功，ConnectionId: {ConnectionId}, ClientId: {ClientId}, SessionId: {SessionId}",
                                        connectionId,
                                        authenticatedClientId,
                                        authenticatedClientSessionId);
                                }

                                break;

                            case "hub.events.subscribe":
                                if (!DevHubTransportValidator.TryReadSubscriptionTypes(rpcRequest, out var subscriptionTypes, out var subscribeError))
                                {
                                    response = subscribeError;
                                    break;
                                }

                                if (!eventBus.TrySubscribe(connectionId, subscriptionTypes, out var subscriptionId))
                                {
                                    response = DevHubTransportValidator.CreateErrorResponse(-32001, "unauthorized", rpcRequest.Id, new { reason = "missing_token" });
                                    closeAfterResponse = true;
                                    break;
                                }

                                response = new JsonRpcResponse
                                {
                                    Id = rpcRequest.Id,
                                    Result = new
                                    {
                                        ok = true,
                                        subscriptionId
                                    }
                                };
                                break;

                            case "hub.events.unsubscribe":
                                if (!DevHubTransportValidator.TryReadUnsubscribeParam(rpcRequest, out var subscriptionIdToRemove, out var unsubscribeError))
                                {
                                    response = unsubscribeError;
                                    break;
                                }

                                eventBus.Unsubscribe(connectionId, subscriptionIdToRemove);
                                response = new JsonRpcResponse
                                {
                                    Id = rpcRequest.Id,
                                    Result = new
                                    {
                                        ok = true
                                    }
                                };
                                break;

                            default:
                                if (DevHubTransportValidator.IsHttpOnlyMethod(rpcRequest.Method))
                                {
                                    response = DevHubTransportValidator.CreateErrorResponse(-32601, "method_not_found", rpcRequest.Id);
                                    break;
                                }

                                rpcRequest.ClientId = authenticatedClientId;
                                rpcRequest.ClientSessionId = authenticatedClientSessionId;
                                response = await rpcRouter.RouteAsync(rpcRequest, cancellationToken);
                                break;
                        }

                        if (response is not null && rpcRequest.Id is not null)
                        {
                            await SendWebSocketJsonAsync(webSocket, response, cancellationToken);
                        }

                        if (closeAfterResponse)
                        {
                            await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.PolicyViolation, "authentication_failed", logger, cancellationToken);
                            break;
                        }
                    }

                    await SendPendingHubEventsAsync(webSocket, eventBus, connectionId, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogDebug("WS 连接处理被取消，ConnectionId: {ConnectionId}", connectionId);
            }
            catch (WebSocketException ex)
            {
                logger.LogWarning(ex, "WS 连接异常，ConnectionId: {ConnectionId}", connectionId);
            }
            finally
            {
                eventBus.RemoveConnection(connectionId);
                await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.NormalClosure, "connection_closed", logger, CancellationToken.None);
                logger.LogInformation("WS 连接已清理，ConnectionId: {ConnectionId}", connectionId);
            }
        }

        /// <summary>
        /// 发送当前连接待投递的 hub.event 通知。
        /// </summary>
        private static async Task SendPendingHubEventsAsync(
            WebSocket webSocket,
            HubEventBus eventBus,
            string connectionId,
            CancellationToken cancellationToken)
        {
            if (webSocket.State != WebSocketState.Open)
            {
                return;
            }

            var deliveries = eventBus.DrainDeliveries(connectionId, maxCount: 32);
            foreach (var delivery in deliveries)
            {
                var notification = HubEventNotificationFactory.Create(delivery);

                await SendWebSocketJsonAsync(webSocket, notification, cancellationToken);
            }
        }

        /// <summary>
        /// 从 WebSocket 接收完整文本消息（支持分片）。
        /// </summary>
        private static async Task<WebSocketReceiveEnvelope> ReceiveTextMessageAsync(WebSocket webSocket, CancellationToken cancellationToken)
        {
            var buffer = new byte[4096];
            using var stream = new MemoryStream();

            while (true)
            {
                var receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    return new WebSocketReceiveEnvelope(true, false, null);
                }

                if (receiveResult.MessageType != WebSocketMessageType.Text)
                {
                    return new WebSocketReceiveEnvelope(false, false, null);
                }

                if (receiveResult.Count > 0)
                {
                    stream.Write(buffer, 0, receiveResult.Count);
                }

                if (receiveResult.EndOfMessage)
                {
                    return new WebSocketReceiveEnvelope(false, true, Encoding.UTF8.GetString(stream.ToArray()));
                }
            }
        }

        /// <summary>
        /// 发送 JSON 文本到 WebSocket。
        /// </summary>
        private static async Task SendWebSocketJsonAsync(WebSocket webSocket, object payload, CancellationToken cancellationToken)
        {
            if (webSocket.State != WebSocketState.Open)
            {
                return;
            }

            var json = JsonSerializer.Serialize(payload);
            var bytes = Encoding.UTF8.GetBytes(json);
            await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
        }

        /// <summary>
        /// 安全关闭 WebSocket 连接。
        /// </summary>
        private static async Task CloseWebSocketAsync(
            WebSocket webSocket,
            WebSocketCloseStatus closeStatus,
            string description,
            ILogger<Program> logger,
            CancellationToken cancellationToken)
        {
            if (webSocket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            {
                return;
            }

            try
            {
                await webSocket.CloseAsync(closeStatus, description, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "关闭 WS 连接时发生异常，状态: {State}, Description: {Description}", webSocket.State, description);
            }
        }

        private readonly record struct WebSocketReceiveEnvelope(bool IsCloseFrame, bool IsTextFrame, string? Text);
    }
}
