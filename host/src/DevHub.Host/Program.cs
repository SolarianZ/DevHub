using DevHub.Core.Services;
using DevHub.Host.Extensions;
using DevHub.Host.Transport;
using Serilog;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace DevHub.Host;

/// <summary>
/// DevHub Host 进程入口。
/// 仅负责单实例控制与应用装配。
/// </summary>
public class Program
{
    private const int SuccessExitCode = 0;
    private const int FatalStartupExitCode = 1;
    private static Mutex? _singleInstanceMutex;
    private const string SerilogFileSinkPathKey = "Serilog:WriteTo:1:Args:path";

    /// <summary>
    /// 构建当前用户 + 数据根目录维度的单实例互斥量名称。
    /// </summary>
    private static string BuildSingleInstanceMutexName(RuntimePathOptions runtimePathOptions)
    {
        var userKey = ResolveCurrentUserKey();
        var dataDirectoryKey = BuildDataDirectoryKey(runtimePathOptions.RootPath);
        return $"Local\\DevHub_{userKey}_{dataDirectoryKey}";
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
    /// 基于规范化数据根目录构建稳定的锁键。
    /// </summary>
    /// <param name="rootPath">规范化后的数据根目录。</param>
    /// <returns>可用于命名系统互斥量的稳定键。</returns>
    private static string BuildDataDirectoryKey(string rootPath)
    {
        var normalizedRootPath = OperatingSystem.IsWindows()
            ? rootPath.ToUpperInvariant()
            : rootPath;

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRootPath));
        return Convert.ToHexString(hashBytes);
    }

    /// <summary>
    /// 应用程序主入口。
    /// </summary>
    /// <param name="args">命令行参数。</param>
    /// <returns>进程退出码。</returns>
    public static int Main(string[] args)
    {
        var runtimePathOptions = RuntimePathOptions.Resolve();
        var mutexName = BuildSingleInstanceMutexName(runtimePathOptions);
        _singleInstanceMutex = new Mutex(true, mutexName, out var createdNew);

        if (!createdNew)
        {
            Console.WriteLine("DevHub 已在运行中");
            return SuccessExitCode;
        }

        var contentRootPath = ResolveContentRootPath();

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(BuildBootstrapConfiguration(contentRootPath, runtimePathOptions.LogsPath))
            .CreateLogger();

        try
        {
            Log.Information("DevHub 启动初始化...");
            var hubVersion = HostVersionProvider.ResolveHubVersion();

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = contentRootPath
            });

            builder.Host.UseSerilog();
            builder.Services.AddAuthorization();
            builder.Services.AddOpenApi();
            builder.Services.AddDevHubHost(runtimePathOptions, hubVersion);

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

            app.Map("/ws", async (HttpContext context, WebSocketSessionHandler wsHandler, CancellationToken cancellationToken) =>
            {
                await wsHandler.HandleEndpointAsync(context, cancellationToken);
            });

            app.MapMethods("/rpc", [HttpMethods.Options], static (HttpRequest request) =>
            {
                return RpcHttpCorsPolicy.CreatePreflightResponse(request);
            });

            app.MapPost("/rpc", async (HttpRequest request, RpcHttpEndpointHandler rpcHandler, CancellationToken cancellationToken) =>
            {
                return await rpcHandler.HandleAsync(request, cancellationToken);
            });

            logger.LogDebug("启动服务器...");
            app.Urls.Add("http://127.0.0.1:0");

            app.Lifetime.ApplicationStarted.Register(() =>
            {
                PersistHubRuntimeOrStop(
                    () => bootstrapper.TryPersistHubRuntime(app.Urls, out _),
                    app.Lifetime.StopApplication,
                    logger);
            });

            app.Lifetime.ApplicationStopped.Register(bootstrapper.Cleanup);

            app.Run();
            return SuccessExitCode;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "DevHub 启动过程中发生致命错误");
            return FatalStartupExitCode;
        }
        finally
        {
            Log.CloseAndFlush();
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
        }
    }

    /// <summary>
    /// 解析 Host 的内容根目录。
    /// </summary>
    /// <returns>可用于加载配置文件的内容根目录。</returns>
    internal static string ResolveContentRootPath()
    {
        var baseDirectory = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return Directory.GetCurrentDirectory();
        }

        return Path.GetFullPath(baseDirectory);
    }

    /// <summary>
    /// 构建启动阶段使用的配置对象。
    /// </summary>
    /// <param name="contentRootPath">内容根目录。</param>
    /// <param name="logsPath">运行时日志目录。</param>
    /// <returns>可供日志初始化使用的配置。</returns>
    internal static IConfigurationRoot BuildBootstrapConfiguration(string contentRootPath, string logsPath)
    {
        return new ConfigurationBuilder()
            .SetBasePath(contentRootPath)
            .AddJsonFile("appsettings.json")
            .AddEnvironmentVariables()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SerilogFileSinkPathKey] = Path.Combine(logsPath, "devhub-.log")
            })
            .Build();
    }

    /// <summary>
    /// 在应用启动后持久化运行时发现文件；失败时主动停止应用，避免进入不可发现状态。
    /// </summary>
    /// <param name="tryPersistHubRuntime">执行运行时发现文件持久化的委托。</param>
    /// <param name="stopApplication">停止应用的回调。</param>
    /// <param name="logger">日志记录器。</param>
    internal static void PersistHubRuntimeOrStop(
        Func<bool> tryPersistHubRuntime,
        Action stopApplication,
        Microsoft.Extensions.Logging.ILogger logger)
    {
        try
        {
            if (tryPersistHubRuntime())
            {
                return;
            }

            logger.LogCritical("Hub 运行时发现文件持久化失败，Host 将停止运行以避免客户端发现错误。");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Hub 运行时发现文件持久化失败，Host 将停止运行以避免客户端发现错误。");
        }

        stopApplication();
    }
}
