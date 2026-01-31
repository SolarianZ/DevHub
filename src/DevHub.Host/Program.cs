
using DevHub.Core.Extensions;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services.Rpc;
using System.Text.Json;

namespace DevHub.Host
{
    public class Program
    {
        public static void Main(string[] args)
        {
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

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi();
            }

            app.UseAuthorization();

            // RPC endpoint
            app.MapPost("/rpc", async (HttpRequest request, RpcRouter rpcRouter, CancellationToken cancellationToken) =>
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

                    // Route request
                    var response = await rpcRouter.RouteAsync(rpcRequest, cancellationToken);

                    return Results.Json(response, jsonOptions);
                }
                catch (Exception ex)
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

            app.Run();
        }
    }
}
