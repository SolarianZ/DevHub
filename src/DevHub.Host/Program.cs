using DevHub.Core.Extensions;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services.Events;
using DevHub.Core.Services;
using DevHub.Core.Services.Invocation;
using DevHub.Core.Services.Rpc;
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
                            return Results.Json(CreateErrorResponse(-32700, "parse_error", null));
                        }

                        using (requestDocument)
                        {
                            var root = requestDocument.RootElement;
                            if (root.ValueKind == JsonValueKind.Array)
                            {
                                endpointLogger.LogWarning("收到批量请求，按规范拒绝，ClientId: {ClientId}", clientId);
                                return Results.Json(CreateErrorResponse(-32600, "invalid_request", null), jsonOptions);
                            }

                            if (root.ValueKind != JsonValueKind.Object)
                            {
                                endpointLogger.LogWarning("收到非对象 JSON-RPC 根节点，ClientId: {ClientId}", clientId);
                                return Results.Json(CreateErrorResponse(-32600, "invalid_request", null), jsonOptions);
                            }

                            if (!TryBuildRpcRequest(root, out var rpcRequest, out var requestErrorResponse))
                            {
                                endpointLogger.LogWarning("JSON-RPC 信封无效，ClientId: {ClientId}", clientId);
                                return Results.Json(requestErrorResponse, jsonOptions);
                            }

                            requestId = rpcRequest.Id;
                            method = rpcRequest.Method;

                            endpointLogger.LogInformation("处理RPC请求: {Method}, RequestId: {RequestId}, ClientId: {ClientId}",
                                rpcRequest.Method, rpcRequest.Id, clientId);

                            // 校验请求头
                            if (!ValidateHeaders(
                                request,
                                fsManager,
                                endpointLogger,
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
                            if (IsHubMethodParamsArray(rpcRequest))
                            {
                                endpointLogger.LogWarning("hub.* 方法参数为数组，返回 invalid_params，Method: {Method}, RequestId: {RequestId}",
                                    rpcRequest.Method, rpcRequest.Id);
                                return Results.Json(CreateErrorResponse(-32602, "invalid_params", rpcRequest.Id), jsonOptions);
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
                        await SendWebSocketJsonAsync(webSocket, CreateErrorResponse(-32700, "parse_error", null), cancellationToken);

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
                            await SendWebSocketJsonAsync(webSocket, CreateErrorResponse(-32600, "invalid_request", null), cancellationToken);
                            if (!isAuthenticated)
                            {
                                await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.PolicyViolation, "invalid_request", logger, cancellationToken);
                                break;
                            }

                            continue;
                        }

                        if (!TryBuildRpcRequest(root, out var rpcRequest, out var envelopeError))
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
                                        CreateErrorResponse(-32001, "unauthorized", rpcRequest.Id, new { reason = "missing_token" }),
                                        cancellationToken);
                                }

                                await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.PolicyViolation, "authentication_required", logger, cancellationToken);
                                break;
                            }

                            if (rpcRequest.Id is null)
                            {
                                await SendWebSocketJsonAsync(webSocket, CreateErrorResponse(-32600, "invalid_request", null), cancellationToken);
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
                                    CreateErrorResponse(-32001, "unauthorized", rpcRequest.Id, new { reason = "missing_token" }),
                                    cancellationToken);
                                await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.PolicyViolation, "authentication_required", logger, cancellationToken);
                            }
                            else
                            {
                                await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.PolicyViolation, "authentication_required", logger, cancellationToken);
                            }

                            break;
                        }

                        if (IsHubMethodParamsArray(rpcRequest))
                        {
                            if (rpcRequest.Id is not null)
                            {
                                await SendWebSocketJsonAsync(webSocket, CreateErrorResponse(-32602, "invalid_params", rpcRequest.Id), cancellationToken);
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
                                    response = CreateErrorResponse(-32600, "invalid_request", rpcRequest.Id, new { reason = "already_authenticated" });
                                    break;
                                }

                                response = HandleWsAuthenticate(
                                    rpcRequest,
                                    fileSystemManager,
                                    eventBus,
                                    connectionId,
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
                                if (!TryReadSubscriptionTypes(rpcRequest, out var subscriptionTypes, out var subscribeError))
                                {
                                    response = subscribeError;
                                    break;
                                }

                                if (!eventBus.TrySubscribe(connectionId, subscriptionTypes, out var subscriptionId))
                                {
                                    response = CreateErrorResponse(-32001, "unauthorized", rpcRequest.Id, new { reason = "missing_token" });
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
                                if (!TryReadUnsubscribeParam(rpcRequest, out var subscriptionIdToRemove, out var unsubscribeError))
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
                                if (IsHttpOnlyMethod(rpcRequest.Method))
                                {
                                    response = CreateErrorResponse(-32601, "method_not_found", rpcRequest.Id);
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
        /// 处理 WS 鉴权请求。
        /// </summary>
        private static JsonRpcResponse HandleWsAuthenticate(
            JsonRpcRequest request,
            FileSystemManager fileSystemManager,
            HubEventBus eventBus,
            string connectionId,
            out bool authenticated,
            out string? clientId,
            out string? clientSessionId,
            out bool closeAfterResponse)
        {
            authenticated = false;
            clientId = null;
            clientSessionId = null;
            closeAfterResponse = false;

            if (request.Params is not JsonElement paramsElement || paramsElement.ValueKind != JsonValueKind.Object)
            {
                return CreateErrorResponse(-32602, "invalid_params", request.Id);
            }

            if (!paramsElement.TryGetProperty("token", out var tokenElement) || tokenElement.ValueKind != JsonValueKind.String)
            {
                closeAfterResponse = true;
                return CreateErrorResponse(-32001, "unauthorized", request.Id, new { reason = "missing_token" });
            }

            var token = tokenElement.GetString();
            if (string.IsNullOrWhiteSpace(token))
            {
                closeAfterResponse = true;
                return CreateErrorResponse(-32001, "unauthorized", request.Id, new { reason = "missing_token" });
            }

            if (!paramsElement.TryGetProperty("protocolVersion", out var protocolElement) ||
                protocolElement.ValueKind != JsonValueKind.Number ||
                !protocolElement.TryGetInt32(out var protocolVersion))
            {
                closeAfterResponse = true;
                return CreateErrorResponse(-32099, "not_supported", request.Id, new { expected = 1, reason = "missing" });
            }

            if (protocolVersion != 1)
            {
                closeAfterResponse = true;
                return CreateErrorResponse(-32099, "not_supported", request.Id, new { expected = 1, received = protocolVersion, reason = "mismatch" });
            }

            if (!paramsElement.TryGetProperty("clientId", out var clientIdElement) ||
                clientIdElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(clientIdElement.GetString()))
            {
                return CreateErrorResponse(-32602, "invalid_params", request.Id);
            }

            if (!paramsElement.TryGetProperty("clientSessionId", out var sessionIdElement) ||
                sessionIdElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(sessionIdElement.GetString()))
            {
                return CreateErrorResponse(-32602, "invalid_params", request.Id);
            }

            var parsedClientId = clientIdElement.GetString()!.Trim();
            var parsedClientSessionId = sessionIdElement.GetString()!.Trim();

            if (!Guid.TryParseExact(parsedClientSessionId, "D", out _))
            {
                return CreateErrorResponse(-32602, "invalid_params", request.Id);
            }

            try
            {
                var currentToken = fileSystemManager.GetToken();
                if (!string.Equals(token, currentToken, StringComparison.Ordinal))
                {
                    closeAfterResponse = true;
                    return CreateErrorResponse(-32001, "unauthorized", request.Id, new { reason = "invalid_token" });
                }
            }
            catch
            {
                closeAfterResponse = true;
                return CreateErrorResponse(-32001, "unauthorized", request.Id, new { reason = "invalid_token" });
            }

            if (!eventBus.TryMarkAuthenticated(connectionId, parsedClientId, parsedClientSessionId))
            {
                closeAfterResponse = true;
                return CreateErrorResponse(-32603, "internal_error", request.Id);
            }

            authenticated = true;
            clientId = parsedClientId;
            clientSessionId = parsedClientSessionId;

            return new JsonRpcResponse
            {
                Id = request.Id,
                Result = new
                {
                    ok = true,
                    protocolVersion = 1
                }
            };
        }

        /// <summary>
        /// 读取订阅参数中的事件类型过滤。
        /// </summary>
        private static bool TryReadSubscriptionTypes(JsonRpcRequest request, out IReadOnlyCollection<string>? types, out JsonRpcResponse errorResponse)
        {
            types = null;

            if (request.Params is null)
            {
                errorResponse = null!;
                return true;
            }

            if (request.Params is not JsonElement paramsElement)
            {
                errorResponse = CreateErrorResponse(-32602, "invalid_params", request.Id);
                return false;
            }

            if (paramsElement.ValueKind == JsonValueKind.Null)
            {
                errorResponse = null!;
                return true;
            }

            if (paramsElement.ValueKind != JsonValueKind.Object)
            {
                errorResponse = CreateErrorResponse(-32602, "invalid_params", request.Id);
                return false;
            }

            if (!paramsElement.TryGetProperty("types", out var typesElement) ||
                typesElement.ValueKind == JsonValueKind.Null)
            {
                errorResponse = null!;
                return true;
            }

            if (typesElement.ValueKind != JsonValueKind.Array)
            {
                errorResponse = CreateErrorResponse(-32602, "invalid_params", request.Id);
                return false;
            }

            var parsedTypes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var typeElement in typesElement.EnumerateArray())
            {
                if (typeElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(typeElement.GetString()))
                {
                    errorResponse = CreateErrorResponse(-32602, "invalid_params", request.Id);
                    return false;
                }

                var eventType = typeElement.GetString()!;
                if (!HubEventBus.IsSupportedEventType(eventType))
                {
                    errorResponse = CreateErrorResponse(-32602, "invalid_params", request.Id, new { reason = "unsupported_event_type", type = eventType });
                    return false;
                }

                parsedTypes.Add(eventType);
            }

            types = parsedTypes.Count == 0 ? null : parsedTypes.ToArray();
            errorResponse = null!;
            return true;
        }

        /// <summary>
        /// 读取 unsubscribe 所需参数。
        /// </summary>
        private static bool TryReadUnsubscribeParam(JsonRpcRequest request, out string subscriptionId, out JsonRpcResponse errorResponse)
        {
            subscriptionId = string.Empty;

            if (request.Params is not JsonElement paramsElement ||
                paramsElement.ValueKind != JsonValueKind.Object ||
                !paramsElement.TryGetProperty("subscriptionId", out var subscriptionIdElement) ||
                subscriptionIdElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(subscriptionIdElement.GetString()))
            {
                errorResponse = CreateErrorResponse(-32602, "invalid_params", request.Id);
                return false;
            }

            subscriptionId = subscriptionIdElement.GetString()!.Trim();
            errorResponse = null!;
            return true;
        }

        /// <summary>
        /// 判断方法是否仅支持 HTTP 传输。
        /// </summary>
        private static bool IsHttpOnlyMethod(string method)
        {
            return method is
                "hub.apps.registerInstance" or
                "hub.apps.heartbeat" or
                "hub.apps.unregisterInstance" or
                "hub.apps.launch" or
                "hub.invoke.notify" or
                "hub.invoke.request" or
                "hub.invoke.poll" or
                "hub.invoke.respond";
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
                var notification = new
                {
                    jsonrpc = "2.0",
                    method = "hub.event",
                    @params = new
                    {
                        subscriptionId = delivery.SubscriptionId,
                        type = delivery.Type,
                        timeUtc = delivery.TimeUtc.ToString("O"),
                        payload = delivery.Payload
                    }
                };

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

        /// <summary>
        /// 校验 HTTP 请求头
        /// </summary>
        private static bool ValidateHeaders(
            HttpRequest request,
            FileSystemManager fileSystemManager,
            ILogger<Program> logger,
            object? requestId,
            out JsonRpcResponse errorResponse,
            out string? validatedClientId,
            out string? validatedClientSessionId)
        {
            validatedClientId = null;
            validatedClientSessionId = null;

            // 校验 Content-Type（必须为 application/json，可带 charset）
            if (!IsValidJsonContentType(request.ContentType))
            {
                logger.LogWarning("Content-Type 校验失败: {ContentType}", request.ContentType);
                errorResponse = CreateErrorResponse(
                    -32600,
                    "invalid_request",
                    requestId,
                    new { reason = "invalid_content_type", received = request.ContentType });
                return false;
            }

            // 校验协议版本（必须是字符串 "1"）
            if (!request.Headers.TryGetValue("X-DevHub-Protocol", out var protocolValue) || string.IsNullOrWhiteSpace(protocolValue))
            {
                logger.LogWarning("协议版本头缺失");
                errorResponse = CreateErrorResponse(
                    -32099,
                    "not_supported",
                    requestId,
                    new { expected = 1, reason = "missing" });
                return false;
            }

            var protocol = protocolValue.ToString().Trim();
            if (!string.Equals(protocol, "1", StringComparison.Ordinal))
            {
                logger.LogWarning("协议版本校验失败，请求的版本: {ProtocolVersion}", protocol);
                errorResponse = CreateErrorResponse(
                    -32099,
                    "not_supported",
                    requestId,
                    new { expected = 1, received = protocol, reason = "mismatch" });
                return false;
            }

            logger.LogDebug("协议版本校验通过: {ProtocolVersion}", protocol);

            // 校验客户端 ID
            if (!request.Headers.TryGetValue("X-DevHub-ClientId", out var clientIdValue) ||
                string.IsNullOrWhiteSpace(clientIdValue))
            {
                logger.LogWarning("客户端ID校验失败");
                errorResponse = CreateErrorResponse(
                    -32600,
                    "invalid_request",
                    requestId,
                    new { reason = "missing_header", header = "X-DevHub-ClientId" });
                return false;
            }

            var clientId = clientIdValue.ToString().Trim();
            validatedClientId = clientId;
            logger.LogDebug("客户端ID校验通过: {ClientId}", clientId);

            // 校验会话 ID
            if (!request.Headers.TryGetValue("X-DevHub-ClientSessionId", out var sessionIdValue) ||
                string.IsNullOrWhiteSpace(sessionIdValue))
            {
                logger.LogWarning("会话ID校验失败");
                errorResponse = CreateErrorResponse(
                    -32600,
                    "invalid_request",
                    requestId,
                    new { reason = "missing_header", header = "X-DevHub-ClientSessionId" });
                return false;
            }

            var sessionId = sessionIdValue.ToString().Trim();
            if (!Guid.TryParseExact(sessionId, "D", out _))
            {
                logger.LogWarning("会话ID格式无效: {SessionId}", sessionId);
                errorResponse = CreateErrorResponse(
                    -32600,
                    "invalid_request",
                    requestId,
                    new { reason = "invalid_header", header = "X-DevHub-ClientSessionId" });
                return false;
            }

            validatedClientSessionId = sessionId;
            logger.LogDebug("会话ID校验通过: {SessionId}", sessionId);

            // 校验 Authorization 头
            if (!request.Headers.TryGetValue("Authorization", out var authorizationValue) ||
                string.IsNullOrWhiteSpace(authorizationValue) ||
                !authorizationValue.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Authorization头校验失败");
                errorResponse = CreateErrorResponse(
                    -32001,
                    "unauthorized",
                    requestId,
                    new { reason = "missing_token" });
                return false;
            }

            // 校验 token
            var token = authorizationValue.ToString().Substring("Bearer ".Length).Trim();
            try
            {
                var validToken = fileSystemManager.GetToken();
                if (token != validToken)
                {
                    logger.LogWarning("Token校验失败");
                    errorResponse = CreateErrorResponse(
                        -32001,
                        "unauthorized",
                        requestId,
                        new { reason = "invalid_token" });
                    return false;
                }

                logger.LogDebug("Token校验通过");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Token验证过程中发生异常");
                errorResponse = CreateErrorResponse(
                    -32001,
                    "unauthorized",
                    requestId,
                    new { reason = "invalid_token" });
                return false;
            }

            errorResponse = null!;
            return true;
        }

        /// <summary>
        /// 校验 Content-Type 是否为 application/json（允许附带 charset）
        /// </summary>
        private static bool IsValidJsonContentType(string? contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType))
            {
                return false;
            }

            var separatorIndex = contentType.IndexOf(';');
            var mediaType = separatorIndex >= 0
                ? contentType[..separatorIndex].Trim()
                : contentType.Trim();

            return string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 构建 JSON-RPC 请求模型并进行信封校验
        /// </summary>
        private static bool TryBuildRpcRequest(JsonElement root, out JsonRpcRequest request, out JsonRpcResponse errorResponse)
        {
            request = null!;

            var canUseRequestId = TryExtractRequestId(root, out var requestId);

            if (!root.TryGetProperty("jsonrpc", out var jsonRpcElement) ||
                jsonRpcElement.ValueKind != JsonValueKind.String ||
                !string.Equals(jsonRpcElement.GetString(), "2.0", StringComparison.Ordinal))
            {
                errorResponse = CreateErrorResponse(-32600, "invalid_request", canUseRequestId ? requestId : null);
                return false;
            }

            if (!root.TryGetProperty("method", out var methodElement) ||
                methodElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(methodElement.GetString()))
            {
                errorResponse = CreateErrorResponse(-32600, "invalid_request", canUseRequestId ? requestId : null);
                return false;
            }

            if (root.TryGetProperty("id", out var idElement))
            {
                if (!TryConvertJsonRpcId(idElement, out requestId))
                {
                    errorResponse = CreateErrorResponse(-32600, "invalid_request", null);
                    return false;
                }
            }

            object? requestParams = null;
            if (root.TryGetProperty("params", out var paramsElement))
            {
                if (paramsElement.ValueKind is not JsonValueKind.Object and not JsonValueKind.Array and not JsonValueKind.Null)
                {
                    errorResponse = CreateErrorResponse(-32600, "invalid_request", requestId);
                    return false;
                }

                requestParams = paramsElement.Clone();
            }

            request = new JsonRpcRequest
            {
                Id = requestId,
                Method = methodElement.GetString()!,
                Params = requestParams
            };

            errorResponse = null!;
            return true;
        }

        /// <summary>
        /// 尝试从原始请求提取可用于错误响应的请求ID
        /// </summary>
        private static bool TryExtractRequestId(JsonElement root, out object? requestId)
        {
            requestId = null;

            if (!root.TryGetProperty("id", out var idElement))
            {
                return false;
            }

            return TryConvertJsonRpcId(idElement, out requestId);
        }

        /// <summary>
        /// 将 JSON-RPC id 转换为可序列化对象
        /// </summary>
        private static bool TryConvertJsonRpcId(JsonElement idElement, out object? id)
        {
            switch (idElement.ValueKind)
            {
                case JsonValueKind.String:
                    id = idElement.GetString();
                    return true;
                case JsonValueKind.Number:
                    if (idElement.TryGetInt64(out var int64Value))
                    {
                        id = int64Value;
                        return true;
                    }

                    if (idElement.TryGetDouble(out var doubleValue))
                    {
                        id = doubleValue;
                        return true;
                    }

                    id = null;
                    return false;
                case JsonValueKind.Null:
                    id = null;
                    return false;
                default:
                    id = null;
                    return false;
            }
        }

        /// <summary>
        /// 判断是否 hub.* 方法且 params 为数组
        /// </summary>
        private static bool IsHubMethodParamsArray(JsonRpcRequest request)
        {
            if (!request.Method.StartsWith("hub.", StringComparison.Ordinal))
            {
                return false;
            }

            return request.Params is JsonElement paramsElement && paramsElement.ValueKind == JsonValueKind.Array;
        }

        /// <summary>
        /// 创建标准 JSON-RPC 错误响应
        /// </summary>
        private static JsonRpcResponse CreateErrorResponse(int code, string message, object? id, object? data = null)
        {
            return new JsonRpcResponse
            {
                Id = id,
                Error = new JsonRpcError
                {
                    Code = code,
                    Message = message,
                    Data = data
                }
            };
        }
    }
}
