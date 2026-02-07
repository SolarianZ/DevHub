using DevHub.Core.Extensions;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Invocation;
using DevHub.Core.Services.Rpc;
using Microsoft.Extensions.Logging;
using Serilog;
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
        private const string MutexName = "Local\\DevHub_SingleInstance";

        /// <summary>
        /// 应用程序主入口。
        /// </summary>
        /// <param name="args">命令行参数。</param>
        public static void Main(string[] args)
        {
            #region 检查是否已有实例在运行

            bool createdNew;
            _singleInstanceMutex = new Mutex(true, MutexName, out createdNew);

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

                var currentPort = 0;

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
                    new { reason = "token_verification_failed" });
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
