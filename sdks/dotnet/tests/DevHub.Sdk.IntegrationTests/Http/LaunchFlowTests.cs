using System.Text.Json;
using DevHub.Sdk.IntegrationTests.TestHost;
using DevHub.Sdk.Models;

namespace DevHub.Sdk.IntegrationTests.Http;

/// <summary>
/// Launch 黑盒测试。
/// </summary>
public sealed class LaunchFlowTests
{
    private const string InstancePassword = "sdk-launch-password";

    [Fact]
    public async Task Launch_ShouldCoverStartedStartingAndAlreadyRunning()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(CreateLaunchDefinition("launch.started.app"));
        await host.WriteDefinitionAsync(CreateLaunchDefinition("launch.starting.app"));
        await host.WriteDefinitionAsync(CreateLaunchDefinition("launch.running.app"));
        await host.WriteDefinitionAsync(CreateLaunchDefinition("launch.dedupe.app"));

        await using var client = await host.CreateClientAsync("launch-client");

        var started = await client.LaunchAsync(new LaunchRequest
        {
            AppId = "launch.started.app",
            Scope = string.Empty,
            WaitForRegisterMs = 0
        });
        Assert.Equal("started", started.Status);
        Assert.True(started.Pid > 0);

        var starting = await client.LaunchAsync(new LaunchRequest
        {
            AppId = "launch.starting.app",
            Scope = string.Empty,
            WaitForRegisterMs = 200
        });
        Assert.Equal("starting", starting.Status);
        Assert.True(starting.Pid > 0);

        var registered = await client.RegisterInstanceAsync(new AppInstanceRegistration
        {
            InstanceId = "launch-running-inst-1",
            AppId = "launch.running.app",
            Scope = string.Empty,
            Pid = Environment.ProcessId,
            Invoke = new InvokeCapability
            {
                Poll = true,
                Respond = true
            }
        }, InstancePassword);

        var alreadyRunning = await client.LaunchAsync(new LaunchRequest
        {
            AppId = "launch.running.app",
            Scope = string.Empty
        });
        Assert.Equal("already_running", alreadyRunning.Status);
        Assert.Equal(registered.Instance.Pid, alreadyRunning.Pid);

        var firstDedupeLaunch = await client.LaunchAsync(new LaunchRequest
        {
            AppId = "launch.dedupe.app",
            Scope = string.Empty,
            DedupeKey = "launch-dedupe-key",
            WaitForRegisterMs = 0
        });
        var secondDedupeLaunch = await client.LaunchAsync(new LaunchRequest
        {
            AppId = "launch.dedupe.app",
            Scope = string.Empty,
            DedupeKey = "launch-dedupe-key",
            WaitForRegisterMs = 0
        });

        Assert.Equal("started", firstDedupeLaunch.Status);
        Assert.True(firstDedupeLaunch.Pid > 0);
        Assert.Equal("already_running", secondDedupeLaunch.Status);
        Assert.Equal(firstDedupeLaunch.LaunchId, secondDedupeLaunch.LaunchId);
        Assert.Equal(firstDedupeLaunch.Pid, secondDedupeLaunch.Pid);
    }

    [Fact]
    public async Task Launch_WhenDefinitionMissingOrLaunchConfigMissing_ShouldMapExpectedErrors()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "launch.blank-config.app",
            Scope = string.Empty,
            DisplayName = "launch.blank-config.app",
            Launch = new LaunchConfiguration
            {
                ExePath = "   "
            }
        });

        await using var client = await host.CreateClientAsync("launch-error-client");

        var missingDefinition = await Assert.ThrowsAsync<DevHubRpcException>(() => client.LaunchAsync(new LaunchRequest
        {
            AppId = "launch.missing.app",
            Scope = string.Empty,
            WaitForRegisterMs = 0
        }));
        Assert.Equal(-32014, missingDefinition.Code);
        Assert.Equal("app_definition_not_found", missingDefinition.Message);
        Assert.Equal("launch.missing.app", (string?)missingDefinition.ErrorData!["appId"]);
        Assert.Equal(string.Empty, (string?)missingDefinition.ErrorData!["scope"]);

        var blankConfig = await Assert.ThrowsAsync<DevHubRpcException>(() => client.LaunchAsync(new LaunchRequest
        {
            AppId = "launch.blank-config.app",
            Scope = string.Empty,
            WaitForRegisterMs = 0
        }));
        Assert.Equal(-32020, blankConfig.Code);
        Assert.Equal("launch_failed", blankConfig.Message);
        Assert.Equal("launch_config_missing", blankConfig.Reason);
    }

    [Fact]
    public async Task Launch_WhenWaitingForRegister_ShouldSucceedWithMatchingLaunchId()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        var registerResultPath = Path.Combine(host.DataDirectory, "launch-wait-register-result.json");
        await host.WriteDefinitionAsync(CreateRegisterOnLaunchDefinition(
            "launch.wait-register.app",
            registerResultPath,
            scope: string.Empty,
            registrationScope: string.Empty,
            instanceId: "launch-wait-register-inst-1"));

        await using var client = await host.CreateClientAsync("launch-wait-register-client");

        var launchResult = await client.LaunchAsync(new LaunchRequest
        {
            AppId = "launch.wait-register.app",
            Scope = string.Empty,
            WaitForRegisterMs = 5000
        });

        Assert.Equal("started", launchResult.Status);
        Assert.False(string.IsNullOrWhiteSpace(launchResult.LaunchId));

        using var registrationDocument = JsonDocument.Parse(await WaitForFileTextAsync(registerResultPath));
        var registerResponse = registrationDocument.RootElement;
        Assert.Equal("launch.wait-register.app", registerResponse.GetProperty("result").GetProperty("instance").GetProperty("appId").GetString());
        Assert.Equal(string.Empty, registerResponse.GetProperty("result").GetProperty("instance").GetProperty("scope").GetString());
        Assert.Equal("launch-wait-register-inst-1", registerResponse.GetProperty("result").GetProperty("instance").GetProperty("instanceId").GetString());
    }

    [Fact]
    public async Task Launch_WhenWaitForRegisterPositiveAndNoRegistration_ShouldReturnStarting()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(CreateLongRunningLaunchDefinition("launch.timeout.app"));

        await using var client = await host.CreateClientAsync("launch-timeout-client");

        var result = await client.LaunchAsync(new LaunchRequest
        {
            AppId = "launch.timeout.app",
            Scope = string.Empty,
            WaitForRegisterMs = 1200
        });

        Assert.Equal("starting", result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.LaunchId));
        Assert.True(result.Pid > 0);
    }

    [Fact]
    public async Task Launch_WhenRegistrationScopeMismatchesLaunchScope_ShouldFailWithDefinitionScopeMismatch()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        var registerResultPath = Path.Combine(host.DataDirectory, "launch-scope-mismatch-result.json");
        await host.WriteDefinitionAsync(CreateRegisterOnLaunchDefinition(
            "launch.scope-match.app",
            registerResultPath,
            scope: "scope-a",
            registrationScope: "scope-b",
            instanceId: "launch-scope-mismatch-inst-1"));
        await host.WriteDefinitionAsync(CreateLaunchDefinition("launch.scope-match.app", scope: "scope-b"));

        await using var client = await host.CreateClientAsync("launch-scope-mismatch-client");

        var launchException = await Assert.ThrowsAsync<DevHubRpcException>(() => client.LaunchAsync(new LaunchRequest
        {
            AppId = "launch.scope-match.app",
            Scope = "scope-a",
            WaitForRegisterMs = 5000
        }));
        Assert.Equal(-32020, launchException.Code);
        Assert.Equal("launch_failed", launchException.Message);
        Assert.Equal("definition_scope_mismatch", launchException.Reason);
        Assert.Equal("launch.scope-match.app", (string?)launchException.ErrorData!["appId"]);
        Assert.Equal("scope-a", (string?)launchException.ErrorData!["expectedScope"]);
        Assert.Equal("scope-b", (string?)launchException.ErrorData!["scope"]);

        using var registrationDocument = JsonDocument.Parse(await WaitForFileTextAsync(registerResultPath));
        var registerResponse = registrationDocument.RootElement;
        Assert.Equal(-32002, registerResponse.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("forbidden", registerResponse.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("definition_scope_mismatch", registerResponse.GetProperty("error").GetProperty("data").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Launch_WhenUsingDefaultAndCustomDedupeTemplates_ShouldReturnResolvedDedupeKeys()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        var defaultCapturePath = Path.Combine(host.DataDirectory, "launch-default-dedupe-argv.json");
        var scopedCapturePath = Path.Combine(host.DataDirectory, "launch-custom-dedupe-argv.json");
        await host.WriteDefinitionAsync(CreateCaptureLaunchDefinition(
            "launch.default-dedupe.app",
            defaultCapturePath,
            scope: string.Empty,
            args:
            [
                "default",
                "{appId}",
                "{scope}",
                "{scopeOrGlobal}",
                "{httpBaseUrl}"
            ]));
        await host.WriteDefinitionAsync(CreateCaptureLaunchDefinition(
            "launch.custom-dedupe.app",
            scopedCapturePath,
            scope: "scope-a",
            args:
            [
                "custom",
                "{appId}",
                "{scope}",
                "{scopeOrGlobal}",
                "{unknown}"
            ],
            dedupeKeyTemplate: "{appId}:{scope}:{scopeOrGlobal}"));

        await using var client = await host.CreateClientAsync("launch-template-client");

        var defaultLaunch = await client.LaunchAsync(new LaunchRequest
        {
            AppId = "launch.default-dedupe.app",
            Scope = string.Empty,
            WaitForRegisterMs = 0
        });
        var scopedLaunch = await client.LaunchAsync(new LaunchRequest
        {
            AppId = "launch.custom-dedupe.app",
            Scope = "scope-a",
            WaitForRegisterMs = 0
        });

        Assert.Equal("launch.default-dedupe.app:global", defaultLaunch.DedupeKey);
        Assert.Equal("launch.custom-dedupe.app:scope-a:scope-a", scopedLaunch.DedupeKey);

        using var defaultDocument = JsonDocument.Parse(await WaitForFileTextAsync(defaultCapturePath));
        var defaultArgv = ReadArgv(defaultDocument.RootElement);
        Assert.Equal("default", defaultArgv[0]);
        Assert.Equal("launch.default-dedupe.app", defaultArgv[1]);
        Assert.Equal(string.Empty, defaultArgv[2]);
        Assert.Equal("global", defaultArgv[3]);
        Assert.StartsWith("http://", defaultArgv[4], StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(defaultDocument.RootElement.GetProperty("launchId").GetString()));

        using var scopedDocument = JsonDocument.Parse(await WaitForFileTextAsync(scopedCapturePath));
        var scopedArgv = ReadArgv(scopedDocument.RootElement);
        Assert.Equal(["custom", "launch.custom-dedupe.app", "scope-a", "scope-a", "{unknown}"], scopedArgv);
        Assert.False(string.IsNullOrWhiteSpace(scopedDocument.RootElement.GetProperty("launchId").GetString()));
    }

    [Fact]
    public async Task Launch_WhenArgsAndArgsTemplateBothPresent_ShouldUseStructuredArgs()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        var structuredCapturePath = Path.Combine(host.DataDirectory, "launch-structured-args.json");
        var templateCapturePath = Path.Combine(host.DataDirectory, "launch-ignored-template-args.json");
        var ignoredScriptPath = CreateCaptureArgvScript();
        await host.WriteDefinitionAsync(CreateCaptureLaunchDefinition(
            "launch.structured-args.app",
            structuredCapturePath,
            scope: "workspace-A",
            args:
            [
                "structured",
                "{appId}",
                "{scope}",
                "{scopeOrGlobal}",
                "{httpBaseUrl}"
            ],
            argsTemplate: $"\"{ignoredScriptPath}\" \"{templateCapturePath}\" template-should-be-ignored"));

        await using var client = await host.CreateClientAsync("launch-structured-args-client");

        _ = await client.LaunchAsync(new LaunchRequest
        {
            AppId = "launch.structured-args.app",
            Scope = "workspace-A",
            WaitForRegisterMs = 0
        });

        using var document = JsonDocument.Parse(await WaitForFileTextAsync(structuredCapturePath));
        var argv = ReadArgv(document.RootElement);
        Assert.Equal("structured", argv[0]);
        Assert.Equal("launch.structured-args.app", argv[1]);
        Assert.Equal("workspace-A", argv[2]);
        Assert.Equal("workspace-A", argv[3]);
        Assert.StartsWith("http://", argv[4], StringComparison.Ordinal);
        Assert.False(File.Exists(templateCapturePath));
    }

    [Fact]
    public async Task Launch_WhenArgsTemplateUsesQuotesAndEscapes_ShouldSplitIntoArgv()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        var scriptPath = CreateCaptureArgvScript();
        var capturePath = Path.Combine(host.DataDirectory, "launch-args-template-argv.json");
        const string windowsStylePath = @"C:\Program Files\DevHub\config.json";
        const string unquotedWindowsLiteral = @"C:\Temp\literal.txt";
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "launch.args-template.app",
            Scope = string.Empty,
            DisplayName = "launch.args-template.app",
            Launch = new LaunchConfiguration
            {
                ExePath = GetPythonExecutable(),
                ArgsTemplate = $"\"{scriptPath}\" \"{capturePath}\" --name \"hello world\" '--literal value' plain\\ value \"{windowsStylePath}\" {unquotedWindowsLiteral}"
            }
        });

        await using var client = await host.CreateClientAsync("launch-args-template-client");

        _ = await client.LaunchAsync(new LaunchRequest
        {
            AppId = "launch.args-template.app",
            Scope = string.Empty,
            WaitForRegisterMs = 0
        });

        using var document = JsonDocument.Parse(await WaitForFileTextAsync(capturePath));
        Assert.Equal(["--name", "hello world", "--literal value", "plain value", windowsStylePath, unquotedWindowsLiteral], ReadArgv(document.RootElement));
    }

    [Fact]
    public async Task Launch_WhenArgsTemplateInvalid_ShouldReturnInvalidParams()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "launch.invalid-args-template.app",
            Scope = string.Empty,
            DisplayName = "launch.invalid-args-template.app",
            Launch = new LaunchConfiguration
            {
                ExePath = GetPythonExecutable(),
                ArgsTemplate = "foo\\"
            }
        });

        await using var client = await host.CreateClientAsync("launch-invalid-template-client");

        var exception = await Assert.ThrowsAsync<DevHubRpcException>(() => client.LaunchAsync(new LaunchRequest
        {
            AppId = "launch.invalid-args-template.app",
            Scope = string.Empty,
            WaitForRegisterMs = 0
        }));
        Assert.Equal(-32602, exception.Code);
        Assert.Equal("invalid_params", exception.Message);
        Assert.Equal("invalid_launch_args_template", exception.Reason);
    }

    [Fact]
    public async Task Launch_WhenArgsTemplateQuoteUnterminated_ShouldReturnInvalidParams()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "launch.unterminated-quote-args-template.app",
            Scope = string.Empty,
            DisplayName = "launch.unterminated-quote-args-template.app",
            Launch = new LaunchConfiguration
            {
                ExePath = GetPythonExecutable(),
                ArgsTemplate = "\"unterminated"
            }
        });

        await using var client = await host.CreateClientAsync("launch-unterminated-quote-template-client");

        var exception = await Assert.ThrowsAsync<DevHubRpcException>(() => client.LaunchAsync(new LaunchRequest
        {
            AppId = "launch.unterminated-quote-args-template.app",
            Scope = string.Empty,
            WaitForRegisterMs = 0
        }));
        Assert.Equal(-32602, exception.Code);
        Assert.Equal("invalid_params", exception.Message);
        Assert.Equal("invalid_launch_args_template", exception.Reason);
    }

    private static AppDefinition CreateRegisterOnLaunchDefinition(
        string appId,
        string resultPath,
        string scope,
        string registrationScope,
        string instanceId)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"devhub-dotnet-sdk-register-on-launch-{Guid.NewGuid():N}.py");
        File.WriteAllText(
            scriptPath,
            """
import json
import os
import sys
import urllib.request

def send_rpc(data_dir, payload):
    with open(os.path.join(data_dir, "runtime", "hub.json"), "r", encoding="utf-8") as handle:
        hub = json.load(handle)
    with open(hub["tokenFile"], "r", encoding="utf-8") as handle:
        token = handle.read().strip()
    body = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    request = urllib.request.Request(
        f"{hub['httpBaseUrl']}/rpc",
        data=body,
        headers={
            "Content-Type": "application/json",
            "Authorization": f"Bearer {token}",
            "X-DevHub-Protocol": "1",
            "X-DevHub-ClientId": "DotNetSdkLaunchScript",
            "X-DevHub-ClientSessionId": "00000000-0000-0000-0000-000000000131"
        },
        method="POST"
    )
    with urllib.request.urlopen(request, timeout=10) as response:
        return json.loads(response.read().decode("utf-8"))

def main():
    app_id, scope, instance_id, result_path = sys.argv[1:5]
    data_dir = os.environ["DEVHUB_DATA_DIR"]
    launch_id = os.environ.get("DEVHUB_LAUNCH_ID")
    payload = {
        "jsonrpc": "2.0",
        "id": "dotnet-sdk-launch-register",
        "method": "hub.apps.registerInstance",
        "params": {
            "password": "sdk-launch-password",
            "launchId": launch_id,
            "instance": {
                "instanceId": instance_id,
                "appId": app_id,
                "scope": scope,
                "pid": os.getpid(),
                "invoke": {
                    "poll": True,
                    "respond": True
                }
            }
        }
    }
    response = send_rpc(data_dir, payload)
    with open(result_path, "w", encoding="utf-8") as handle:
        json.dump(response, handle, ensure_ascii=False)
    import time
    time.sleep(3)
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
""");

        return new AppDefinition
        {
            AppId = appId,
            Scope = scope,
            DisplayName = appId,
            Launch = new LaunchConfiguration
            {
                ExePath = GetPythonExecutable(),
                Args =
                [
                    scriptPath,
                    appId,
                    registrationScope,
                    instanceId,
                    resultPath
                ]
            }
        };
    }

    private static AppDefinition CreateCaptureLaunchDefinition(
        string appId,
        string capturePath,
        string scope,
        List<string> args,
        string? dedupeKeyTemplate = null,
        string? argsTemplate = null)
    {
        var scriptPath = CreateCaptureArgvScript();
        return new AppDefinition
        {
            AppId = appId,
            Scope = scope,
            DisplayName = appId,
            Launch = new LaunchConfiguration
            {
                ExePath = GetPythonExecutable(),
                Args = [scriptPath, capturePath, .. args],
                ArgsTemplate = argsTemplate,
                DedupeKeyTemplate = dedupeKeyTemplate
            }
        };
    }

    private static string CreateCaptureArgvScript()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"devhub-dotnet-sdk-capture-argv-{Guid.NewGuid():N}.py");
        File.WriteAllText(
            scriptPath,
            """
import json
import os
import pathlib
import sys
import time

payload = {
    "argv": sys.argv[2:],
    "launchId": os.environ.get("DEVHUB_LAUNCH_ID")
}
pathlib.Path(sys.argv[1]).write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
time.sleep(2)
""");
        return scriptPath;
    }

    private static AppDefinition CreateLaunchDefinition(string appId, string scope = "")
    {
        if (OperatingSystem.IsWindows())
        {
            return new AppDefinition
            {
                AppId = appId,
                Scope = scope,
                DisplayName = appId,
                Launch = new LaunchConfiguration
                {
                    ExePath = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                    ArgsTemplate = "-NoProfile -Command Start-Sleep -Seconds 5"
                }
            };
        }

        return new AppDefinition
        {
            AppId = appId,
            Scope = scope,
            DisplayName = appId,
            Launch = new LaunchConfiguration
            {
                ExePath = "/bin/sh",
                ArgsTemplate = "-c \"sleep 5\""
            }
        };
    }

    private static AppDefinition CreateLongRunningLaunchDefinition(string appId, string scope = "")
    {
        if (OperatingSystem.IsWindows())
        {
            return new AppDefinition
            {
                AppId = appId,
                Scope = scope,
                DisplayName = appId,
                Launch = new LaunchConfiguration
                {
                    ExePath = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                    ArgsTemplate = "-NoProfile -Command Start-Sleep -Seconds 7"
                }
            };
        }

        return new AppDefinition
        {
            AppId = appId,
            Scope = scope,
            DisplayName = appId,
            Launch = new LaunchConfiguration
            {
                ExePath = "/bin/sh",
                ArgsTemplate = "-c \"sleep 7\""
            }
        };
    }

    private static string GetPythonExecutable()
    {
        return Environment.GetEnvironmentVariable("PYTHON") switch
        {
            { Length: > 0 } configured => configured,
            _ when OperatingSystem.IsWindows() => "python",
            _ => "python3"
        };
    }

    private static async Task<string> WaitForFileTextAsync(string path, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                return await File.ReadAllTextAsync(path);
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"未能在限定时间内读取文件：{path}");
    }

    private static List<string> ReadArgv(JsonElement root)
    {
        return root.GetProperty("argv").EnumerateArray().Select(static item => item.GetString() ?? string.Empty).ToList();
    }
}
