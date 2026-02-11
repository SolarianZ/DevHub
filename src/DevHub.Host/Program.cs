using DevHub.Core.Extensions;
using DevHub.Core.Services;
using Serilog;
using System.Security.Principal;
using System.Text;

namespace DevHub.Host;

/// <summary>
/// DevHub Host 进程入口。
/// 仅负责单实例控制与应用装配。
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
        var mutexName = BuildSingleInstanceMutexName();
        _singleInstanceMutex = new Mutex(true, mutexName, out var createdNew);

        if (!createdNew)
        {
            Console.WriteLine("DevHub 已在运行中");
            return;
        }

        var runtimePathOptions = RuntimePathOptions.Resolve();
        Environment.SetEnvironmentVariable(RuntimePathOptions.LogDirEnvironmentVariable, runtimePathOptions.LogsPath);

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
            builder.Host.UseSerilog();
            builder.Services.AddAuthorization();
            builder.Services.AddOpenApi();
            builder.Services.AddDevHubCore(runtimePathOptions.DefinitionsPath);
            builder.Services.AddSingleton<HostBootstrapper>();
            builder.Services.AddSingleton<RpcHttpEndpointHandler>();
            builder.Services.AddSingleton<WebSocketSessionHandler>();

            var app = builder.Build();
            var logger = app.Services.GetRequiredService<ILogger<Program>>();
            logger.LogInformation("Web 应用程序构建完成，开始初始化系统...");

            var bootstrapper = app.Services.GetRequiredService<HostBootstrapper>();
            bootstrapper.Initialize();

            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi();
            }

            app.UseAuthorization();
            app.UseWebSockets();

            var currentPort = 0;

            app.Map("/ws", async (HttpContext context, WebSocketSessionHandler wsHandler, CancellationToken cancellationToken) =>
            {
                await wsHandler.HandleEndpointAsync(context, currentPort > 0 ? currentPort : null, cancellationToken);
            });

            app.MapPost("/rpc", async (HttpRequest request, RpcHttpEndpointHandler rpcHandler, CancellationToken cancellationToken) =>
            {
                return await rpcHandler.HandleAsync(request, currentPort > 0 ? currentPort : null, cancellationToken);
            });

            logger.LogDebug("启动服务器...");
            app.Urls.Add("http://127.0.0.1:0");

            app.Lifetime.ApplicationStarted.Register(() =>
            {
                if (bootstrapper.TryPersistHubRuntime(app.Urls, out var parsedPort))
                {
                    currentPort = parsedPort;
                }
            });

            app.Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "DevHub 启动过程中发生致命错误");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
