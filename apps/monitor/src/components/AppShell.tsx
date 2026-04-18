import type { AppDefinition, AppInstance } from "@devhub/sdk";
import type { DefinitionFormState } from "../lib/definition-form";
import type {
  BootstrapSnapshot,
  LogKind,
  MonitorSettings,
  SettingsSnapshot,
} from "../lib/models";
import {
  type DefinitionDialogState,
  type HostSessionStatus,
  type PrimaryWorkspaceMode,
  type SettingsFieldErrors,
  formatBootstrapDescription,
  formatBootstrapHeadline,
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
  workspaceMode: PrimaryWorkspaceMode;
  helpMenuOpen: boolean;
  openingLogKind: LogKind | null;
  settingsOpen: boolean;
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
  definitionDialog: DefinitionDialogState | null;
  onOpenSettings: () => void;
  onCloseSettings: () => void;
  onToggleHelpMenu: () => void;
  onOpenLogDirectory: (kind: LogKind) => void;
  onResumeDiscovery: () => void;
  onLaunchHost: () => void;
  onChangeSettingsField: (field: keyof MonitorSettings, value: string) => void;
  onSaveSettings: () => void;
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
    workspaceMode,
    helpMenuOpen,
    openingLogKind,
    settingsOpen,
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
    definitionDialog,
    onOpenSettings,
    onCloseSettings,
    onToggleHelpMenu,
    onOpenLogDirectory,
    onResumeDiscovery,
    onLaunchHost,
    onChangeSettingsField,
    onSaveSettings,
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
          <h1>连接并管理你的 DevHub Host</h1>
          <p className="hero-copy">
            {workspaceMode === "status"
              ? "当前主界面会持续展示连接状态、应用定义和实例清单；辅助操作通过顶部菜单进入。"
              : "Monitor 会持续查找可用的 DevHub Host，并在连接就绪后自动切换到管理视图。"}
          </p>
        </div>

        <div className="shell-side">
          <nav className="menu-bar" aria-label="Monitor menu">
            <button type="button" className="menu-trigger" onClick={onOpenSettings}>
              设置
            </button>

            <div className="menu-group">
              <button
                type="button"
                className={`menu-trigger ${helpMenuOpen ? "active" : ""}`}
                aria-expanded={helpMenuOpen}
                aria-haspopup="menu"
                onClick={onToggleHelpMenu}
              >
                帮助
              </button>

              {helpMenuOpen ? (
                <div className="menu-popover" role="menu" aria-label="帮助菜单">
                  <button
                    type="button"
                    role="menuitem"
                    className="menu-item"
                    onClick={() => onOpenLogDirectory("host")}
                    disabled={openingLogKind !== null}
                  >
                    {openingLogKind === "host" ? "正在打开 Host 日志..." : "打开 Host 日志"}
                  </button>
                  <button
                    type="button"
                    role="menuitem"
                    className="menu-item"
                    onClick={() => onOpenLogDirectory("monitor")}
                    disabled={openingLogKind !== null}
                  >
                    {openingLogKind === "monitor" ? "正在打开 Monitor 日志..." : "打开 Monitor 日志"}
                  </button>
                </div>
              ) : null}
            </div>
          </nav>

          <div className="shell-status">
            <span className={`phase phase-${bootstrap?.phase ?? "loading"}`}>
              {formatPhaseLabel(bootstrap?.phase)}
            </span>
            <span className={`session-badge session-${hostSessionStatus}`}>
              {formatSessionLabel(hostSessionStatus)}
            </span>
          </div>
        </div>
      </header>

      <section className="summary-grid">
        <article className="summary-card">
          <span className="summary-label">当前数据目录</span>
          <strong>{bootstrap?.effectiveDataDir ?? settings?.effectiveDataDir ?? "加载中"}</strong>
          <span className="summary-meta">
            来源：{formatDataDirSource(bootstrap?.dataDirSource ?? settings?.dataDirSource)}
          </span>
        </article>

        <article className="summary-card">
          <span className="summary-label">当前连接</span>
          <strong>{bootstrap?.connection ? getRuntimePort(bootstrap.connection) : "未连接"}</strong>
          <span className="summary-meta">
            {bootstrap?.connection?.rpcEndpoint ?? "等待发现并验证可用的 Host。"}
          </span>
        </article>

        <article className="summary-card">
          <span className="summary-label">资产概览</span>
          <strong>
            {definitions.length} 个定义 / {instances.length} 个实例
          </strong>
          <span className="summary-meta">{inventoryMessage}</span>
        </article>
      </section>

      {workspaceMode === "bootstrap" ? (
        <BootstrapWorkspace
          bootstrap={bootstrap}
          busy={bootstrapBusy}
          onResumeDiscovery={onResumeDiscovery}
          onLaunchHost={onLaunchHost}
        />
      ) : (
        <StatusWorkspace
          bootstrap={bootstrap}
          definitions={definitions}
          hostSessionStatus={hostSessionStatus}
          instances={instances}
          nowTick={nowTick}
          onAddDefinition={onAddDefinition}
          onEditDefinition={onEditDefinition}
          onViewInstanceDefinition={onViewInstanceDefinition}
        />
      )}

      {settingsOpen ? (
        <SettingsDialog
          bootstrap={bootstrap}
          busy={settingsBusy}
          fieldErrors={settingsFieldErrors}
          settings={settings}
          settingsDirty={settingsDirty}
          settingsDraft={settingsDraft}
          onChangeField={onChangeSettingsField}
          onClose={onCloseSettings}
          onSave={onSaveSettings}
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

function BootstrapWorkspace(props: {
  bootstrap: BootstrapSnapshot | null;
  busy: boolean;
  onResumeDiscovery: () => void;
  onLaunchHost: () => void;
}) {
  const { bootstrap, busy, onResumeDiscovery, onLaunchHost } = props;

  return (
    <section className="page-grid bootstrap-grid">
      <article className="panel panel-primary panel-span">
        <header className="panel-header">
          <div>
            <p className="eyebrow">Connection</p>
            <h2>连接 DevHub Host</h2>
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
          </div>
        </header>

        <div className="status-banner">
          <strong>{formatBootstrapHeadline(bootstrap?.phase)}</strong>
          <p>{formatBootstrapDescription(bootstrap)}</p>
        </div>
      </article>

      <article className="panel">
        <header className="panel-header">
          <h2>当前环境</h2>
        </header>
        <dl className="detail-list">
          <div>
            <dt>生效数据目录</dt>
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
            <dt>当前连接</dt>
            <dd>{bootstrap?.connection?.rpcEndpoint ?? "尚未发现可用 Host。"}</dd>
          </div>
        </dl>
      </article>

      <article className="panel">
        <header className="panel-header">
          <h2>下一步</h2>
        </header>
        <div className="stack-list">
          <div className="stack-item">
            <strong>继续等待自动发现</strong>
            <p>Monitor 会持续验证当前数据目录中的运行时信息，不需要手动切换页面。</p>
          </div>
          <div className="stack-item">
            <strong>无法启动时先检查设置</strong>
            <p>如果缺少 Host 路径或需要调整数据目录，请使用顶部“设置”菜单更新本机配置。</p>
          </div>
          <div className="stack-item">
            <strong>排障入口位于帮助菜单</strong>
            <p>需要查看日志时，可通过顶部“帮助”菜单直接打开 Host 或 Monitor 的日志目录。</p>
          </div>
        </div>

        <div className="problem-card">
          {bootstrap?.lastProblem?.message ?? "当前没有记录到需要处理的连接问题。"}
        </div>
      </article>
    </section>
  );
}

function StatusWorkspace(props: {
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
            <p className="eyebrow">Connection</p>
            <h2>当前连接</h2>
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
            <h2>应用定义</h2>
          </div>
          <button type="button" onClick={onAddDefinition}>
            新增定义
          </button>
        </header>

        {definitions.length === 0 ? (
          <EmptyState
            title="当前没有应用定义"
            description="连接已建立，但 Host 还没有返回任何定义。你可以先创建一个新的应用定义。"
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
            <h2>应用实例</h2>
          </div>
          <span className="subtle">列表包含离线保留实例</span>
        </header>

        {instances.length === 0 ? (
          <EmptyState
            title="当前没有应用实例"
            description="实例会在 Host 接收到注册后显示在这里，离线保留实例也会继续保留在列表中。"
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

function SettingsDialog(props: {
  bootstrap: BootstrapSnapshot | null;
  busy: boolean;
  fieldErrors: SettingsFieldErrors;
  settings: SettingsSnapshot | null;
  settingsDirty: boolean;
  settingsDraft: MonitorSettings;
  onChangeField: (field: keyof MonitorSettings, value: string) => void;
  onClose: () => void;
  onSave: () => void;
}) {
  const { bootstrap, busy, fieldErrors, settings, settingsDirty, settingsDraft, onChangeField, onClose, onSave } = props;

  return (
    <div className="modal-backdrop" role="presentation">
      <section className="modal-card settings-modal" role="dialog" aria-modal="true" aria-labelledby="settings-dialog-title">
        <header className="modal-header">
          <div>
            <p className="eyebrow">Settings</p>
            <h2 id="settings-dialog-title">Monitor 设置</h2>
            <p className="modal-subtitle">更新数据目录和 Host 启动路径。保存后 Monitor 会重新扫描当前环境。</p>
          </div>
          <div className="button-row">
            <button type="button" className="button-secondary" onClick={onClose} disabled={busy}>
              关闭
            </button>
            <button type="button" onClick={onSave} disabled={busy || !settingsDirty}>
              {busy ? "保存中..." : "保存设置"}
            </button>
          </div>
        </header>

        <div className="modal-body settings-dialog-grid">
          <article className="panel panel-primary">
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

            <p className="subtle settings-note">
              两个路径字段都只接受绝对路径。调整完成后，Monitor 会立即用新的配置重新发现 Host。
            </p>
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
        </div>
      </section>
    </div>
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
            <p>Monitor 会先同步 Host 中的最新定义，再决定是进入编辑模式还是只读视图。</p>
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
                <p className="subtle">未启用 launch 配置时，Host 不会接收 launch 字段。</p>
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
