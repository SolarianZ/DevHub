using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DevHub.Sdk;
using DevHub.Sdk.Models;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace DevHub.Editor
{
    /// <summary>
    /// Unity Editor 侧 DevHub dispatcher 的唯一对外入口。
    /// </summary>
    [InitializeOnLoad]
    public static class DevHubDispatcher
    {
        private const int InvalidDispatcherMessageCode = 1001;
        private const int ToolNotFoundCode = 1002;
        private const int ToolHandlerFailedCode = 1003;
        private const int DefaultRetryDelaySeconds = 5;

        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, IDevHubTool> ToolsById = new Dictionary<string, IDevHubTool>(StringComparer.Ordinal);
        private static readonly Dictionary<object, string> ToolIdsByInstance = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
        private static readonly List<Task> BackgroundTasks = new List<Task>();

        private static DevHubDispatcherIdentity _identity;
        private static CancellationTokenSource _lifetimeCts;
        private static DevHubClient _client;
        private static AppInstance _registeredInstance;
        private static Task<RuntimeConnection> _initializeTask;
        private static Task<DateTimeOffset> _heartbeatTask;
        private static Task<PollResult> _pollTask;
        private static DateTime _nextInitializeUtc;
        private static DateTime _nextHeartbeatUtc;
        private static DateTime _nextPollUtc;
        private static TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(10);
        private static int _pollWaitMs = 5000;
        private static bool _hostConnected;
        private static bool _stopping;
        private static string _lastError = string.Empty;
        private static DateTime? _lastConnectedAtUtc;

        static DevHubDispatcher()
        {
            ConfigureEditorHooks();
            _identity = DevHubDispatcherIdentity.LoadOrCreate();
            StartRuntime("editor-load");
        }

        /// <summary>
        /// 注册一个 Unity 内部 Tool。Tool 只进入本地路由表，不会注册为 Host 可见的 app。
        /// </summary>
        /// <param name="tool">实现 <see cref="IDevHubTool"/> 的 Tool 实例。</param>
        /// <returns>注册是否成功以及诊断消息。</returns>
        public static Result RegisterTool(object tool)
        {
            if (tool == null)
            {
                return Result.Fail("tool 不能为空。");
            }

            var devHubTool = tool as IDevHubTool;
            if (devHubTool == null)
            {
                return Result.Fail("tool 必须实现 IDevHubTool。");
            }

            if (string.IsNullOrWhiteSpace(devHubTool.ToolId))
            {
                return Result.Fail("toolId 不能为空。");
            }

            lock (SyncRoot)
            {
                if (ToolIdsByInstance.ContainsKey(tool))
                {
                    return Result.Fail("该 Tool 实例已经注册。");
                }

                if (ToolsById.ContainsKey(devHubTool.ToolId))
                {
                    return Result.Fail("toolId 已经被注册: " + devHubTool.ToolId);
                }

                ToolsById.Add(devHubTool.ToolId, devHubTool);
                ToolIdsByInstance.Add(tool, devHubTool.ToolId);
            }

            return Result.Ok("Tool 已注册: " + devHubTool.ToolId);
        }

        /// <summary>
        /// 注销一个 Unity 内部 Tool。注销只影响 dispatcher 本地路由表。
        /// </summary>
        /// <param name="tool">曾通过 <see cref="RegisterTool"/> 注册的 Tool 实例。</param>
        /// <returns>注销是否成功以及诊断消息。</returns>
        public static Result UnregisterTool(object tool)
        {
            if (tool == null)
            {
                return Result.Fail("tool 不能为空。");
            }

            lock (SyncRoot)
            {
                string toolId;
                if (!ToolIdsByInstance.TryGetValue(tool, out toolId))
                {
                    return Result.Fail("该 Tool 实例尚未注册。");
                }

                ToolIdsByInstance.Remove(tool);
                ToolsById.Remove(toolId);
                return Result.Ok("Tool 已注销: " + toolId);
            }
        }

        /// <summary>
        /// 通过 dispatcher 向目标 DevHub app 发送 notify。
        /// </summary>
        /// <param name="tool">已注册 Tool 实例。</param>
        /// <param name="appId">目标 appId。</param>
        /// <param name="method">目标方法名。</param>
        /// <param name="payload">业务载荷；会被放入 <c>payload</c> 信封字段。</param>
        /// <param name="options">可选目标与调用选项。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>发送结果。</returns>
        public static async Task<Result> NotifyAsync(object tool, string appId, string method, JToken payload, DevHubDispatcherSendOptions options = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            var toolId = GetRegisteredToolIdOrThrow(tool);
            var client = GetConnectedClientOrThrow();
            var request = BuildInvokeRequest(appId, method, BuildEnvelope(toolId, payload), options);
            var notifyResult = await client.NotifyAsync(request, cancellationToken);
            return Result.Ok("Notify 已发送: " + notifyResult.InvocationId);
        }

        /// <summary>
        /// 通过 dispatcher 向目标 DevHub app 发送 request。
        /// </summary>
        /// <param name="tool">已注册 Tool 实例。</param>
        /// <param name="appId">目标 appId。</param>
        /// <param name="method">目标方法名。</param>
        /// <param name="payload">业务载荷；会被放入 <c>payload</c> 信封字段。</param>
        /// <param name="options">可选目标与调用选项。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>Host 返回的业务结果，dispatcher 不改写成功载荷。</returns>
        public static async Task<JToken> RequestAsync(object tool, string appId, string method, JToken payload, DevHubDispatcherSendOptions options = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            var toolId = GetRegisteredToolIdOrThrow(tool);
            var client = GetConnectedClientOrThrow();
            var request = BuildInvokeRequest(appId, method, BuildEnvelope(toolId, payload), options);
            var requestResult = await client.RequestAsync(request, cancellationToken);
            return requestResult.Value;
        }

        /// <summary>
        /// 读取 dispatcher 当前运行态快照。
        /// </summary>
        /// <returns>只读运行态快照。</returns>
        public static DevHubDispatcherStatus GetStatus()
        {
            lock (SyncRoot)
            {
                return new DevHubDispatcherStatus(_hostConnected, _identity == null ? string.Empty : _identity.AppId, _identity == null ? string.Empty : _identity.InstanceId, ToolsById.Count, _lastError, _lastConnectedAtUtc);
            }
        }

        private static void ConfigureEditorHooks()
        {
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.update += OnEditorUpdate;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload -= OnAfterAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            EditorApplication.quitting -= OnEditorQuitting;
            EditorApplication.quitting += OnEditorQuitting;
        }

        private static void OnBeforeAssemblyReload()
        {
            if (_identity != null)
            {
                _identity.Save();
            }

            StopRuntime(false, true);
        }

        private static void OnAfterAssemblyReload()
        {
            _identity = DevHubDispatcherIdentity.LoadOrCreate();
            StartRuntime("after-assembly-reload");
        }

        private static void OnEditorQuitting()
        {
            StopRuntime(true, true);
        }

        private static void StartRuntime(string reason)
        {
            lock (SyncRoot)
            {
                if (_identity == null)
                {
                    _identity = DevHubDispatcherIdentity.LoadOrCreate();
                }

                if (_lifetimeCts == null || _lifetimeCts.IsCancellationRequested)
                {
                    _lifetimeCts = new CancellationTokenSource();
                }

                _stopping = false;
                _lastError = string.Empty;
                _nextInitializeUtc = DateTime.UtcNow;
            }

            Debug.Log("DevHub dispatcher runtime start: " + reason);
        }

        private static void StopRuntime(bool unregisterInstance, bool stopUntilRestart)
        {
            CancellationTokenSource cts;
            DevHubClient client;
            Task initializeTask;
            Task heartbeatTask;
            Task pollTask;
            lock (SyncRoot)
            {
                cts = _lifetimeCts;
                client = _client;
                initializeTask = _initializeTask;
                heartbeatTask = _heartbeatTask;
                pollTask = _pollTask;
                _lifetimeCts = null;
                _client = null;
                _registeredInstance = null;
                _initializeTask = null;
                _heartbeatTask = null;
                _pollTask = null;
                _hostConnected = false;
                _stopping = stopUntilRestart;
            }

            ObserveFaultSilently(initializeTask);
            ObserveFaultSilently(heartbeatTask);
            ObserveFaultSilently(pollTask);
            CancelAndDispose(cts);
            if (client != null)
            {
                if (unregisterInstance && _identity != null)
                {
                    TryUnregisterInstance(client, _identity);
                }

                DisposeClientSilently(client);
            }
        }

        private static void OnEditorUpdate()
        {
            ObserveBackgroundTasks();
            if (_stopping)
            {
                return;
            }

            ProcessInitializeTask();
            if (!HasClient())
            {
                BeginInitializeIfDue();
                return;
            }

            ProcessHeartbeatTask();
            ProcessPollTask();
            if (!HasClient())
            {
                BeginInitializeIfDue();
                return;
            }

            BeginHeartbeatIfDue();
            BeginPollIfDue();
        }

        private static bool HasClient()
        {
            lock (SyncRoot)
            {
                return _client != null && _lifetimeCts != null && !_lifetimeCts.IsCancellationRequested;
            }
        }

        private static void BeginInitializeIfDue()
        {
            RuntimeEditorContext editorContext;
            CancellationToken token;
            lock (SyncRoot)
            {
                if (_initializeTask != null || DateTime.UtcNow < _nextInitializeUtc)
                {
                    return;
                }

                if (_lifetimeCts == null || _lifetimeCts.IsCancellationRequested)
                {
                    _lifetimeCts = new CancellationTokenSource();
                }

                token = _lifetimeCts.Token;
            }

            editorContext = RuntimeEditorContext.Capture();
            lock (SyncRoot)
            {
                _initializeTask = InitializeRuntimeAsync(_identity, editorContext, token);
            }
        }

        private static async Task<RuntimeConnection> InitializeRuntimeAsync(DevHubDispatcherIdentity identity, RuntimeEditorContext editorContext, CancellationToken cancellationToken)
        {
            DevHubClient client = null;
            try
            {
                client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
                {
                    ClientId = "unity-devhub-dispatcher:" + identity.InstanceId,
                    RequestTimeout = TimeSpan.FromSeconds(30)
                }, cancellationToken);

                await client.UpsertDefinitionAsync(BuildAppDefinition(identity, editorContext), cancellationToken);
                var instance = await client.RegisterInstanceAsync(BuildAppInstanceRegistration(identity, editorContext), identity.InstancePassword, cancellationToken);
                return new RuntimeConnection(client, instance, client.Runtime.RuntimeTuning);
            }
            catch
            {
                if (client != null)
                {
                    await client.DisposeAsync();
                }

                throw;
            }
        }

        private static void ProcessInitializeTask()
        {
            Task<RuntimeConnection> task;
            lock (SyncRoot)
            {
                task = _initializeTask;
            }

            if (task == null || !task.IsCompleted)
            {
                return;
            }

            lock (SyncRoot)
            {
                if (ReferenceEquals(_initializeTask, task))
                {
                    _initializeTask = null;
                }
            }

            if (task.IsCanceled)
            {
                ScheduleInitializeRetry("initialize", null);
                return;
            }

            if (task.IsFaulted)
            {
                ScheduleInitializeRetry("initialize", GetTaskException(task));
                return;
            }

            var connection = task.Result;
            DevHubClient oldClient = null;
            var shouldDisposeConnection = false;
            lock (SyncRoot)
            {
                if (_lifetimeCts == null || _lifetimeCts.IsCancellationRequested || _stopping)
                {
                    shouldDisposeConnection = true;
                }
                else
                {
                    oldClient = _client;
                    _client = connection.Client;
                    _registeredInstance = connection.Instance;
                    _heartbeatInterval = ResolveHeartbeatInterval(connection.RuntimeTuning);
                    _pollWaitMs = ResolvePollWaitMs(connection.RuntimeTuning);
                    _nextHeartbeatUtc = DateTime.UtcNow;
                    _nextPollUtc = DateTime.UtcNow;
                    _hostConnected = true;
                    _lastConnectedAtUtc = DateTime.UtcNow;
                    _lastError = string.Empty;
                }
            }

            if (shouldDisposeConnection)
            {
                DisposeClientSilently(connection.Client);
                return;
            }

            if (oldClient != null)
            {
                DisposeClientSilently(oldClient);
            }

            Debug.Log("DevHub dispatcher connected. AppId: " + _identity.AppId + ", InstanceId: " + _identity.InstanceId);
        }

        private static void ScheduleInitializeRetry(string stage, Exception exception)
        {
            SetLastError(stage + " failed: " + (exception == null ? "canceled" : exception.Message));
            if (exception != null)
            {
                Debug.LogWarning("DevHub dispatcher " + stage + " failed: " + exception);
            }

            lock (SyncRoot)
            {
                _hostConnected = false;
                _nextInitializeUtc = DateTime.UtcNow.AddSeconds(DefaultRetryDelaySeconds);
            }
        }

        private static void BeginHeartbeatIfDue()
        {
            lock (SyncRoot)
            {
                if (_heartbeatTask != null || DateTime.UtcNow < _nextHeartbeatUtc || _client == null)
                {
                    return;
                }

                _heartbeatTask = _client.HeartbeatAsync(_identity.InstanceId, _lifetimeCts.Token);
            }
        }

        private static void ProcessHeartbeatTask()
        {
            Task<DateTimeOffset> task;
            lock (SyncRoot)
            {
                task = _heartbeatTask;
            }

            if (task == null || !task.IsCompleted)
            {
                return;
            }

            lock (SyncRoot)
            {
                if (ReferenceEquals(_heartbeatTask, task))
                {
                    _heartbeatTask = null;
                }
            }

            if (task.IsCanceled)
            {
                lock (SyncRoot)
                {
                    _nextHeartbeatUtc = DateTime.UtcNow.AddSeconds(DefaultRetryDelaySeconds);
                }

                return;
            }

            if (task.IsFaulted)
            {
                ResetRuntimeForRetry("heartbeat", GetTaskException(task));
                return;
            }

            lock (SyncRoot)
            {
                _hostConnected = true;
                _lastConnectedAtUtc = DateTime.UtcNow;
                _nextHeartbeatUtc = DateTime.UtcNow.Add(_heartbeatInterval);
                _lastError = string.Empty;
            }
        }

        private static void BeginPollIfDue()
        {
            lock (SyncRoot)
            {
                if (_pollTask != null || DateTime.UtcNow < _nextPollUtc || _client == null)
                {
                    return;
                }

                _pollTask = _client.PollAsync(new PollRequest
                {
                    InstanceId = _identity.InstanceId,
                    MaxCount = 10,
                    WaitMs = _pollWaitMs
                }, _lifetimeCts.Token);
            }
        }

        private static void ProcessPollTask()
        {
            Task<PollResult> task;
            lock (SyncRoot)
            {
                task = _pollTask;
            }

            if (task == null || !task.IsCompleted)
            {
                return;
            }

            lock (SyncRoot)
            {
                if (ReferenceEquals(_pollTask, task))
                {
                    _pollTask = null;
                }
            }

            if (task.IsCanceled)
            {
                lock (SyncRoot)
                {
                    _nextPollUtc = DateTime.UtcNow.AddSeconds(DefaultRetryDelaySeconds);
                }

                return;
            }

            if (task.IsFaulted)
            {
                ResetRuntimeForRetry("poll", GetTaskException(task));
                return;
            }

            var result = task.Result;
            lock (SyncRoot)
            {
                _hostConnected = true;
                _lastConnectedAtUtc = DateTime.UtcNow;
                _nextPollUtc = DateTime.UtcNow;
                _lastError = string.Empty;
            }

            for (var index = 0; index < result.Items.Count; index++)
            {
                RouteInvocation(result.Items[index]);
            }
        }

        private static void RouteInvocation(Invocation invocation)
        {
            if (invocation == null)
            {
                return;
            }

            string toolId;
            JToken payload;
            string envelopeError;
            if (!TryReadEnvelope(invocation.Args, out toolId, out payload, out envelopeError))
            {
                HandleRoutingFailure(invocation, InvalidDispatcherMessageCode, "invalid_dispatcher_message", null, envelopeError);
                return;
            }

            IDevHubTool tool;
            if (!TryGetTool(toolId, out tool))
            {
                HandleRoutingFailure(invocation, ToolNotFoundCode, "tool_not_found", toolId, "Tool 未注册。");
                return;
            }

            try
            {
                if (invocation.Kind == InvocationKind.Request)
                {
                    EnqueueBackgroundTask(RespondWithValueAsync(invocation, tool.HandleDevHubRequest(invocation.Method, payload)));
                    return;
                }

                tool.HandleDevHubNotify(invocation.Method, payload);
            }
            catch (Exception ex)
            {
                if (invocation.Kind == InvocationKind.Request)
                {
                    HandleRoutingFailure(invocation, ToolHandlerFailedCode, "tool_handler_failed", toolId, ex.Message);
                    return;
                }

                Debug.LogWarning("DevHub dispatcher notify 路由失败。toolId=" + toolId + ", method=" + invocation.Method + ", reason=" + ex);
            }
        }

        private static void HandleRoutingFailure(Invocation invocation, int code, string message, string toolId, string reason)
        {
            if (invocation.Kind == InvocationKind.Request)
            {
                EnqueueBackgroundTask(RespondWithErrorAsync(invocation, code, message, toolId, reason));
                return;
            }

            Debug.LogWarning("DevHub dispatcher notify 路由失败。toolId=" + (toolId ?? string.Empty) + ", method=" + invocation.Method + ", reason=" + reason);
        }

        private static async Task RespondWithValueAsync(Invocation invocation, JToken value)
        {
            DevHubClient client;
            CancellationToken token;
            lock (SyncRoot)
            {
                client = _client;
                token = _lifetimeCts == null ? CancellationToken.None : _lifetimeCts.Token;
            }

            if (client == null)
            {
                return;
            }

            await client.RespondAsync(new RespondRequest
            {
                InstanceId = _identity.InstanceId,
                InvocationId = invocation.InvocationId,
                Value = value == null ? JValue.CreateNull() : value
            }, token);
        }

        private static async Task RespondWithErrorAsync(Invocation invocation, int code, string message, string toolId, string reason)
        {
            DevHubClient client;
            CancellationToken token;
            lock (SyncRoot)
            {
                client = _client;
                token = _lifetimeCts == null ? CancellationToken.None : _lifetimeCts.Token;
            }

            if (client == null)
            {
                return;
            }

            await client.RespondAsync(new RespondRequest
            {
                InstanceId = _identity.InstanceId,
                InvocationId = invocation.InvocationId,
                Error = DevHubCalleeError.Create(code, message, new
                {
                    toolId = toolId ?? string.Empty,
                    method = invocation.Method ?? string.Empty,
                    reason = reason ?? string.Empty
                })
            }, token);
        }

        private static void EnqueueBackgroundTask(Task task)
        {
            if (task != null)
            {
                BackgroundTasks.Add(task);
            }
        }

        private static void ObserveBackgroundTasks()
        {
            for (var index = BackgroundTasks.Count - 1; index >= 0; index--)
            {
                var task = BackgroundTasks[index];
                if (!task.IsCompleted)
                {
                    continue;
                }

                BackgroundTasks.RemoveAt(index);
                if (task.IsFaulted)
                {
                    var exception = GetTaskException(task);
                    SetLastError("background task failed: " + (exception == null ? "unknown" : exception.Message));
                    Debug.LogWarning("DevHub dispatcher background task failed: " + exception);
                }
            }
        }

        private static void ResetRuntimeForRetry(string stage, Exception exception)
        {
            SetLastError(stage + " failed: " + (exception == null ? "unknown" : exception.Message));
            Debug.LogWarning("DevHub dispatcher " + stage + " failed: " + exception);
            StopRuntime(false, false);
            StartRuntime(stage + "-retry");
            lock (SyncRoot)
            {
                _nextInitializeUtc = DateTime.UtcNow.AddSeconds(DefaultRetryDelaySeconds);
            }
        }

        private static bool TryReadEnvelope(JToken args, out string toolId, out JToken payload, out string error)
        {
            toolId = null;
            payload = null;
            error = null;

            var obj = args as JObject;
            if (obj == null)
            {
                error = "args 必须是对象。";
                return false;
            }

            var toolIdToken = obj["toolId"];
            if (toolIdToken == null || toolIdToken.Type != JTokenType.String)
            {
                error = "缺少合法 toolId。";
                return false;
            }

            toolId = toolIdToken.Value<string>();
            if (string.IsNullOrWhiteSpace(toolId))
            {
                error = "toolId 不能为空。";
                return false;
            }

            payload = obj["payload"] == null ? JValue.CreateNull() : obj["payload"];
            return true;
        }

        private static bool TryGetTool(string toolId, out IDevHubTool tool)
        {
            lock (SyncRoot)
            {
                return ToolsById.TryGetValue(toolId, out tool);
            }
        }

        private static string GetRegisteredToolIdOrThrow(object tool)
        {
            if (tool == null)
            {
                throw new ArgumentNullException("tool");
            }

            lock (SyncRoot)
            {
                string toolId;
                if (!ToolIdsByInstance.TryGetValue(tool, out toolId))
                {
                    throw new InvalidOperationException("Tool 尚未注册到 DevHubDispatcher。");
                }

                return toolId;
            }
        }

        private static DevHubClient GetConnectedClientOrThrow()
        {
            lock (SyncRoot)
            {
                if (_client == null || !_hostConnected)
                {
                    throw new InvalidOperationException("DevHub dispatcher 尚未建立 Host 连接。");
                }

                return _client;
            }
        }

        private static JObject BuildEnvelope(string toolId, JToken payload)
        {
            return new JObject
            {
                ["toolId"] = toolId,
                ["payload"] = payload == null ? JValue.CreateNull() : payload.DeepClone()
            };
        }

        private static InvokeRequest BuildInvokeRequest(string appId, string method, JObject envelope, DevHubDispatcherSendOptions options)
        {
            if (string.IsNullOrWhiteSpace(appId))
            {
                throw new ArgumentException("appId 不能为空。", "appId");
            }

            if (string.IsNullOrWhiteSpace(method))
            {
                throw new ArgumentException("method 不能为空。", "method");
            }

            var request = new InvokeRequest
            {
                AppId = appId,
                Method = method,
                Args = envelope
            };

            if (options == null)
            {
                return request;
            }

            if (!string.IsNullOrEmpty(options.Scope) || !string.IsNullOrEmpty(options.InstanceId))
            {
                request.Target = new InvocationTarget
                {
                    Scope = NormalizeOptionalString(options.Scope, "options.Scope"),
                    InstanceId = NormalizeOptionalString(options.InstanceId, "options.InstanceId")
                };
            }

            if (options.TtlMs.HasValue || options.WaitTimeoutMs.HasValue || options.QueueIfOffline.HasValue || options.AutoLaunch.HasValue)
            {
                request.Options = new InvocationOptions
                {
                    TtlMs = options.TtlMs,
                    WaitTimeoutMs = options.WaitTimeoutMs,
                    QueueIfOffline = options.QueueIfOffline,
                    AutoLaunch = options.AutoLaunch
                };
            }

            return request;
        }

        private static string NormalizeOptionalString(string value, string parameterName)
        {
            if (value == null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(parameterName + " 不能是空白字符串。", parameterName);
            }

            return value;
        }

        private static AppDefinition BuildAppDefinition(DevHubDispatcherIdentity identity, RuntimeEditorContext context)
        {
            return new AppDefinition
            {
                AppId = identity.AppId,
                DisplayName = "Unity Editor - " + context.ProjectName,
                Description = "DevHub dispatcher for Unity project " + context.ProjectName,
                Capabilities = new AppCapabilities
                {
                    Rpc = true
                },
                Launch = new LaunchConfiguration
                {
                    ExePath = context.UnityEditorPath,
                    WorkingDirectory = context.ProjectPath,
                    ArgsTemplate = "-projectPath " + QuoteArgument(context.ProjectPath) + " -devhubAppId {appId}",
                    DedupeKeyTemplate = "{appId}:{scopeOrGlobal}"
                }
            };
        }

        private static AppInstanceRegistration BuildAppInstanceRegistration(DevHubDispatcherIdentity identity, RuntimeEditorContext context)
        {
            return new AppInstanceRegistration
            {
                InstanceId = identity.InstanceId,
                AppId = identity.AppId,
                Scope = context.ProjectPath,
                Pid = context.ProcessId,
                Invoke = new InvokeCapability
                {
                    Poll = true,
                    Respond = true
                },
                Meta = new JObject
                {
                    ["projectPath"] = context.ProjectPath,
                    ["unityVersion"] = context.UnityVersion,
                    ["unityEditorPath"] = context.UnityEditorPath,
                    ["dispatcherPackage"] = "devhub.dispatcher",
                    ["dispatcherRole"] = "unity-editor"
                }
            };
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static TimeSpan ResolveHeartbeatInterval(HubRuntimeTuning tuning)
        {
            if (tuning == null)
            {
                return TimeSpan.FromSeconds(10);
            }

            var leaseSeconds = tuning.LeaseSeconds > 0 ? tuning.LeaseSeconds : 30;
            var onlineThresholdSeconds = tuning.OnlineThresholdSeconds > 0 ? tuning.OnlineThresholdSeconds : leaseSeconds;
            return TimeSpan.FromSeconds(Math.Max(1, Math.Min(leaseSeconds, onlineThresholdSeconds) / 2));
        }

        private static int ResolvePollWaitMs(HubRuntimeTuning tuning)
        {
            var leaseSeconds = tuning == null || tuning.LeaseSeconds <= 0 ? 30 : tuning.LeaseSeconds;
            return Math.Max(1000, Math.Min(5000, leaseSeconds * 1000 / 2));
        }

        private static void TryUnregisterInstance(DevHubClient client, DevHubDispatcherIdentity identity)
        {
            try
            {
                client.UnregisterInstanceAsync(identity.InstanceId, identity.InstancePassword).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("DevHub dispatcher unregisterInstance failed: " + ex);
            }
        }

        private static void DisposeClientSilently(DevHubClient client)
        {
            if (client == null)
            {
                return;
            }

            try
            {
                client.DisposeAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("DevHub dispatcher dispose failed: " + ex);
            }
        }

        private static void CancelAndDispose(CancellationTokenSource cts)
        {
            if (cts == null)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            cts.Dispose();
        }

        private static void ObserveFaultSilently(Task task)
        {
            if (task == null)
            {
                return;
            }

            task.ContinueWith(t =>
            {
                var ignored = t.Exception;
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private static Exception GetTaskException(Task task)
        {
            return task.Exception == null ? null : task.Exception.GetBaseException();
        }

        private static void SetLastError(string message)
        {
            lock (SyncRoot)
            {
                _lastError = message ?? string.Empty;
                _hostConnected = false;
            }
        }

        private sealed class RuntimeConnection
        {
            public RuntimeConnection(DevHubClient client, AppInstance instance, HubRuntimeTuning runtimeTuning)
            {
                Client = client;
                Instance = instance;
                RuntimeTuning = runtimeTuning;
            }

            public DevHubClient Client { get; private set; }
            public AppInstance Instance { get; private set; }
            public HubRuntimeTuning RuntimeTuning { get; private set; }
        }

        private sealed class RuntimeEditorContext
        {
            public static RuntimeEditorContext Capture()
            {
                var projectPath = ResolveProjectPath();
                return new RuntimeEditorContext
                {
                    ProjectPath = projectPath,
                    ProjectName = new DirectoryInfo(projectPath).Name,
                    UnityEditorPath = EditorApplication.applicationPath,
                    UnityVersion = Application.unityVersion,
                    ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id
                };
            }

            public string ProjectPath { get; private set; }
            public string ProjectName { get; private set; }
            public string UnityEditorPath { get; private set; }
            public string UnityVersion { get; private set; }
            public int ProcessId { get; private set; }

            private static string ResolveProjectPath()
            {
                var assetsDirectory = new DirectoryInfo(Application.dataPath);
                return assetsDirectory.Parent == null ? assetsDirectory.FullName : assetsDirectory.Parent.FullName;
            }
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

            public new bool Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
