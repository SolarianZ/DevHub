import type { AppDefinition, AppInstance } from "@devhub/sdk";
import type { DefinitionFormState } from "../lib/definition-form";
import type {
  BootstrapSnapshot,
  LogFileInfo,
  LogKind,
  LogReadResult,
  MonitorSettings,
  SettingsSnapshot,
} from "../lib/models";
import {
  type DefinitionDialogState,
  type HostSessionStatus,
  type RoutePage,
  type SettingsFieldErrors,
  formatBootstrapDescription,
  formatBootstrapHeadline,
  formatBytes,
  formatDataDirSource,
  formatDefinitionCapabilities,
  formatPhaseLabel,
  formatRelativeTime,
  formatScope,
  formatSessionLabel,
  getRuntimePort,
  isInstanceOffline,
} from "../lib/monitor-ui";

interface AppShellProps {
  route: RoutePage;
  canOpenStatus: boolean;
  activeError: string | null;
  bootstrap: BootstrapSnapshot | null;
  bootstrapBusy: boolean;
  settings: SettingsSnapshot | null;
  settingsDraft: MonitorSettings;
  settingsDirty: boolean;
  settingsFieldErrors: SettingsFieldErrors;
  settingsBusy: boolean;
  hostSessionStatus: HostSessionStatus;
  definitions: AppDefinition[];
  instances: AppInstance[];
  inventoryMessage: string;
  nowTick: number;
  activeLogKind: LogKind;
  monitorLogDirectory: string | null;
  visibleLogs: LogFileInfo[];
  selectedLog: LogReadResult | null;
  logsBusy: boolean;
  definitionDialog: DefinitionDialogState | null;
  onNavigate: (route: RoutePage) => void;
  onResumeDiscovery: () => void;
  onLaunchHost: () => void;
  onChangeSettingsField: (field: keyof MonitorSettings, value: string) => void;
  onSaveSettings: () => void;
  onRefreshLogs: () => void;
  onSelectLogKind: (kind: LogKind) => void;
  onOpenLog: (kind: LogKind, fileName: string) => void;
  onAddDefinition: () => void;
  onEditDefinition: (appId: string) => void;
  onViewInstanceDefinition: (instance: AppInstance) => void;
  onChangeDefinitionField: (field: keyof DefinitionFormState, value: string | boolean) => void;
  onCloseDefinitionDialog: () => void;
  onDeleteDefinition: () => void;
  onSubmitDefinition: () => void;
}

export function AppShell(props: AppShellProps) {
  const {
    route,
    canOpenStatus,
    activeError,
    bootstrap,
    bootstrapBusy,
    settings,
    settingsDraft,
    settingsDirty,
    settingsFieldErrors,
    settingsBusy,
    hostSessionStatus,
    definitions,
    instances,
    inventoryMessage,
    nowTick,
    activeLogKind,
    monitorLogDirectory,
    visibleLogs,
    selectedLog,
    logsBusy,
    definitionDialog,
    onNavigate,
    onResumeDiscovery,
    onLaunchHost,
    onChangeSettingsField,
    onSaveSettings,
    onRefreshLogs,
    onSelectLogKind,
    onOpenLog,
    onAddDefinition,
    onEditDefinition,
    onViewInstanceDefinition,
    onChangeDefinitionField,
    onCloseDefinitionDialog,
    onDeleteDefinition,
    onSubmitDefinition,
  } = props;

  return (
    <main className="app-shell">
      <header className="shell-header">
        <div className="shell-title">
          <p className="eyebrow">DevHub Monitor</p>
          <h1>Desktop workflow shell</h1>
          <p className="hero-copy">
            Monitor 前端通过壳层装配各条工作流：初始化探测、Host 会话、定义管理、设置保存与日志排障分别由独立控制器负责。
          </p>
        </div>
        <div className="shell-status">
          <span className={`phase phase-${bootstrap?.phase ?? "loading"}`}>
            {formatPhaseLabel(bootstrap?.phase)}
          </span>
          <span className={`session-badge session-${hostSessionStatus}`}>
            {formatSessionLabel(hostSessionStatus)}
          </span>
        </div>
      </header>

      <nav className="route-bar" aria-label="Monitor navigation">
        <RouteButton active={route === "bootstrap"} label="初始化" onClick={() => onNavigate("bootstrap")} />
        <RouteButton
          active={route === "status"}
          label="状态"
          disabled={!canOpenStatus}
          onClick={() => onNavigate("status")}
        />
        <RouteButton active={route === "settings"} label="设置" onClick={() => onNavigate("settings")} />
        <RouteButton active={route === "logs"} label="日志" onClick={() => onNavigate("logs")} />
      </nav>

      <section className="summary-grid">
        <article className="summary-card">
          <span className="summary-label">生效数据目录</span>
          <strong>{bootstrap?.effectiveDataDir ?? settings?.effectiveDataDir ?? "加载中"}</strong>
          <span className="summary-meta">
            来源：{formatDataDirSource(bootstrap?.dataDirSource ?? settings?.dataDirSource)}
          </span>
        </article>
        <article className="summary-card">
          <span className="summary-label">当前端口</span>
          <strong>{bootstrap?.connection ? getRuntimePort(bootstrap.connection) : "未连接"}</strong>
          <span className="summary-meta">
            {bootstrap?.connection?.rpcEndpoint ?? "等待验证可用 Host。"}
          </span>
        </article>
        <article className="summary-card">
          <span className="summary-label">定义 / 实例</span>
          <strong>
            {definitions.length} / {instances.length}
          </strong>
          <span className="summary-meta">{inventoryMessage}</span>
        </article>
      </section>

      {route === "bootstrap" ? (
        <BootstrapPage
          bootstrap={bootstrap}
          busy={bootstrapBusy}
          onResumeDiscovery={onResumeDiscovery}
          onLaunchHost={onLaunchHost}
          onOpenSettings={() => onNavigate("settings")}
        />
      ) : null}

      {route === "status" ? (
        <StatusPage
          bootstrap={bootstrap}
          definitions={definitions}
          hostSessionStatus={hostSessionStatus}
          instances={instances}
          nowTick={nowTick}
          onAddDefinition={onAddDefinition}
          onEditDefinition={onEditDefinition}
          onViewInstanceDefinition={onViewInstanceDefinition}
        />
      ) : null}

      {route === "settings" ? (
        <SettingsPage
          bootstrap={bootstrap}
          busy={settingsBusy}
          fieldErrors={settingsFieldErrors}
          settings={settings}
          settingsDirty={settingsDirty}
          settingsDraft={settingsDraft}
          onChangeField={onChangeSettingsField}
          onSave={onSaveSettings}
        />
      ) : null}

      {route === "logs" ? (
        <LogsPage
          activeLogKind={activeLogKind}
          busy={logsBusy}
          monitorLogDirectory={monitorLogDirectory}
          selectedLog={selectedLog}
          visibleLogs={visibleLogs}
          onOpenLog={onOpenLog}
          onRefresh={onRefreshLogs}
          onSelectKind={onSelectLogKind}
        />
      ) : null}

      {definitionDialog ? (
        <DefinitionDialog
          dialog={definitionDialog}
          onChangeField={onChangeDefinitionField}
          onClose={onCloseDefinitionDialog}
          onDelete={onDeleteDefinition}
          onSubmit={onSubmitDefinition}
        />
      ) : null}

      {activeError ? <div className="error-banner">{activeError}</div> : null}
    </main>
  );
}

function BootstrapPage(props: {
  bootstrap: BootstrapSnapshot | null;
  busy: boolean;
  onResumeDiscovery: () => void;
  onLaunchHost: () => void;
  onOpenSettings: () => void;
}) {
  const { bootstrap, busy, onResumeDiscovery, onLaunchHost, onOpenSettings } = props;

  return (
    <section className="page-grid bootstrap-grid">
      <article className="panel panel-primary">
        <header className="panel-header">
          <div>
            <p className="eyebrow">Initialization</p>
            <h2>扫描与启动流程</h2>
          </div>
          <div className="button-row">
            <button type="button" onClick={onResumeDiscovery} disabled={busy}>
              重新扫描
            </button>
            <button
              type="button"
              onClick={onLaunchHost}
              disabled={busy || bootstrap?.phase === "host_available"}
            >
              启动 DevHub Host
            </button>
            <button type="button" className="button-secondary" onClick={onOpenSettings} disabled={busy}>
              打开设置
            </button>
          </div>
        </header>

        <div className="status-banner">
          <strong>{formatBootstrapHeadline(bootstrap?.phase)}</strong>
          <p>{formatBootstrapDescription(bootstrap)}</p>
        </div>

        <dl className="detail-list">
          <div>
            <dt>有效数据目录</dt>
            <dd>{bootstrap?.effectiveDataDir ?? "加载中"}</dd>
          </div>
          <div>
            <dt>目录来源</dt>
            <dd>{formatDataDirSource(bootstrap?.dataDirSource)}</dd>
          </div>
          <div>
            <dt>Host 可执行文件</dt>
            <dd>{bootstrap?.settings.hostExecutablePath ?? "未配置"}</dd>
          </div>
          <div>
            <dt>连接状态</dt>
            <dd>{bootstrap?.connection?.rpcEndpoint ?? "尚未发现可用 Host。"}</dd>
          </div>
        </dl>
      </article>

      <article className="panel">
        <header className="panel-header">
          <h2>流程断点</h2>
        </header>
        <div className="stack-list">
          <div className="stack-item">
            <strong>3 秒后显示启动按钮</strong>
            <p>当前 phase 为 `launch_available` 时，初始化页会继续扫描，但允许用户主动拉起 Host。</p>
          </div>
          <div className="stack-item">
            <strong>缺少 Host 路径自动转设置</strong>
            <p>原生层返回 `settings_required` 时，前端立即切到设置页，而不是继续发起启动。</p>
          </div>
          <div className="stack-item">
            <strong>断线回退初始化</strong>
            <p>状态页关键会话断开时会释放客户端并恢复扫描，避免停留在过期运行时上。</p>
          </div>
        </div>
      </article>

      <article className="panel">
        <header className="panel-header">
          <h2>最近问题</h2>
        </header>
        <p className="problem-card">
          {bootstrap?.lastProblem?.message ?? "当前没有记录到扫描或连接问题。"}
        </p>
      </article>
    </section>
  );
}

function StatusPage(props: {
  bootstrap: BootstrapSnapshot | null;
  definitions: AppDefinition[];
  hostSessionStatus: HostSessionStatus;
  instances: AppInstance[];
  nowTick: number;
  onAddDefinition: () => void;
  onEditDefinition: (appId: string) => void;
  onViewInstanceDefinition: (instance: AppInstance) => void;
}) {
  const {
    bootstrap,
    definitions,
    hostSessionStatus,
    instances,
    nowTick,
    onAddDefinition,
    onEditDefinition,
    onViewInstanceDefinition,
  } = props;

  return (
    <section className="page-grid status-grid">
      <article className="panel panel-primary">
        <header className="panel-header">
          <div>
            <p className="eyebrow">Status</p>
            <h2>连接摘要</h2>
          </div>
          <span className={`session-badge session-${hostSessionStatus}`}>
            {formatSessionLabel(hostSessionStatus)}
          </span>
        </header>

        <dl className="detail-list">
          <div>
            <dt>HTTP RPC</dt>
            <dd>{bootstrap?.connection?.rpcEndpoint ?? "未连接"}</dd>
          </div>
          <div>
            <dt>WebSocket</dt>
            <dd>{bootstrap?.connection?.websocketEndpoint ?? "未连接"}</dd>
          </div>
          <div>
            <dt>Host PID</dt>
            <dd>{bootstrap?.connection?.runtime.pid ?? "未连接"}</dd>
          </div>
          <div>
            <dt>运行时目录</dt>
            <dd>{bootstrap?.connection?.runtimeDirectory ?? "未连接"}</dd>
          </div>
        </dl>
      </article>

      <article className="panel panel-span">
        <header className="panel-header">
          <div>
            <p className="eyebrow">App Definitions</p>
            <h2>定义列表</h2>
          </div>
          <button type="button" onClick={onAddDefinition}>
            新增定义
          </button>
        </header>

        {definitions.length === 0 ? (
          <EmptyState
            title="当前没有已注册定义"
            description="连接建立后会拉取完整的 Definition 列表，后续变更会通过事件触发自动刷新。"
          />
        ) : (
          <div className="inventory-list">
            {definitions.map((definition) => (
              <article key={definition.appId} className="inventory-item">
                <div className="inventory-main">
                  <div className="inventory-heading">
                    <strong>{definition.displayName}</strong>
                    <span className="chip">{definition.appId}</span>
                  </div>
                  <p>{definition.description ?? "未提供描述。"}</p>
                  <div className="chip-row">
                    {formatDefinitionCapabilities(definition).map((item) => (
                      <span key={`${definition.appId}-${item}`} className="chip subtle-chip">
                        {item}
                      </span>
                    ))}
                  </div>
                </div>
                <div className="inventory-actions">
                  <button type="button" className="button-secondary" onClick={() => onEditDefinition(definition.appId)}>
                    编辑
                  </button>
                </div>
              </article>
            ))}
          </div>
        )}
      </article>

      <article className="panel panel-span">
        <header className="panel-header">
          <div>
            <p className="eyebrow">App Instances</p>
            <h2>跨 scope 实例</h2>
          </div>
          <span className="subtle">包含离线实例</span>
        </header>

        {instances.length === 0 ? (
          <EmptyState
            title="当前没有实例"
            description="状态页会通过 `includeAllScopes=true` 和 `includeOffline=true` 拉取完整实例镜像。"
          />
        ) : (
          <div className="inventory-list">
            {instances.map((instance) => {
              const offline = isInstanceOffline(instance, bootstrap?.connection, nowTick);

              return (
                <article key={instance.instanceId} className="inventory-item">
                  <div className="inventory-main">
                    <div className="inventory-heading">
                      <strong>{instance.instanceId}</strong>
                      <span className={`chip ${offline ? "chip-warning" : "chip-success"}`}>
                        {offline ? "离线" : "在线"}
                      </span>
                    </div>
                    <p>
                      {instance.appId} · scope {formatScope(instance.scope)} · PID {instance.pid}
                    </p>
                    <div className="chip-row">
                      <span className="chip subtle-chip">
                        最近心跳 {formatRelativeTime(instance.lastSeenUtc, nowTick)}
                      </span>
                      <span className="chip subtle-chip">
                        invoke: {instance.invoke.poll ? "poll" : "no-poll"} /{" "}
                        {instance.invoke.respond ? "respond" : "no-respond"}
                      </span>
                    </div>
                  </div>
                  <div className="inventory-actions">
                    <button
                      type="button"
                      className="button-secondary"
                      onClick={() => onViewInstanceDefinition(instance)}
                    >
                      查看定义
                    </button>
                  </div>
                </article>
              );
            })}
          </div>
        )}
      </article>
    </section>
  );
}

function SettingsPage(props: {
  bootstrap: BootstrapSnapshot | null;
  busy: boolean;
  fieldErrors: SettingsFieldErrors;
  settings: SettingsSnapshot | null;
  settingsDirty: boolean;
  settingsDraft: MonitorSettings;
  onChangeField: (field: keyof MonitorSettings, value: string) => void;
  onSave: () => void;
}) {
  const { bootstrap, busy, fieldErrors, settings, settingsDirty, settingsDraft, onChangeField, onSave } = props;

  return (
    <section className="page-grid settings-grid">
      <article className="panel panel-primary">
        <header className="panel-header">
          <div>
            <p className="eyebrow">Settings</p>
            <h2>Monitor 运行时设置</h2>
          </div>
          <button type="button" onClick={onSave} disabled={busy || !settingsDirty}>
            保存设置
          </button>
        </header>

        <div className="form-grid">
          <label className="field">
            <span>DEVHUB_DATA_DIR 覆盖值</span>
            <input
              type="text"
              value={settingsDraft.dataDirOverride ?? ""}
              placeholder="留空表示使用环境变量或平台默认目录"
              onChange={(event) => onChangeField("dataDirOverride", event.target.value)}
            />
            <FieldError message={fieldErrors.dataDirOverride} />
          </label>

          <label className="field">
            <span>Host 可执行文件路径</span>
            <input
              type="text"
              value={settingsDraft.hostExecutablePath ?? ""}
              placeholder="例如 D:\\path\\to\\DevHub.Host.exe"
              onChange={(event) => onChangeField("hostExecutablePath", event.target.value)}
            />
            <FieldError message={fieldErrors.hostExecutablePath} />
          </label>
        </div>
      </article>

      <article className="panel">
        <header className="panel-header">
          <h2>当前解析结果</h2>
        </header>
        <dl className="detail-list">
          <div>
            <dt>设置文件</dt>
            <dd>{settings?.settingsFilePath ?? "加载中"}</dd>
          </div>
          <div>
            <dt>生效数据目录</dt>
            <dd>{settings?.effectiveDataDir ?? bootstrap?.effectiveDataDir ?? "加载中"}</dd>
          </div>
          <div>
            <dt>目录来源</dt>
            <dd>{formatDataDirSource(settings?.dataDirSource ?? bootstrap?.dataDirSource)}</dd>
          </div>
          <div>
            <dt>Monitor 日志目录</dt>
            <dd>{settings?.monitorLogDirectory ?? "加载中"}</dd>
          </div>
        </dl>
      </article>
    </section>
  );
}

function LogsPage(props: {
  activeLogKind: LogKind;
  busy: boolean;
  monitorLogDirectory: string | null;
  selectedLog: LogReadResult | null;
  visibleLogs: LogFileInfo[];
  onOpenLog: (kind: LogKind, fileName: string) => void;
  onRefresh: () => void;
  onSelectKind: (kind: LogKind) => void;
}) {
  const {
    activeLogKind,
    busy,
    monitorLogDirectory,
    selectedLog,
    visibleLogs,
    onOpenLog,
    onRefresh,
    onSelectKind,
  } = props;

  return (
    <section className="page-grid logs-grid-page">
      <article className="panel panel-span">
        <header className="panel-header">
          <div>
            <p className="eyebrow">Logs</p>
            <h2>Host / Monitor 双视图</h2>
          </div>
          <div className="button-row">
            <button
              type="button"
              className={activeLogKind === "monitor" ? "active" : ""}
              onClick={() => onSelectKind("monitor")}
            >
              Monitor 日志
            </button>
            <button
              type="button"
              className={activeLogKind === "host" ? "active" : ""}
              onClick={() => onSelectKind("host")}
            >
              Host 日志
            </button>
            <button type="button" className="button-secondary" onClick={onRefresh} disabled={busy}>
              刷新
            </button>
          </div>
        </header>

        <div className="status-banner compact-banner">
          <strong>{activeLogKind === "monitor" ? "Monitor 结构化日志" : "Host 运行日志"}</strong>
          <p>
            {activeLogKind === "monitor"
              ? monitorLogDirectory ?? "加载中"
              : "当前有效 DEVHUB_DATA_DIR/logs/ 下的日志文件会显示在这里。"}
          </p>
        </div>

        <div className="logs-grid">
          <div className="log-list">
            {visibleLogs.length === 0 ? (
              <EmptyState
                title="当前没有可读取的日志文件"
                description="切换页面或执行启动、设置保存、定义管理等关键路径后，这里会出现最新日志。"
              />
            ) : (
              visibleLogs.map((file) => (
                <button
                  key={`${file.kind}-${file.name}`}
                  type="button"
                  className={`log-item ${
                    selectedLog?.kind === file.kind && selectedLog.fileName === file.name
                      ? "selected"
                      : ""
                  }`}
                  onClick={() => onOpenLog(file.kind, file.name)}
                >
                  <span>{file.name}</span>
                  <span className="subtle">{formatBytes(file.sizeBytes)}</span>
                </button>
              ))
            )}
          </div>

          <div className="log-content">
            <div className="log-meta">
              <strong>{selectedLog?.fileName ?? "未选择日志文件"}</strong>
              <span className="subtle">{selectedLog?.filePath ?? "请选择左侧日志。"}</span>
            </div>
            <pre>{selectedLog?.contents ?? "暂无内容。"}</pre>
          </div>
        </div>
      </article>
    </section>
  );
}

function DefinitionDialog(props: {
  dialog: DefinitionDialogState;
  onChangeField: (field: keyof DefinitionFormState, value: string | boolean) => void;
  onClose: () => void;
  onDelete: () => void;
  onSubmit: () => void;
}) {
  const { dialog, onChangeField, onClose, onDelete, onSubmit } = props;
  const submitLabel = dialog.mode === "create" ? "创建定义" : "保存修改";
  const canDelete = dialog.mode === "edit" && !dialog.readOnly && !dialog.missing;
  const disableInputs = dialog.readOnly || dialog.loading || dialog.saving;

  return (
    <div className="modal-backdrop" role="presentation">
      <section className="modal-card" role="dialog" aria-modal="true" aria-labelledby="definition-dialog-title">
        <header className="modal-header">
          <div>
            <p className="eyebrow">App Definition</p>
            <h2 id="definition-dialog-title">{dialog.title}</h2>
            <p className="modal-subtitle">{dialog.subtitle}</p>
          </div>
          <button type="button" className="button-secondary" onClick={onClose}>
            关闭
          </button>
        </header>

        {dialog.submitError ? <div className="inline-error">{dialog.submitError}</div> : null}

        {dialog.loading ? (
          <div className="empty-state modal-state">
            <h3>正在读取定义</h3>
            <p>状态页会优先拉取最新持久化定义，再决定是否进入编辑或只读查看。</p>
          </div>
        ) : dialog.missing ? (
          <div className="empty-state modal-state">
            <h3>定义不可用</h3>
            <p>{dialog.emptyStateMessage}</p>
          </div>
        ) : (
          <div className="modal-body">
            <div className="form-grid">
              <label className="field">
                <span>App ID</span>
                <input
                  type="text"
                  value={dialog.form.appId}
                  disabled={disableInputs || dialog.mode !== "create"}
                  onChange={(event) => onChangeField("appId", event.target.value)}
                />
                <FieldIssues issues={dialog.fieldErrors["definition.appId"]} />
              </label>

              <label className="field">
                <span>显示名称</span>
                <input
                  type="text"
                  value={dialog.form.displayName}
                  disabled={disableInputs}
                  onChange={(event) => onChangeField("displayName", event.target.value)}
                />
                <FieldIssues issues={dialog.fieldErrors["definition.displayName"]} />
              </label>

              <label className="field field-full">
                <span>描述</span>
                <textarea
                  rows={3}
                  value={dialog.form.description}
                  disabled={disableInputs}
                  onChange={(event) => onChangeField("description", event.target.value)}
                />
                <FieldIssues issues={dialog.fieldErrors["definition.description"]} />
              </label>
            </div>

            <section className="section-block">
              <header className="section-header">
                <h3>Capabilities</h3>
              </header>
              <div className="toggle-grid">
                <label className="toggle-item">
                  <input
                    type="checkbox"
                    checked={dialog.form.enableRpc}
                    disabled={disableInputs}
                    onChange={(event) => onChangeField("enableRpc", event.target.checked)}
                  />
                  <span>RPC</span>
                </label>
                <label className="toggle-item">
                  <input
                    type="checkbox"
                    checked={dialog.form.enableEvents}
                    disabled={disableInputs}
                    onChange={(event) => onChangeField("enableEvents", event.target.checked)}
                  />
                  <span>Events</span>
                </label>
              </div>
              <FieldIssues issues={dialog.fieldErrors["definition.capabilities"]} />
              <FieldIssues issues={dialog.fieldErrors["definition.capabilities.rpc"]} />
              <FieldIssues issues={dialog.fieldErrors["definition.capabilities.events"]} />
            </section>

            <section className="section-block">
              <header className="section-header">
                <h3>Launch</h3>
                <label className="toggle-item">
                  <input
                    type="checkbox"
                    checked={dialog.form.enableLaunch}
                    disabled={disableInputs}
                    onChange={(event) => onChangeField("enableLaunch", event.target.checked)}
                  />
                  <span>启用 launch 配置</span>
                </label>
              </header>
              {dialog.form.enableLaunch ? (
                <div className="form-grid">
                  <label className="field">
                    <span>exePath</span>
                    <input
                      type="text"
                      value={dialog.form.launchExePath}
                      disabled={disableInputs}
                      onChange={(event) => onChangeField("launchExePath", event.target.value)}
                    />
                    <FieldIssues issues={dialog.fieldErrors["definition.launch.exePath"]} />
                  </label>
                  <label className="field">
                    <span>argsTemplate</span>
                    <input
                      type="text"
                      value={dialog.form.launchArgsTemplate}
                      disabled={disableInputs}
                      onChange={(event) => onChangeField("launchArgsTemplate", event.target.value)}
                    />
                    <FieldIssues issues={dialog.fieldErrors["definition.launch.argsTemplate"]} />
                  </label>
                  <label className="field">
                    <span>workingDirectory</span>
                    <input
                      type="text"
                      value={dialog.form.launchWorkingDirectory}
                      disabled={disableInputs}
                      onChange={(event) => onChangeField("launchWorkingDirectory", event.target.value)}
                    />
                    <FieldIssues issues={dialog.fieldErrors["definition.launch.workingDirectory"]} />
                  </label>
                  <label className="field">
                    <span>dedupeKeyTemplate</span>
                    <input
                      type="text"
                      value={dialog.form.launchDedupeKeyTemplate}
                      disabled={disableInputs}
                      onChange={(event) => onChangeField("launchDedupeKeyTemplate", event.target.value)}
                    />
                    <FieldIssues issues={dialog.fieldErrors["definition.launch.dedupeKeyTemplate"]} />
                  </label>
                </div>
              ) : (
                <p className="subtle">未启用 launch 配置时，将不会向 Host 提交 launch 字段。</p>
              )}
              <FieldIssues issues={dialog.fieldErrors["definition.launch"]} />
            </section>
          </div>
        )}

        <footer className="modal-footer">
          {canDelete ? (
            <button type="button" className="button-danger" onClick={onDelete} disabled={dialog.saving}>
              删除定义
            </button>
          ) : (
            <span className="subtle">
              {dialog.readOnly ? "只读模式不允许保存或删除。" : "删除操作仅在编辑现有定义时可用。"}
            </span>
          )}
          <div className="button-row">
            <button type="button" className="button-secondary" onClick={onClose} disabled={dialog.saving}>
              {dialog.readOnly ? "关闭" : "取消"}
            </button>
            {!dialog.readOnly && !dialog.missing ? (
              <button type="button" onClick={onSubmit} disabled={dialog.saving}>
                {dialog.saving ? "处理中..." : submitLabel}
              </button>
            ) : null}
          </div>
        </footer>
      </section>
    </div>
  );
}

function RouteButton(props: {
  active: boolean;
  disabled?: boolean;
  label: string;
  onClick: () => void;
}) {
  const { active, disabled, label, onClick } = props;

  return (
    <button
      type="button"
      className={`route-pill ${active ? "active" : ""}`}
      onClick={onClick}
      disabled={disabled}
    >
      {label}
    </button>
  );
}

function FieldIssues(props: {
  issues?: readonly { code: string; message: string }[];
}) {
  const { issues } = props;
  if (!issues || issues.length === 0) {
    return null;
  }

  return (
    <div className="field-issues">
      {issues.map((issue) => (
        <p key={`${issue.code}-${issue.message}`}>{issue.message}</p>
      ))}
    </div>
  );
}

function FieldError(props: {
  message?: string | null;
}) {
  const { message } = props;
  if (!message) {
    return null;
  }

  return (
    <div className="field-issues">
      <p>{message}</p>
    </div>
  );
}

function EmptyState(props: {
  title: string;
  description: string;
}) {
  const { title, description } = props;

  return (
    <div className="empty-state">
      <h3>{title}</h3>
      <p>{description}</p>
    </div>
  );
}
