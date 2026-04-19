using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DevHub.Sdk;
using DevHub.Sdk.Models;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace DevHubDispatcher.Editor
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

        private const string LifecycleLogCategory = "Lifecycle";
        private const string ToolRegistryLogCategory = "ToolRegistry";
        private const string RoutingLogCategory = "Routing";
        private const string OutboundLogCategory = "Outbound";
        private const string CleanupLogCategory = "Cleanup";
        private const string BackgroundLogCategory = "Background";

        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, IDevHubTool> ToolsById = new Dictionary<string, IDevHubTool>(StringComparer.Ordinal);
        private static readonly Dictionary<IDevHubTool, string> ToolIdsByInstance = new Dictionary<IDevHubTool, string>(DevHubToolReferenceEqualityComparer.Instance);
        private static readonly List<Task> BackgroundTasks = new List<Task>();

        private static DevHubDispatcherIdentity _identity;
        private static CancellationTokenSource _lifetimeCts;
        private static DevHubClient _client;
        private static Task<Result<RuntimeConnection>> _initializeTask;
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
            try
            {
                ConfigureEditorHooks();
                _identity = DevHubDispatcherIdentity.LoadOrCreate();
                StartRuntime("editor-load");
            }
            catch (Exception ex)
            {
                SetLastError("dispatcher 初始化失败: " + ex.Message);
                DevHubDispatcherLogger.Error(LifecycleLogCategory, "Dispatcher 静态初始化失败。", ex);
            }
        }

        /// <summary>
        /// 基于 request handler 注册一个 Unity 内部 Tool。
        /// </summary>
        /// <param name="toolId">Tool 的稳定标识。</param>
        /// <param name="requestHandler">处理 request 的委托。</param>
        /// <returns>注册是否成功以及诊断消息。</returns>
        public static Result RegisterTool(string toolId, DevHubRequestHandler requestHandler)
        {
            return RegisterTool(toolId, requestHandler, null);
        }

        /// <summary>
        /// 基于 notify handler 注册一个 Unity 内部 Tool。
        /// </summary>
        /// <param name="toolId">Tool 的稳定标识。</param>
        /// <param name="notifyHandler">处理 notify 的委托。</param>
        /// <returns>注册是否成功以及诊断消息。</returns>
        public static Result RegisterTool(string toolId, DevHubNotifyHandler notifyHandler)
        {
            return RegisterTool(toolId, null, notifyHandler);
        }

        /// <summary>
        /// 基于委托注册一个 Unity 内部 Tool。至少需要提供一个非空 handler。
        /// </summary>
        /// <param name="toolId">Tool 的稳定标识。</param>
        /// <param name="requestHandler">处理 request 的委托；可为空。</param>
        /// <param name="notifyHandler">处理 notify 的委托；可为空。</param>
        /// <returns>注册是否成功以及诊断消息。</returns>
        /// <remarks>当前重载集合即最终公开注册契约；dispatcher 不为早期未实现的占位 API 保留兼容层。</remarks>
        public static Result RegisterTool(string toolId, DevHubRequestHandler requestHandler, DevHubNotifyHandler notifyHandler)
        {
            if (!TryCreateDelegateTool(toolId, requestHandler, notifyHandler, out DevHubToolDelegate tool, out Result validationResult))
            {
                return validationResult;
            }

            return RegisterToolCore(tool);
        }

        /// <summary>
        /// 注册一个 Unity 内部 Tool。Tool 只进入本地路由表，不会注册为 Host 可见的 app。
        /// </summary>
        /// <param name="tool">实现 <see cref="IDevHubTool"/> 的 Tool 实例。</param>
        /// <returns>注册是否成功以及诊断消息。</returns>
        public static Result RegisterTool(IDevHubTool tool)
        {
            if (!TryValidateToolRegistration(tool, out Result validationResult))
            {
                return validationResult;
            }

            return RegisterToolCore(tool);
        }

        /// <summary>
        /// 按 <paramref name="toolId"/> 注销一个 Unity 内部 Tool。对象注册和委托注册都适用。
        /// </summary>
        /// <param name="toolId">已注册 Tool 的稳定标识。</param>
        /// <returns>注销是否成功以及诊断消息。</returns>
        /// <remarks>调用方不需要持有委托包装实例，也不需要依赖未实现旧 API 的兼容行为。</remarks>
        public static Result UnregisterTool(string toolId)
        {
            if (string.IsNullOrWhiteSpace(toolId))
            {
                return LogFailure(ToolRegistryLogCategory, "toolId 不能为空。");
            }

            lock (SyncRoot)
            {
                if (!ToolsById.TryGetValue(toolId, out IDevHubTool tool))
                {
                    return LogFailure(ToolRegistryLogCategory, "toolId 尚未注册: " + toolId);
                }

                RemoveToolRegistration(tool, toolId);
            }

            DevHubDispatcherLogger.Info(ToolRegistryLogCategory, "Tool 已注销。toolId=" + toolId);
            return Result.Ok("Tool 已注销: " + toolId);
        }

        /// <summary>
        /// 注销一个 Unity 内部 Tool。注销只影响 dispatcher 本地路由表。
        /// </summary>
        /// <param name="tool">曾通过 <see cref="RegisterTool(IDevHubTool)"/> 注册的 Tool 实例。</param>
        /// <returns>注销是否成功以及诊断消息。</returns>
        public static Result UnregisterTool(IDevHubTool tool)
        {
            if (tool == null)
            {
                return LogFailure(ToolRegistryLogCategory, "tool 不能为空。");
            }

            string toolId;
            lock (SyncRoot)
            {
                if (!ToolIdsByInstance.TryGetValue(tool, out toolId))
                {
                    return LogFailure(ToolRegistryLogCategory, "该 Tool 实例尚未注册。");
                }

                RemoveToolRegistration(tool, toolId);
            }

            DevHubDispatcherLogger.Info(ToolRegistryLogCategory, "Tool 已注销。toolId=" + toolId);
            return Result.Ok("Tool 已注销: " + toolId);
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
        public static async Task<Result> NotifyAsync(IDevHubTool tool, string appId, string method, JToken payload, DevHubDispatcherSendOptions options = null, CancellationToken cancellationToken = default)
        {
            if (!TryGetRegisteredToolId(tool, out string toolId, out Result toolIdResult))
            {
                return LogFailure(OutboundLogCategory, toolIdResult.Message);
            }

            if (!TryGetConnectedClient(out DevHubClient client, out string clientError))
            {
                return LogFailure(OutboundLogCategory, BuildOutboundFailureMessage(toolId, method, clientError));
            }

            if (!TryBuildInvokeRequest(appId, method, BuildEnvelope(toolId, payload), options, out InvokeRequest request, out string requestError))
            {
                return LogFailure(OutboundLogCategory, BuildOutboundFailureMessage(toolId, method, requestError));
            }

            try
            {
                NotifyResult notifyResult = await client.NotifyAsync(request, cancellationToken);
                return Result.Ok("Notify 已发送: " + notifyResult.InvocationId);
            }
            catch (OperationCanceledException ex)
            {
                string message = BuildOutboundFailureMessage(toolId, method, "Notify 已取消。");
                DevHubDispatcherLogger.Warning(OutboundLogCategory, message, ex);
                return Result.Fail(message);
            }
            catch (Exception ex)
            {
                return LogFailure(OutboundLogCategory, BuildOutboundFailureMessage(toolId, method, "Notify 发送失败。"), ex);
            }
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
        public static async Task<Result<JToken>> RequestAsync(IDevHubTool tool, string appId, string method, JToken payload, DevHubDispatcherSendOptions options = null, CancellationToken cancellationToken = default)
        {
            if (!TryGetRegisteredToolId(tool, out string toolId, out Result toolIdResult))
            {
                return LogFailure<JToken>(OutboundLogCategory, toolIdResult.Message);
            }

            if (!TryGetConnectedClient(out DevHubClient client, out string clientError))
            {
                return LogFailure<JToken>(OutboundLogCategory, BuildOutboundFailureMessage(toolId, method, clientError));
            }

            if (!TryBuildInvokeRequest(appId, method, BuildEnvelope(toolId, payload), options, out InvokeRequest request, out string requestError))
            {
                return LogFailure<JToken>(OutboundLogCategory, BuildOutboundFailureMessage(toolId, method, requestError));
            }

            try
            {
                RequestResult requestResult = await client.RequestAsync(request, cancellationToken);
                JToken value = requestResult.Value ?? JValue.CreateNull();
                return Result<JToken>.Ok(value, "Request 已完成: " + requestResult.InvocationId);
            }
            catch (OperationCanceledException ex)
            {
                string message = BuildOutboundFailureMessage(toolId, method, "Request 已取消。");
                DevHubDispatcherLogger.Warning(OutboundLogCategory, message, ex);
                return Result<JToken>.Fail(message);
            }
            catch (Exception ex)
            {
                return LogFailure<JToken>(OutboundLogCategory, BuildOutboundFailureMessage(toolId, method, "Request 发送失败。"), ex);
            }
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
            try
            {
                if (_identity != null)
                {
                    _identity.Save();
                }

                StopRuntime(false, true);
            }
            catch (Exception ex)
            {
                SetLastError("beforeAssemblyReload failed: " + ex.Message);
                DevHubDispatcherLogger.Error(LifecycleLogCategory, "beforeAssemblyReload 处理失败。", ex);
            }
        }

        private static void OnAfterAssemblyReload()
        {
            try
            {
                _identity = DevHubDispatcherIdentity.LoadOrCreate();
                StartRuntime("after-assembly-reload");
            }
            catch (Exception ex)
            {
                SetLastError("afterAssemblyReload failed: " + ex.Message);
                DevHubDispatcherLogger.Error(LifecycleLogCategory, "afterAssemblyReload 处理失败。", ex);
            }
        }

        private static void OnEditorQuitting()
        {
            try
            {
                StopRuntime(true, true);
            }
            catch (Exception ex)
            {
                SetLastError("editorQuitting failed: " + ex.Message);
                DevHubDispatcherLogger.Error(LifecycleLogCategory, "editorQuitting 处理失败。", ex);
            }
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

            DevHubDispatcherLogger.Info(LifecycleLogCategory, "runtime start: " + reason);
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
            try
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
            catch (Exception ex)
            {
                ResetRuntimeForRetry("editor-update", ex);
            }
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

            RuntimeEditorContext editorContext = RuntimeEditorContext.Capture();
            lock (SyncRoot)
            {
                _initializeTask = InitializeRuntimeAsync(_identity, editorContext, token);
            }
        }

        private static async Task<Result<RuntimeConnection>> InitializeRuntimeAsync(DevHubDispatcherIdentity identity, RuntimeEditorContext editorContext, CancellationToken cancellationToken)
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
                AppInstance instance = await client.RegisterInstanceAsync(BuildAppInstanceRegistration(identity, editorContext), identity.InstancePassword, cancellationToken);
                return Result<RuntimeConnection>.Ok(new RuntimeConnection(client, instance, client.Runtime.RuntimeTuning), string.Empty);
            }
            catch (OperationCanceledException ex)
            {
                if (client != null)
                {
                    try
                    {
                        await client.DisposeAsync();
                    }
                    catch (Exception disposeEx)
                    {
                        DevHubDispatcherLogger.Error(CleanupLogCategory, "initialize 取消后的 client 清理失败。", disposeEx);
                    }
                }

                return Result<RuntimeConnection>.Fail("initialize canceled: " + ex.Message);
            }
            catch (Exception ex)
            {
                if (client != null)
                {
                    try
                    {
                        await client.DisposeAsync();
                    }
                    catch (Exception disposeEx)
                    {
                        DevHubDispatcherLogger.Error(CleanupLogCategory, "initialize 失败后的 client 清理失败。", disposeEx);
                    }
                }

                return Result<RuntimeConnection>.Fail(ex.Message);
            }
        }

        private static void ProcessInitializeTask()
        {
            Task<Result<RuntimeConnection>> task;
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

            if (task.IsFaulted)
            {
                ScheduleInitializeRetry("initialize", GetTaskException(task));
                return;
            }

            if (task.IsCanceled)
            {
                ScheduleInitializeRetry("initialize", (Exception)null);
                return;
            }

            Result<RuntimeConnection> initializeResult = task.Result;
            if (!initializeResult.Success)
            {
                ScheduleInitializeRetry("initialize", initializeResult.Message);
                return;
            }

            RuntimeConnection connection = initializeResult.Value;
            DevHubClient oldClient = null;
            bool shouldDisposeConnection = false;
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

            DevHubDispatcherLogger.Info(LifecycleLogCategory, "dispatcher connected. AppId=" + _identity.AppId + ", InstanceId=" + _identity.InstanceId);
        }

        private static void ScheduleInitializeRetry(string stage, Exception exception)
        {
            SetLastError(stage + " failed: " + (exception == null ? "canceled" : exception.Message));
            lock (SyncRoot)
            {
                _hostConnected = false;
                _nextInitializeUtc = DateTime.UtcNow.AddSeconds(DefaultRetryDelaySeconds);
            }

            if (exception == null)
            {
                DevHubDispatcherLogger.Warning(LifecycleLogCategory, stage + " 已取消，将在 " + DefaultRetryDelaySeconds + " 秒后重试。");
                return;
            }

            DevHubDispatcherLogger.Error(LifecycleLogCategory, stage + " failed.", exception);
            DevHubDispatcherLogger.Warning(LifecycleLogCategory, stage + " 将在 " + DefaultRetryDelaySeconds + " 秒后重试。");
        }

        private static void ScheduleInitializeRetry(string stage, string reason)
        {
            string normalizedReason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason;
            SetLastError(stage + " failed: " + normalizedReason);
            lock (SyncRoot)
            {
                _hostConnected = false;
                _nextInitializeUtc = DateTime.UtcNow.AddSeconds(DefaultRetryDelaySeconds);
            }

            if (normalizedReason.IndexOf("canceled", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                DevHubDispatcherLogger.Warning(LifecycleLogCategory, stage + " 已取消，将在 " + DefaultRetryDelaySeconds + " 秒后重试。原因: " + normalizedReason);
                return;
            }

            DevHubDispatcherLogger.Error(LifecycleLogCategory, stage + " failed: " + normalizedReason);
            DevHubDispatcherLogger.Warning(LifecycleLogCategory, stage + " 将在 " + DefaultRetryDelaySeconds + " 秒后重试。");
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

                DevHubDispatcherLogger.Warning(LifecycleLogCategory, "heartbeat 已取消，将延后重试。");
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

                DevHubDispatcherLogger.Warning(LifecycleLogCategory, "poll 已取消，将延后重试。");
                return;
            }

            if (task.IsFaulted)
            {
                ResetRuntimeForRetry("poll", GetTaskException(task));
                return;
            }

            PollResult result = task.Result;
            lock (SyncRoot)
            {
                _hostConnected = true;
                _lastConnectedAtUtc = DateTime.UtcNow;
                _nextPollUtc = DateTime.UtcNow;
                _lastError = string.Empty;
            }

            for (int index = 0; index < result.Items.Count; index++)
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

            if (!TryReadEnvelope(invocation.Args, out string toolId, out JToken payload, out string envelopeError))
            {
                HandleRoutingFailure(invocation, InvalidDispatcherMessageCode, "invalid_dispatcher_message", null, envelopeError, null);
                return;
            }

            if (!TryGetTool(toolId, out IDevHubTool tool))
            {
                HandleRoutingFailure(invocation, ToolNotFoundCode, "tool_not_found", toolId, "Tool 未注册。", null);
                return;
            }

            try
            {
                if (invocation.Kind == InvocationKind.Request)
                {
                    EnqueueBackgroundTask(RespondWithValueAsync(invocation, toolId, tool.HandleDevHubRequest(invocation.Method, payload)));
                    return;
                }

                tool.HandleDevHubNotify(invocation.Method, payload);
            }
            catch (Exception ex)
            {
                HandleRoutingFailure(invocation, ToolHandlerFailedCode, "tool_handler_failed", toolId, ex.Message, ex);
            }
        }

        private static void HandleRoutingFailure(Invocation invocation, int code, string message, string toolId, string reason, Exception exception)
        {
            string logMessage = "Tool 路由失败。toolId=" + (toolId ?? string.Empty) + ", method=" + invocation.Method + ", code=" + code + ", reason=" + (reason ?? string.Empty);
            SetLastError(logMessage);
            DevHubDispatcherLogger.Error(RoutingLogCategory, logMessage, exception);

            if (invocation.Kind == InvocationKind.Request)
            {
                EnqueueBackgroundTask(RespondWithErrorAsync(invocation, code, message, toolId, reason));
            }
        }

        private static async Task RespondWithValueAsync(Invocation invocation, string toolId, JToken value)
        {
            if (!TryGetRespondContext(out DevHubClient client, out string instanceId, out CancellationToken token))
            {
                return;
            }

            try
            {
                await client.RespondAsync(new RespondRequest
                {
                    InstanceId = instanceId,
                    InvocationId = invocation.InvocationId,
                    Value = value ?? JValue.CreateNull()
                }, token);
            }
            catch (OperationCanceledException ex)
            {
                string message = "返回 request 响应已取消。toolId=" + (toolId ?? string.Empty) + ", method=" + invocation.Method;
                SetLastError(message);
                DevHubDispatcherLogger.Warning(OutboundLogCategory, message, ex);
            }
            catch (Exception ex)
            {
                string message = "返回 request 响应失败。toolId=" + (toolId ?? string.Empty) + ", method=" + invocation.Method;
                SetLastError(message);
                DevHubDispatcherLogger.Error(OutboundLogCategory, message, ex);
            }
        }

        private static async Task RespondWithErrorAsync(Invocation invocation, int code, string message, string toolId, string reason)
        {
            if (!TryGetRespondContext(out DevHubClient client, out string instanceId, out CancellationToken token))
            {
                return;
            }

            try
            {
                await client.RespondAsync(new RespondRequest
                {
                    InstanceId = instanceId,
                    InvocationId = invocation.InvocationId,
                    Error = DevHubCalleeError.Create(code, message, new
                    {
                        toolId = toolId ?? string.Empty,
                        method = invocation.Method,
                        reason = reason ?? string.Empty
                    })
                }, token);
            }
            catch (OperationCanceledException ex)
            {
                string errorMessage = "返回错误响应已取消。toolId=" + (toolId ?? string.Empty) + ", method=" + invocation.Method;
                SetLastError(errorMessage);
                DevHubDispatcherLogger.Warning(OutboundLogCategory, errorMessage, ex);
            }
            catch (Exception ex)
            {
                string errorMessage = "返回错误响应失败。toolId=" + (toolId ?? string.Empty) + ", method=" + invocation.Method;
                SetLastError(errorMessage);
                DevHubDispatcherLogger.Error(OutboundLogCategory, errorMessage, ex);
            }
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
            for (int index = BackgroundTasks.Count - 1; index >= 0; index--)
            {
                Task task = BackgroundTasks[index];
                if (!task.IsCompleted)
                {
                    continue;
                }

                BackgroundTasks.RemoveAt(index);
                if (task.IsFaulted)
                {
                    Exception exception = GetTaskException(task);
                    string message = "background task failed: " + (exception == null ? "unknown" : exception.Message);
                    SetLastError(message);
                    DevHubDispatcherLogger.Error(BackgroundLogCategory, message, exception);
                }
            }
        }

        private static void ResetRuntimeForRetry(string stage, Exception exception)
        {
            SetLastError(stage + " failed: " + (exception == null ? "unknown" : exception.Message));
            DevHubDispatcherLogger.Error(LifecycleLogCategory, stage + " failed.", exception);
            StopRuntime(false, false);
            StartRuntime(stage + "-retry");
            lock (SyncRoot)
            {
                _hostConnected = false;
                _nextInitializeUtc = DateTime.UtcNow.AddSeconds(DefaultRetryDelaySeconds);
            }

            DevHubDispatcherLogger.Warning(LifecycleLogCategory, stage + " 将在 " + DefaultRetryDelaySeconds + " 秒后重试。");
        }

        private static bool TryReadEnvelope(JToken args, out string toolId, out JToken payload, out string error)
        {
            toolId = null;
            payload = null;
            error = null;

            JObject obj = args as JObject;
            if (obj == null)
            {
                error = "args 必须是对象。";
                return false;
            }

            JToken toolIdToken = obj["toolId"];
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

        private static bool TryValidateToolRegistration(IDevHubTool tool, out Result validationResult)
        {
            if (tool == null)
            {
                validationResult = LogFailure(ToolRegistryLogCategory, "tool 不能为空。");
                return false;
            }

            if (string.IsNullOrWhiteSpace(tool.ToolId))
            {
                validationResult = LogFailure(ToolRegistryLogCategory, "toolId 不能为空。");
                return false;
            }

            validationResult = Result.Ok(string.Empty);
            return true;
        }

        private static bool TryCreateDelegateTool(string toolId, DevHubRequestHandler requestHandler, DevHubNotifyHandler notifyHandler, out DevHubToolDelegate tool, out Result validationResult)
        {
            tool = null;
            if (string.IsNullOrWhiteSpace(toolId))
            {
                validationResult = LogFailure(ToolRegistryLogCategory, "toolId 不能为空。");
                return false;
            }

            if (requestHandler == null && notifyHandler == null)
            {
                validationResult = LogFailure(ToolRegistryLogCategory, "requestHandler 和 notifyHandler 不能同时为空。");
                return false;
            }

            tool = new DevHubToolDelegate(toolId, requestHandler, notifyHandler);
            validationResult = Result.Ok(string.Empty);
            return true;
        }

        private static Result RegisterToolCore(IDevHubTool tool)
        {
            lock (SyncRoot)
            {
                if (ToolIdsByInstance.ContainsKey(tool))
                {
                    return LogFailure(ToolRegistryLogCategory, "该 Tool 实例已经注册。");
                }

                if (ToolsById.ContainsKey(tool.ToolId))
                {
                    return LogFailure(ToolRegistryLogCategory, "toolId 已经被注册: " + tool.ToolId);
                }

                AddToolRegistration(tool);
            }

            DevHubDispatcherLogger.Info(ToolRegistryLogCategory, "Tool 已注册。toolId=" + tool.ToolId);
            return Result.Ok("Tool 已注册: " + tool.ToolId);
        }

        private static void AddToolRegistration(IDevHubTool tool)
        {
            ToolsById.Add(tool.ToolId, tool);
            ToolIdsByInstance.Add(tool, tool.ToolId);
        }

        private static void RemoveToolRegistration(IDevHubTool tool, string toolId)
        {
            ToolIdsByInstance.Remove(tool);
            ToolsById.Remove(toolId);
        }

        private static bool TryGetRegisteredToolId(IDevHubTool tool, out string toolId, out Result result)
        {
            toolId = null;
            result = Result.Ok(string.Empty);
            if (tool == null)
            {
                result = Result.Fail("tool 不能为空。");
                return false;
            }

            lock (SyncRoot)
            {
                if (!ToolIdsByInstance.TryGetValue(tool, out toolId))
                {
                    result = Result.Fail("Tool 尚未注册到 DevHubDispatcher。");
                    return false;
                }
            }

            return true;
        }

        private static bool TryGetConnectedClient(out DevHubClient client, out string error)
        {
            lock (SyncRoot)
            {
                if (_client == null || !_hostConnected)
                {
                    client = null;
                    error = "DevHub dispatcher 尚未建立 Host 连接。";
                    return false;
                }

                client = _client;
                error = null;
                return true;
            }
        }

        private static bool TryGetRespondContext(out DevHubClient client, out string instanceId, out CancellationToken token)
        {
            lock (SyncRoot)
            {
                if (_client == null || _identity == null)
                {
                    client = null;
                    instanceId = null;
                    token = CancellationToken.None;
                    return false;
                }

                client = _client;
                instanceId = _identity.InstanceId;
                token = _lifetimeCts?.Token ?? CancellationToken.None;
                return true;
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

        private static bool TryBuildInvokeRequest(string appId, string method, JObject envelope, DevHubDispatcherSendOptions options, out InvokeRequest request, out string error)
        {
            request = null;
            error = null;

            if (string.IsNullOrWhiteSpace(appId))
            {
                error = "appId 不能为空。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(method))
            {
                error = "method 不能为空。";
                return false;
            }

            request = new InvokeRequest
            {
                AppId = appId,
                Method = method,
                Args = envelope
            };

            if (options == null)
            {
                return true;
            }

            if (!string.IsNullOrEmpty(options.Scope) || !string.IsNullOrEmpty(options.InstanceId))
            {
                if (!TryNormalizeOptionalString(options.Scope, "options.Scope", out string scope, out error))
                {
                    request = null;
                    return false;
                }

                if (!TryNormalizeOptionalString(options.InstanceId, "options.InstanceId", out string instanceId, out error))
                {
                    request = null;
                    return false;
                }

                request.Target = new InvocationTarget
                {
                    Scope = scope,
                    InstanceId = instanceId
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

            return true;
        }

        private static bool TryNormalizeOptionalString(string value, string parameterName, out string normalizedValue, out string error)
        {
            if (value == null)
            {
                normalizedValue = null;
                error = null;
                return true;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                normalizedValue = null;
                error = parameterName + " 不能是空白字符串。";
                return false;
            }

            normalizedValue = value;
            error = null;
            return true;
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

            int leaseSeconds = tuning.LeaseSeconds > 0 ? tuning.LeaseSeconds : 30;
            int onlineThresholdSeconds = tuning.OnlineThresholdSeconds > 0 ? tuning.OnlineThresholdSeconds : leaseSeconds;
            return TimeSpan.FromSeconds(Math.Max(1, Math.Min(leaseSeconds, onlineThresholdSeconds) / 2));
        }

        private static int ResolvePollWaitMs(HubRuntimeTuning tuning)
        {
            int leaseSeconds = tuning == null || tuning.LeaseSeconds <= 0 ? 30 : tuning.LeaseSeconds;
            return Math.Max(1000, Math.Min(5000, leaseSeconds * 1000 / 2));
        }

        private static void TryUnregisterInstance(DevHubClient client, DevHubDispatcherIdentity identity)
        {
            try
            {
                client.UnregisterInstanceAsync(identity.InstanceId, identity.InstancePassword).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException ex)
            {
                DevHubDispatcherLogger.Warning(CleanupLogCategory, "unregisterInstance 已取消。", ex);
            }
            catch (Exception ex)
            {
                SetLastError("unregisterInstance failed: " + ex.Message);
                DevHubDispatcherLogger.Error(CleanupLogCategory, "unregisterInstance failed.", ex);
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
            catch (OperationCanceledException ex)
            {
                DevHubDispatcherLogger.Warning(CleanupLogCategory, "dispose 已取消。", ex);
            }
            catch (Exception ex)
            {
                SetLastError("dispose failed: " + ex.Message);
                DevHubDispatcherLogger.Error(CleanupLogCategory, "dispose failed.", ex);
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
                AggregateException ignored = t.Exception;
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private static Exception GetTaskException(Task task)
        {
            return task.Exception?.GetBaseException();
        }

        private static void SetLastError(string message)
        {
            lock (SyncRoot)
            {
                _lastError = message ?? string.Empty;
            }
        }

        private static Result LogFailure(string category, string message, Exception exception = null)
        {
            SetLastError(message);
            DevHubDispatcherLogger.Error(category, message, exception);
            return Result.Fail(message);
        }

        private static Result<T> LogFailure<T>(string category, string message, Exception exception = null)
        {
            SetLastError(message);
            DevHubDispatcherLogger.Error(category, message, exception);
            return Result<T>.Fail(message);
        }

        private static string BuildOutboundFailureMessage(string toolId, string method, string reason)
        {
            return "Tool 调用失败。toolId=" + (toolId ?? string.Empty) + ", method=" + (method ?? string.Empty) + ", reason=" + (reason ?? string.Empty);
        }
    }
}
