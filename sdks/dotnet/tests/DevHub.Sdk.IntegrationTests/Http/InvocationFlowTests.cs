using System.Text.Json;
using DevHub.Sdk.IntegrationTests.TestHost;
using DevHub.Sdk.Models;

namespace DevHub.Sdk.IntegrationTests.Http;

/// <summary>
/// Invocation 黑盒测试。
/// </summary>
public sealed class InvocationFlowTests
{
    private const string InstancePassword = "sdk-invocation-password";

    [Fact]
    public async Task NotifyAndPoll_ShouldRoundTrip()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.notify.app",
            Scope = string.Empty,
            DisplayName = "invoke.notify.app"
        });

        await using var client = await host.CreateClientAsync("invoke-notify-client");
        var registered = await RegisterInstanceAsync(client, "invoke.notify.app", "notify-inst-1", scope: string.Empty);

        var notifyResult = await client.NotifyAsync(new InvokeRequest
        {
            AppId = "invoke.notify.app",
            Method = "test.notify",
            Args = new { message = "hello" },
            Target = new InvocationTarget
            {
                Scope = string.Empty
            }
        });

        Assert.True(notifyResult.Ok);
        var invocation = await WaitForSingleInvocationAsync(client, registered.Instance.InstanceId, registered.InstanceSessionToken);
        Assert.Equal(notifyResult.InvocationId, invocation.InvocationId);
        Assert.Equal(InvocationKind.Notify, invocation.Kind);
        Assert.Equal("hello", invocation.Args!.Value.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Poll_WithMaxCount_ShouldLimitReturnedItems()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.poll-max.app",
            Scope = string.Empty,
            DisplayName = "invoke.poll-max.app"
        });

        await using var client = await host.CreateClientAsync("invoke-poll-max-client");
        var registered = await RegisterInstanceAsync(client, "invoke.poll-max.app", "poll-max-inst-1", scope: string.Empty);

        var firstNotify = await client.NotifyAsync(new InvokeRequest
        {
            AppId = "invoke.poll-max.app",
            Method = "test.poll.max.1",
            Target = new InvocationTarget
            {
                Scope = string.Empty
            }
        });
        var secondNotify = await client.NotifyAsync(new InvokeRequest
        {
            AppId = "invoke.poll-max.app",
            Method = "test.poll.max.2",
            Target = new InvocationTarget
            {
                Scope = string.Empty
            }
        });

        var firstPoll = await client.PollAsync(new PollRequest
        {
            InstanceId = registered.Instance.InstanceId,
            InstanceSessionToken = registered.InstanceSessionToken,
            MaxCount = 1,
            WaitMs = 0
        });
        Assert.Single(firstPoll.Items);

        var secondPoll = await client.PollAsync(new PollRequest
        {
            InstanceId = registered.Instance.InstanceId,
            InstanceSessionToken = registered.InstanceSessionToken,
            MaxCount = 10,
            WaitMs = 0
        });
        Assert.Single(secondPoll.Items);

        Assert.Equal(
            new[] { firstNotify.InvocationId, secondNotify.InvocationId }.OrderBy(static item => item, StringComparer.Ordinal),
            firstPoll.Items.Concat(secondPoll.Items).Select(item => item.InvocationId).OrderBy(static item => item, StringComparer.Ordinal));
    }

    [Fact]
    public async Task RequestRespondValue_ShouldReturnResult_AndSecondRespondShouldConflict()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.request.app",
            Scope = string.Empty,
            DisplayName = "invoke.request.app"
        });

        await using var client = await host.CreateClientAsync("invoke-request-client");
        var registered = await RegisterInstanceAsync(client, "invoke.request.app", "request-inst-1", scope: string.Empty);

        var requestTask = client.RequestAsync(new InvokeRequest
        {
            AppId = "invoke.request.app",
            Method = "test.request",
            Target = new InvocationTarget
            {
                Scope = string.Empty
            },
            Args = new { input = 1 },
            Options = new InvocationOptions
            {
                TtlMs = 5000,
                WaitTimeoutMs = 3000
            }
        });

        var invocation = await WaitForSingleInvocationAsync(client, registered.Instance.InstanceId, registered.InstanceSessionToken);

        await client.RespondAsync(new RespondRequest
        {
            InstanceId = registered.Instance.InstanceId,
            InstanceSessionToken = registered.InstanceSessionToken,
            InvocationId = invocation.InvocationId,
            LeaseToken = invocation.Delivery!.LeaseToken,
            Value = new { ok = true, value = 2 }
        });

        var requestResult = await requestTask;
        Assert.True(requestResult.Ok);
        Assert.Equal(2, requestResult.Value!.Value.GetProperty("value").GetInt32());

        var conflict = await Assert.ThrowsAsync<DevHubRpcException>(() => client.RespondAsync(new RespondRequest
        {
            InstanceId = registered.Instance.InstanceId,
            InstanceSessionToken = registered.InstanceSessionToken,
            InvocationId = invocation.InvocationId,
            LeaseToken = invocation.Delivery!.LeaseToken,
            Value = new { ok = true }
        }));
        Assert.Equal(-32030, conflict.Code);
    }

    [Fact]
    public async Task RequestRespondNullValue_ShouldReturnJsonNull()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.request.null.app",
            Scope = string.Empty,
            DisplayName = "invoke.request.null.app"
        });

        await using var client = await host.CreateClientAsync("invoke-request-null-client");
        var registered = await RegisterInstanceAsync(client, "invoke.request.null.app", "request-null-inst-1", scope: string.Empty);

        var requestTask = client.RequestAsync(new InvokeRequest
        {
            AppId = "invoke.request.null.app",
            Method = "test.request.null",
            Target = new InvocationTarget
            {
                Scope = string.Empty
            },
            Options = new InvocationOptions
            {
                TtlMs = 5000,
                WaitTimeoutMs = 3000
            }
        });

        var invocation = await WaitForSingleInvocationAsync(client, registered.Instance.InstanceId, registered.InstanceSessionToken);

        await client.RespondAsync(new RespondRequest
        {
            InstanceId = registered.Instance.InstanceId,
            InstanceSessionToken = registered.InstanceSessionToken,
            InvocationId = invocation.InvocationId,
            LeaseToken = invocation.Delivery!.LeaseToken,
            Value = null
        });

        var requestResult = await requestTask;
        Assert.True(requestResult.Ok);
        Assert.Null(requestResult.Value);
    }

    [Fact]
    public async Task RequestRespondError_ShouldMapInvocationFailed()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.error.app",
            Scope = string.Empty,
            DisplayName = "invoke.error.app"
        });

        await using var client = await host.CreateClientAsync("invoke-error-client");
        var registered = await RegisterInstanceAsync(client, "invoke.error.app", "error-inst-1", scope: string.Empty);

        var requestTask = client.RequestAsync(new InvokeRequest
        {
            AppId = "invoke.error.app",
            Method = "test.request",
            Target = new InvocationTarget
            {
                Scope = string.Empty
            },
            Options = new InvocationOptions
            {
                TtlMs = 5000,
                WaitTimeoutMs = 3000
            }
        });

        var invocation = await WaitForSingleInvocationAsync(client, registered.Instance.InstanceId, registered.InstanceSessionToken);

        await client.RespondAsync(new RespondRequest
        {
            InstanceId = registered.Instance.InstanceId,
            InstanceSessionToken = registered.InstanceSessionToken,
            InvocationId = invocation.InvocationId,
            LeaseToken = invocation.Delivery!.LeaseToken,
            Error = DevHubCalleeError.Create(1001, "app_error", new { reason = "boom" })
        });

        var exception = await Assert.ThrowsAsync<DevHubRpcException>(() => requestTask);
        Assert.Equal(-32050, exception.Code);
        Assert.Equal(invocation.InvocationId, exception.InvocationId);
        Assert.NotNull(exception.CalleeError);
        Assert.Equal(1001, exception.CalleeError!.Code);
        Assert.Equal("app_error", exception.CalleeError.Message);
        Assert.Equal(1001, exception.ErrorData!.Value.GetProperty("calleeError").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task RequestTimeoutAndExpired_ShouldMapExpectedErrors()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.timeout.app",
            Scope = string.Empty,
            DisplayName = "invoke.timeout.app"
        });

        await using var client = await host.CreateClientAsync("invoke-timeout-client");
        _ = await RegisterInstanceAsync(client, "invoke.timeout.app", "timeout-inst-1", scope: string.Empty);

        var timeoutException = await Assert.ThrowsAsync<DevHubRpcException>(() => client.RequestAsync(new InvokeRequest
        {
            AppId = "invoke.timeout.app",
            Method = "test.timeout",
            Target = new InvocationTarget
            {
                Scope = string.Empty
            },
            Options = new InvocationOptions
            {
                TtlMs = 1500,
                WaitTimeoutMs = 1000
            }
        }));
        Assert.Equal(-32012, timeoutException.Code);

        var expiredException = await Assert.ThrowsAsync<DevHubRpcException>(() => client.RequestAsync(new InvokeRequest
        {
            AppId = "invoke.timeout.app",
            Method = "test.expired",
            Target = new InvocationTarget
            {
                Scope = string.Empty
            },
            Options = new InvocationOptions
            {
                TtlMs = 1000,
                WaitTimeoutMs = 1000
            }
        }));
        Assert.Equal(-32011, expiredException.Code);
    }

    [Fact]
    public async Task ScopeRules_ShouldRouteToExpectedInstance()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.scope.app",
            Scope = string.Empty,
            DisplayName = "invoke.scope.app"
        });
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.scope.app",
            Scope = "scope-a",
            DisplayName = "invoke.scope.app.scope-a"
        });
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.scope.app",
            Scope = "global",
            DisplayName = "invoke.scope.app.literal-global"
        });

        await using var client = await host.CreateClientAsync("invoke-scope-client");
        var globalInstance = await RegisterInstanceAsync(client, "invoke.scope.app", "scope-global-inst", scope: string.Empty);
        var scopeAInstance = await RegisterInstanceAsync(client, "invoke.scope.app", "scope-a-inst", scope: "scope-a");
        var literalGlobalInstance = await RegisterInstanceAsync(client, "invoke.scope.app", "scope-literal-global-inst", scope: "global");

        _ = await client.NotifyAsync(new InvokeRequest
        {
            AppId = "invoke.scope.app",
            Method = "test.default-global",
            Target = new InvocationTarget { Scope = string.Empty }
        });
        _ = await WaitForSingleInvocationAsync(client, globalInstance.Instance.InstanceId, globalInstance.InstanceSessionToken);
        Assert.Empty((await client.PollAsync(new PollRequest
        {
            InstanceId = scopeAInstance.Instance.InstanceId,
            InstanceSessionToken = scopeAInstance.InstanceSessionToken,
            WaitMs = 0
        })).Items);

        _ = await client.NotifyAsync(new InvokeRequest
        {
            AppId = "invoke.scope.app",
            Method = "test.scope-a",
            Target = new InvocationTarget { Scope = "scope-a" }
        });
        _ = await WaitForSingleInvocationAsync(client, scopeAInstance.Instance.InstanceId, scopeAInstance.InstanceSessionToken);

        _ = await client.NotifyAsync(new InvokeRequest
        {
            AppId = "invoke.scope.app",
            Method = "test.empty-scope",
            Target = new InvocationTarget { Scope = string.Empty }
        });
        _ = await WaitForSingleInvocationAsync(client, globalInstance.Instance.InstanceId, globalInstance.InstanceSessionToken);

        _ = await client.NotifyAsync(new InvokeRequest
        {
            AppId = "invoke.scope.app",
            Method = "test.literal-global",
            Target = new InvocationTarget { Scope = "global" }
        });
        _ = await WaitForSingleInvocationAsync(client, literalGlobalInstance.Instance.InstanceId, literalGlobalInstance.InstanceSessionToken);
    }

    [Fact]
    public async Task TargetInstanceId_ShouldRouteOnlyToSpecifiedInstance_AndMissingTargetShouldReturnReason()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.target-instance.app",
            Scope = "scope-a",
            DisplayName = "invoke.target-instance.app.scope-a"
        });

        await using var client = await host.CreateClientAsync("invoke-target-instance-client");
        var targetInstance = await RegisterInstanceAsync(client, "invoke.target-instance.app", "target-instance-inst-1", scope: "scope-a");
        var peerInstance = await RegisterInstanceAsync(client, "invoke.target-instance.app", "target-instance-inst-2", scope: "scope-a");

        var notifyResult = await client.NotifyAsync(new InvokeRequest
        {
            AppId = "invoke.target-instance.app",
            Method = "test.target.notify",
            Target = new InvocationTarget
            {
                Scope = "scope-a",
                InstanceId = targetInstance.Instance.InstanceId
            },
            Options = new InvocationOptions
            {
                AutoLaunch = false,
                QueueIfOffline = false
            }
        });

        Assert.True(notifyResult.Ok);
        var notifyInvocation = await WaitForSingleInvocationAsync(client, targetInstance.Instance.InstanceId, targetInstance.InstanceSessionToken);
        Assert.Equal(notifyResult.InvocationId, notifyInvocation.InvocationId);
        Assert.Empty((await client.PollAsync(new PollRequest
        {
            InstanceId = peerInstance.Instance.InstanceId,
            InstanceSessionToken = peerInstance.InstanceSessionToken,
            WaitMs = 0
        })).Items);

        var requestTask = client.RequestAsync(new InvokeRequest
        {
            AppId = "invoke.target-instance.app",
            Method = "test.target.request",
            Target = new InvocationTarget
            {
                Scope = "scope-a",
                InstanceId = targetInstance.Instance.InstanceId
            },
            Options = new InvocationOptions
            {
                TtlMs = 5000,
                WaitTimeoutMs = 3000,
                AutoLaunch = false,
                QueueIfOffline = false
            }
        });

        var requestInvocation = await WaitForSingleInvocationAsync(client, targetInstance.Instance.InstanceId, targetInstance.InstanceSessionToken);
        Assert.Empty((await client.PollAsync(new PollRequest
        {
            InstanceId = peerInstance.Instance.InstanceId,
            InstanceSessionToken = peerInstance.InstanceSessionToken,
            WaitMs = 0
        })).Items);

        await client.RespondAsync(new RespondRequest
        {
            InstanceId = targetInstance.Instance.InstanceId,
            InstanceSessionToken = targetInstance.InstanceSessionToken,
            InvocationId = requestInvocation.InvocationId,
            LeaseToken = requestInvocation.Delivery!.LeaseToken,
            Value = new { ok = true, routed = targetInstance.Instance.InstanceId }
        });

        var requestResult = await requestTask;
        Assert.True(requestResult.Ok);
        Assert.Equal(targetInstance.Instance.InstanceId, requestResult.Value!.Value.GetProperty("routed").GetString());

        var missingNotifyException = await Assert.ThrowsAsync<DevHubRpcException>(() => client.NotifyAsync(new InvokeRequest
        {
            AppId = "invoke.target-instance.app",
            Method = "test.target.notify.missing",
            Target = new InvocationTarget
            {
                Scope = "scope-a",
                InstanceId = "target-instance-missing"
            },
            Options = new InvocationOptions
            {
                AutoLaunch = false,
                QueueIfOffline = false
            }
        }));
        Assert.Equal(-32010, missingNotifyException.Code);
        Assert.Equal("target_instance_missing", missingNotifyException.Reason);

        var missingRequestException = await Assert.ThrowsAsync<DevHubRpcException>(() => client.RequestAsync(new InvokeRequest
        {
            AppId = "invoke.target-instance.app",
            Method = "test.target.request.missing",
            Target = new InvocationTarget
            {
                Scope = "scope-a",
                InstanceId = "target-instance-missing"
            },
            Options = new InvocationOptions
            {
                TtlMs = 3000,
                WaitTimeoutMs = 1000,
                AutoLaunch = false,
                QueueIfOffline = false
            }
        }));
        Assert.Equal(-32010, missingRequestException.Code);
        Assert.Equal("target_instance_missing", missingRequestException.Reason);
        Assert.Empty((await client.PollAsync(new PollRequest
        {
            InstanceId = targetInstance.Instance.InstanceId,
            InstanceSessionToken = targetInstance.InstanceSessionToken,
            WaitMs = 0
        })).Items);
        Assert.Empty((await client.PollAsync(new PollRequest
        {
            InstanceId = peerInstance.Instance.InstanceId,
            InstanceSessionToken = peerInstance.InstanceSessionToken,
            WaitMs = 0
        })).Items);
    }

    [Fact]
    public async Task RequestAsync_WhenCallerCancelsWaiting_ShouldLeaveInvocationRespondable()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.cancel-wait.app",
            Scope = string.Empty,
            DisplayName = "invoke.cancel-wait.app"
        });

        await using var client = await host.CreateClientAsync("invoke-cancel-wait-client");
        var registered = await RegisterInstanceAsync(client, "invoke.cancel-wait.app", "cancel-wait-inst-1", scope: string.Empty);
        using var waitCancellation = new CancellationTokenSource();

        var requestTask = client.RequestAsync(new InvokeRequest
        {
            AppId = "invoke.cancel-wait.app",
            Method = "test.cancel.wait",
            Target = new InvocationTarget
            {
                Scope = string.Empty
            },
            Options = new InvocationOptions
            {
                TtlMs = 5000,
                WaitTimeoutMs = 3000
            }
        }, waitCancellation.Token);

        var invocation = await WaitForSingleInvocationAsync(client, registered.Instance.InstanceId, registered.InstanceSessionToken);

        await waitCancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requestTask);

        await client.RespondAsync(new RespondRequest
        {
            InstanceId = registered.Instance.InstanceId,
            InstanceSessionToken = registered.InstanceSessionToken,
            InvocationId = invocation.InvocationId,
            LeaseToken = invocation.Delivery!.LeaseToken,
            Value = new { ok = true, value = 42 }
        });
    }

    [Fact]
    public async Task PollAndRespond_WithSessionTokenMismatch_ShouldMapForbidden()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.token.guard.app",
            Scope = string.Empty,
            DisplayName = "invoke.token.guard.app"
        });

        await using var client = await host.CreateClientAsync("invoke-token-guard-client");
        var registered = await RegisterInstanceAsync(client, "invoke.token.guard.app", "token-guard-inst-1", scope: string.Empty);

        var pollException = await Assert.ThrowsAsync<DevHubRpcException>(() => client.PollAsync(new PollRequest
        {
            InstanceId = registered.Instance.InstanceId,
            InstanceSessionToken = $"wrong-{registered.InstanceSessionToken}",
            WaitMs = 0
        }));
        Assert.Equal(-32002, pollException.Code);
        Assert.Equal("instance_session_token_mismatch", pollException.Reason);

        var requestTask = client.RequestAsync(new InvokeRequest
        {
            AppId = "invoke.token.guard.app",
            Method = "test.guard",
            Target = new InvocationTarget
            {
                Scope = string.Empty
            },
            Options = new InvocationOptions
            {
                TtlMs = 5000,
                WaitTimeoutMs = 3000
            }
        });

        var invocation = await WaitForSingleInvocationAsync(client, registered.Instance.InstanceId, registered.InstanceSessionToken);

        var respondException = await Assert.ThrowsAsync<DevHubRpcException>(() => client.RespondAsync(new RespondRequest
        {
            InstanceId = registered.Instance.InstanceId,
            InstanceSessionToken = $"wrong-{registered.InstanceSessionToken}",
            InvocationId = invocation.InvocationId,
            LeaseToken = invocation.Delivery!.LeaseToken,
            Value = new { ok = true }
        }));
        Assert.Equal(-32002, respondException.Code);
        Assert.Equal("instance_session_token_mismatch", respondException.Reason);

        await client.RespondAsync(new RespondRequest
        {
            InstanceId = registered.Instance.InstanceId,
            InstanceSessionToken = registered.InstanceSessionToken,
            InvocationId = invocation.InvocationId,
            LeaseToken = invocation.Delivery!.LeaseToken,
            Value = new { ok = true, value = 1 }
        });

        var requestResult = await requestTask;
        Assert.True(requestResult.Ok);
        Assert.Equal(1, requestResult.Value!.Value.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task NotifyAndRequest_WhenRpcDisabled_ShouldReturnForbidden()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.rpc-disabled.app",
            Scope = string.Empty,
            DisplayName = "invoke.rpc-disabled.app",
            Capabilities = new AppCapabilities
            {
                Rpc = false
            }
        });

        await using var client = await host.CreateClientAsync("invoke-rpc-disabled-client");

        var notifyException = await Assert.ThrowsAsync<DevHubRpcException>(() => client.NotifyAsync(new InvokeRequest
        {
            AppId = "invoke.rpc-disabled.app",
            Method = "test.notify",
            Target = new InvocationTarget
            {
                Scope = string.Empty
            },
            Options = new InvocationOptions
            {
                AutoLaunch = false,
                QueueIfOffline = true
            }
        }));
        Assert.Equal(-32002, notifyException.Code);
        Assert.Equal("rpc_disabled", notifyException.Reason);

        var requestException = await Assert.ThrowsAsync<DevHubRpcException>(() => client.RequestAsync(new InvokeRequest
        {
            AppId = "invoke.rpc-disabled.app",
            Method = "test.request",
            Target = new InvocationTarget
            {
                Scope = string.Empty
            },
            Options = new InvocationOptions
            {
                TtlMs = 3000,
                WaitTimeoutMs = 1000,
                AutoLaunch = false,
                QueueIfOffline = true
            }
        }));
        Assert.Equal(-32002, requestException.Code);
        Assert.Equal("rpc_disabled", requestException.Reason);
    }

    [Fact]
    public async Task NotifyAndRequest_WhenDefinitionMissing_ShouldReturnDefinitionNotFound()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await using var client = await host.CreateClientAsync("invoke-definition-missing-client");

        var notifyException = await Assert.ThrowsAsync<DevHubRpcException>(() => client.NotifyAsync(new InvokeRequest
        {
            AppId = "invoke.definition-missing.app",
            Method = "test.notify",
            Target = new InvocationTarget
            {
                Scope = string.Empty
            },
            Options = new InvocationOptions
            {
                AutoLaunch = false,
                QueueIfOffline = true
            }
        }));
        Assert.Equal(-32010, notifyException.Code);
        Assert.Equal("definition_not_found", notifyException.Reason);

        var requestException = await Assert.ThrowsAsync<DevHubRpcException>(() => client.RequestAsync(new InvokeRequest
        {
            AppId = "invoke.definition-missing.app",
            Method = "test.request",
            Target = new InvocationTarget
            {
                Scope = string.Empty
            },
            Options = new InvocationOptions
            {
                TtlMs = 3000,
                WaitTimeoutMs = 1000,
                AutoLaunch = false,
                QueueIfOffline = true
            }
        }));
        Assert.Equal(-32010, requestException.Code);
        Assert.Equal("definition_not_found", requestException.Reason);
    }

    [Fact]
    public async Task Poll_WhenInstancePollDisabled_ShouldReturnForbidden()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.poll-disabled.app",
            Scope = string.Empty,
            DisplayName = "invoke.poll-disabled.app"
        });

        await using var client = await host.CreateClientAsync("invoke-poll-disabled-client");
        var registered = await client.RegisterInstanceAsync(new AppInstanceRegistration
        {
            InstanceId = "poll-disabled-inst-1",
            AppId = "invoke.poll-disabled.app",
            Scope = string.Empty,
            Pid = Environment.ProcessId,
            Invoke = new InvokeCapability
            {
                Poll = false,
                Respond = true
            }
        }, InstancePassword);

        var exception = await Assert.ThrowsAsync<DevHubRpcException>(() => client.PollAsync(new PollRequest
        {
            InstanceId = registered.Instance.InstanceId,
            InstanceSessionToken = registered.InstanceSessionToken,
            WaitMs = 0
        }));
        Assert.Equal(-32002, exception.Code);
        Assert.Equal("poll_not_enabled", exception.Reason);
    }

    [Fact]
    public async Task Respond_WhenInstanceRespondDisabled_ShouldReturnForbidden()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.respond-disabled.app",
            Scope = string.Empty,
            DisplayName = "invoke.respond-disabled.app"
        });

        await using var client = await host.CreateClientAsync("invoke-respond-disabled-client");
        var registered = await client.RegisterInstanceAsync(new AppInstanceRegistration
        {
            InstanceId = "respond-disabled-inst-1",
            AppId = "invoke.respond-disabled.app",
            Scope = string.Empty,
            Pid = Environment.ProcessId,
            Invoke = new InvokeCapability
            {
                Poll = true,
                Respond = false
            }
        }, InstancePassword);

        var exception = await Assert.ThrowsAsync<DevHubRpcException>(() => client.RespondAsync(new RespondRequest
        {
            InstanceId = registered.Instance.InstanceId,
            InstanceSessionToken = registered.InstanceSessionToken,
            InvocationId = "invk-missing",
            LeaseToken = "missing-lease",
            Value = new { ok = true }
        }));
        Assert.Equal(-32002, exception.Code);
        Assert.Equal("respond_not_enabled", exception.Reason);
    }

    [Fact]
    public async Task Respond_WhenLeaseTokenInvalid_ShouldReturnDeliveryConflict()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.lease-conflict.app",
            Scope = string.Empty,
            DisplayName = "invoke.lease-conflict.app"
        });

        await using var client = await host.CreateClientAsync("invoke-lease-conflict-client");
        var registered = await RegisterInstanceAsync(client, "invoke.lease-conflict.app", "lease-conflict-inst-1", scope: string.Empty);

        var requestTask = client.RequestAsync(new InvokeRequest
        {
            AppId = "invoke.lease-conflict.app",
            Method = "test.lease-conflict",
            Target = new InvocationTarget
            {
                Scope = string.Empty
            },
            Options = new InvocationOptions
            {
                TtlMs = 5000,
                WaitTimeoutMs = 3000
            }
        });

        var invocation = await WaitForSingleInvocationAsync(client, registered.Instance.InstanceId, registered.InstanceSessionToken);

        var exception = await Assert.ThrowsAsync<DevHubRpcException>(() => client.RespondAsync(new RespondRequest
        {
            InstanceId = registered.Instance.InstanceId,
            InstanceSessionToken = registered.InstanceSessionToken,
            InvocationId = invocation.InvocationId,
            LeaseToken = $"{invocation.Delivery!.LeaseToken}-wrong",
            Value = new { ok = true }
        }));
        Assert.Equal(-32030, exception.Code);
        Assert.Equal("delivery_conflict", exception.Message);

        await client.RespondAsync(new RespondRequest
        {
            InstanceId = registered.Instance.InstanceId,
            InstanceSessionToken = registered.InstanceSessionToken,
            InvocationId = invocation.InvocationId,
            LeaseToken = invocation.Delivery!.LeaseToken,
            Value = new { ok = true, value = 9 }
        });

        var requestResult = await requestTask;
        Assert.True(requestResult.Ok);
        Assert.Equal(9, requestResult.Value!.Value.GetProperty("value").GetInt32());
    }

    private static AppInstanceRegistration CreateInstance(string appId, string instanceId, string scope)
    {
        return new AppInstanceRegistration
        {
            InstanceId = instanceId,
            AppId = appId,
            Scope = scope,
            Pid = Environment.ProcessId,
            Invoke = new InvokeCapability
            {
                Poll = true,
                Respond = true
            }
        };
    }

    private static Task<RegisterInstanceResult> RegisterInstanceAsync(DevHubClient client, string appId, string instanceId, string scope)
    {
        return client.RegisterInstanceAsync(CreateInstance(appId, instanceId, scope), InstancePassword);
    }

    private static async Task<Invocation> WaitForSingleInvocationAsync(
        DevHubClient client,
        string instanceId,
        string instanceSessionToken,
        int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var lastCount = 0;

        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            var waitMs = remaining <= TimeSpan.Zero
                ? 0
                : Math.Min((int)Math.Ceiling(remaining.TotalMilliseconds), 250);

            var pollResult = await client.PollAsync(new PollRequest
            {
                InstanceId = instanceId,
                InstanceSessionToken = instanceSessionToken,
                WaitMs = waitMs
            });

            lastCount = pollResult.Items.Count;
            if (lastCount == 1)
            {
                var invocation = pollResult.Items[0];
                Assert.False(string.IsNullOrWhiteSpace(invocation.Delivery?.LeaseToken));
                return invocation;
            }

            if (lastCount > 1)
            {
                Assert.Single(pollResult.Items);
            }
        }

        throw new TimeoutException($"鍦?{timeoutMs}ms 鍐呮湭绛夊埌瀹炰緥 {instanceId} 鐨勫崟鏉¤皟鐢ㄣ€傛渶鍚庝竴娆¤疆璇㈣繑鍥?{lastCount} 涓」鐩€?");
    }
}
