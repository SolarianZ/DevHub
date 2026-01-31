
using DevHub.Core.Extensions;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Rpc;
using Microsoft.Extensions.Logging;
using Serilog;
using System.Text.Json;
using System.Threading;

namespace DevHub.Host
{
    public class Program
    {
        private static Mutex? _singleInstanceMutex;
        private const string MutexName = "Local\\DevHub_SingleInstance";

        public static void Main(string[] args)
        {
            // 检查是否已有实例在运行
            bool createdNew;
            _singleInstanceMutex = new Mutex(true, MutexName, out createdNew);

            if (!createdNew)
            {
                // 已有实例在运行，直接退出
                Console.WriteLine("DevHub 已在运行中");
                return;
            }

            // 配置 Serilog
            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json")
                    .AddEnvironmentVariables()
                    .Build())
                .CreateLogger();

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
                var definitionsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevHub", "apps", "definitions");
                Directory.CreateDirectory(definitionsPath);
                Log.Debug("应用程序定义目录: {DefinitionsPath}", definitionsPath);
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

                // 加载应用程序定义
                var definitionLoader = app.Services.GetRequiredService<DefinitionLoader>();
                logger.LogDebug("加载应用程序定义...");
                definitionLoader.Load();
                logger.LogInformation("应用程序定义加载完成");

                // Configure the HTTP request pipeline.
                if (app.Environment.IsDevelopment())
                {
                    app.MapOpenApi();
                }

                app.UseAuthorization();

                // RPC endpoint
                app.MapPost("/rpc", async (HttpRequest request, RpcRouter rpcRouter, FileSystemManager fsManager, ILogger<Program> endpointLogger, CancellationToken cancellationToken) =>
                {
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    string? clientId = null;
                    object? requestId = null;
                    string? method = null;

                    try
                    {
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

                        var rpcRequest = JsonSerializer.Deserialize<JsonRpcRequest>(body, jsonOptions);

                        if (rpcRequest == null)
                        {
                            endpointLogger.LogWarning("收到无效的JSON-RPC请求，客户端ID: {ClientId}，请求体为空或格式不正确", clientId);
                            return Results.Json(new JsonRpcResponse
                            {
                                Error = new JsonRpcError
                                {
                                    Code = -32600,
                                    Message = "无效请求"
                                }
                            });
                        }

                        requestId = rpcRequest.Id;
                        method = rpcRequest.Method;

                        endpointLogger.LogInformation("处理RPC请求: {Method}, RequestId: {RequestId}, ClientId: {ClientId}",
                            rpcRequest.Method, rpcRequest.Id, clientId);

                        // 校验协议头
                        if (!ValidateHeaders(request, fsManager, endpointLogger, out var errorResponse))
                        {
                            errorResponse.Id = rpcRequest.Id;
                            endpointLogger.LogWarning("请求头校验失败，Method: {Method}, RequestId: {RequestId}, ClientId: {ClientId}, ErrorCode: {ErrorCode}, ErrorMessage: {ErrorMessage}",
                                rpcRequest.Method, rpcRequest.Id, clientId, errorResponse.Error?.Code, errorResponse.Error?.Message);
                            return Results.Json(errorResponse, jsonOptions);
                        }

                        // Route request
                        var response = await rpcRouter.RouteAsync(rpcRequest, cancellationToken);

                        stopwatch.Stop();
                        endpointLogger.LogInformation("RPC请求处理成功: {Method}, RequestId: {RequestId}, ClientId: {ClientId}, 处理时间: {ElapsedMilliseconds}ms",
                            rpcRequest.Method, rpcRequest.Id, clientId, stopwatch.ElapsedMilliseconds);

                        endpointLogger.LogDebug("RPC响应内容: {Response}", JsonSerializer.Serialize(response, jsonOptions));
                        return Results.Json(response, jsonOptions);
                    }
                    catch (Exception ex)
                    {
                        stopwatch.Stop();
                        logger.LogError(ex, "处理RPC请求时发生未捕获的异常，Method: {Method}, RequestId: {RequestId}, ClientId: {ClientId}, 处理时间: {ElapsedMilliseconds}ms",
                            method, requestId, clientId, stopwatch.ElapsedMilliseconds);
                        return Results.Json(new JsonRpcResponse
                        {
                            Error = new JsonRpcError
                            {
                                Code = -32603,
                                Message = "内部错误"
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
                var port = 0;
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
                                port = parsedPort;
                                logger.LogInformation("服务器成功启动，监听地址: {Address}", address);
                                logger.LogDebug("写入 hub.json 文件...");
                                fileSystemManager.WriteHubJson(port);
                                logger.LogInformation("DevHub 启动成功，监听端口: {Port}", port);
                                logger.LogInformation("HTTP 地址: http://127.0.0.1:{Port}", port);
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
        private static bool ValidateHeaders(HttpRequest request, FileSystemManager fileSystemManager, ILogger<Program> logger, out JsonRpcResponse errorResponse)
        {
            // 校验协议版本
            if (!request.Headers.TryGetValue("X-DevHub-Protocol", out var protocolValue) ||
                !int.TryParse(protocolValue, out var protocolVersion) ||
                protocolVersion != 1)
            {
                logger.LogWarning("协议版本校验失败，请求的版本: {ProtocolVersion}", protocolValue);
                errorResponse = new JsonRpcResponse
                {
                    Error = new JsonRpcError
                    {
                        Code = -32099,
                        Message = "不支持的协议版本"
                    }
                };
                return false;
            }

            logger.LogDebug("协议版本校验通过: {ProtocolVersion}", protocolVersion);

            // 校验客户端 ID
            if (!request.Headers.TryGetValue("X-DevHub-ClientId", out var clientIdValue) ||
                string.IsNullOrWhiteSpace(clientIdValue))
            {
                logger.LogWarning("客户端ID校验失败");
                errorResponse = new JsonRpcResponse
                {
                    Error = new JsonRpcError
                    {
                        Code = -32602,
                        Message = "缺少客户端 ID"
                    }
                };
                return false;
            }

            logger.LogDebug("客户端ID校验通过: {ClientId}", clientIdValue);

            // 校验会话 ID
            if (!request.Headers.TryGetValue("X-DevHub-ClientSessionId", out var sessionIdValue) ||
                string.IsNullOrWhiteSpace(sessionIdValue))
            {
                logger.LogWarning("会话ID校验失败");
                errorResponse = new JsonRpcResponse
                {
                    Error = new JsonRpcError
                    {
                        Code = -32602,
                        Message = "缺少会话 ID"
                    }
                };
                return false;
            }

            logger.LogDebug("会话ID校验通过: {SessionId}", sessionIdValue);

            // 校验 Authorization 头
            if (!request.Headers.TryGetValue("Authorization", out var authorizationValue) ||
                string.IsNullOrWhiteSpace(authorizationValue) ||
                !authorizationValue.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Authorization头校验失败");
                errorResponse = new JsonRpcResponse
                {
                    Error = new JsonRpcError
                    {
                        Code = -32001,
                        Message = "未授权"
                    }
                };
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
                    errorResponse = new JsonRpcResponse
                    {
                        Error = new JsonRpcError
                        {
                            Code = -32001,
                            Message = "无效的 token"
                        }
                    };
                    return false;
                }

                logger.LogDebug("Token校验通过");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Token验证过程中发生异常");
                errorResponse = new JsonRpcResponse
                {
                    Error = new JsonRpcError
                    {
                        Code = -32001,
                        Message = "token 验证失败"
                    }
                };
                return false;
            }

            errorResponse = null!;
            return true;
        }

        /// <summary>
        /// 获取服务器监听端口
        /// </summary>
        private static int GetServerPort(WebApplication app, ILogger<Program> logger)
        {
            // 动态分配端口
            var url = "http://127.0.0.1:0";
            app.Urls.Add(url);

            logger.LogDebug("服务器将监听地址: {Url}", url);

            // 创建一个任务来获取实际端口
            var portTask = new TaskCompletionSource<int>();

            app.Lifetime.ApplicationStarted.Register(() =>
            {
                try
                {
                    var addresses = app.Urls;
                    foreach (var address in addresses)
                    {
                        if (address.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase) ||
                            address.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase))
                        {
                            var portStartIndex = address.LastIndexOf(':') + 1;
                            var portStr = address.Substring(portStartIndex);
                            if (int.TryParse(portStr, out var port))
                            {
                                logger.LogInformation("服务器成功启动，监听地址: {Address}", address);
                                portTask.TrySetResult(port);
                                return;
                            }
                        }
                    }

                    var errorMsg = "无法从监听地址中解析出有效端口";
                    logger.LogError(errorMsg);
                    portTask.TrySetException(new InvalidOperationException(errorMsg));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "服务器启动过程中发生异常");
                    portTask.TrySetException(ex);
                }
            });

            return portTask.Task.Result;
        }
    }
}
