
using DevHub.Core.Extensions;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Rpc;
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

            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddAuthorization();

            // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
            builder.Services.AddOpenApi();

            // Add DevHub core services
            var definitionsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevHub", "apps", "definitions");
            Directory.CreateDirectory(definitionsPath);
            builder.Services.AddDevHubCore(definitionsPath);

            var app = builder.Build();

            // 初始化文件系统
            var fileSystemManager = app.Services.GetRequiredService<FileSystemManager>();
            fileSystemManager.InitializeDirectories();

            // 加载应用程序定义
            var definitionLoader = app.Services.GetRequiredService<DefinitionLoader>();
            definitionLoader.Load();

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi();
            }

            app.UseAuthorization();

            // RPC endpoint
            app.MapPost("/rpc", async (HttpRequest request, RpcRouter rpcRouter, FileSystemManager fsManager, CancellationToken cancellationToken) =>
            {
                try
                {
                    // Read and parse JSON-RPC request
                    using var reader = new StreamReader(request.Body);
                    var body = await reader.ReadToEndAsync(cancellationToken);

                    var jsonOptions = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    };

                    var rpcRequest = JsonSerializer.Deserialize<JsonRpcRequest>(body, jsonOptions);

                    if (rpcRequest == null)
                    {
                        return Results.Json(new JsonRpcResponse
                        {
                            Error = new JsonRpcError
                            {
                                Code = -32600,
                                Message = "无效请求"
                            }
                        });
                    }

                    // 校验协议头
                    if (!ValidateHeaders(request, fsManager, out var errorResponse))
                    {
                        errorResponse.Id = rpcRequest.Id;
                        return Results.Json(errorResponse, jsonOptions);
                    }

                    // Route request
                    var response = await rpcRouter.RouteAsync(rpcRequest, cancellationToken);

                    return Results.Json(response, jsonOptions);
                }
                catch
                {
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

            // 启动服务器并获取实际监听端口
            var port = StartServerAndGetPort(app);

            // 写入 hub.json 文件
            fileSystemManager.WriteHubJson(port);

            Console.WriteLine($"DevHub 启动成功，监听端口: {port}");
            Console.WriteLine($"HTTP 地址: http://127.0.0.1:{port}");

            app.Run();
        }

        /// <summary>
        /// 校验 HTTP 请求头
        /// </summary>
        private static bool ValidateHeaders(HttpRequest request, FileSystemManager fileSystemManager, out JsonRpcResponse errorResponse)
        {
            // 校验协议版本
            if (!request.Headers.TryGetValue("X-DevHub-Protocol", out var protocolValue) ||
                !int.TryParse(protocolValue, out var protocolVersion) ||
                protocolVersion != 1)
            {
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

            // 校验客户端 ID
            if (!request.Headers.TryGetValue("X-DevHub-ClientId", out var clientIdValue) ||
                string.IsNullOrWhiteSpace(clientIdValue))
            {
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

            // 校验会话 ID
            if (!request.Headers.TryGetValue("X-DevHub-ClientSessionId", out var sessionIdValue) ||
                string.IsNullOrWhiteSpace(sessionIdValue))
            {
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

            // 校验 Authorization 头
            if (!request.Headers.TryGetValue("Authorization", out var authorizationValue) ||
                string.IsNullOrWhiteSpace(authorizationValue) ||
                !authorizationValue.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
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
            }
            catch
            {
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
        /// 启动服务器并获取实际监听端口
        /// </summary>
        private static int StartServerAndGetPort(WebApplication app)
        {
            // 动态分配端口
            var url = "http://127.0.0.1:0";
            app.Urls.Add(url);

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
                                portTask.TrySetResult(port);
                                return;
                            }
                        }
                    }

                    portTask.TrySetException(new InvalidOperationException("无法获取监听端口"));
                }
                catch (Exception ex)
                {
                    portTask.TrySetException(ex);
                }
            });

            // 在后台启动服务器
            var hostTask = app.RunAsync();

            // 等待获取端口
            if (portTask.Task.Wait(TimeSpan.FromSeconds(10)))
            {
                return portTask.Task.Result;
            }

            throw new TimeoutException("启动服务器超时");
        }
    }
}
