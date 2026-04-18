import packageManifest from "../../package.json";
import type { AppDefinition, AppInstance } from "@devhub/sdk";
import { type ReactNode, useState } from "react";
import type { DefinitionFormState } from "../lib/definition-form";
import type {
  BootstrapSnapshot,
  LogKind,
  MonitorSettings,
  SettingsSnapshot,
} from "../lib/models";
import {
  type DefinitionWorkspaceState,
  type HomeWorkspaceMode,
  type HostSessionStatus,
  type MonitorWorkspace,
  type SettingsFieldErrors,
  type SidebarWorkspace,
  formatHostLogDirectory,
  getSidebarWorkspace,
} from "../lib/monitor-ui";

interface AppShellProps {
  activeWorkspace: MonitorWorkspace;
  homeWorkspaceMode: HomeWorkspaceMode;
  openingLogKind: LogKind | null;
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
  definitionWorkspace: DefinitionWorkspaceState | null;
  onNavigateWorkspace: (workspace: SidebarWorkspace) => void;
  onOpenLogDirectory: (kind: LogKind) => void;
  onResumeDiscovery: () => void;
  onLaunchHost: () => void;
  onChangeSettingsField: (field: keyof MonitorSettings, value: string) => void;
  onSaveSettings: () => void;
  onAddDefinition: () => void;
  onEditDefinition: (appId: string) => void;
  onViewInstanceDefinition: (instance: AppInstance) => void;
  onChangeDefinitionField: (field: keyof DefinitionFormState, value: string | boolean) => void;
  onCloseDefinitionWorkspace: () => void;
  onDeleteDefinition: () => void;
  onSubmitDefinition: () => void;
}

const MONITOR_VERSION_TEXT = `Monitor v${packageManifest.version}`;

export function AppShell(props: AppShellProps) {
  const {
    activeWorkspace,
    homeWorkspaceMode,
    openingLogKind,
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
    definitionWorkspace,
    onNavigateWorkspace,
    onOpenLogDirectory,
    onResumeDiscovery,
    onLaunchHost,
    onChangeSettingsField,
    onSaveSettings,
    onAddDefinition,
    onEditDefinition,
    onViewInstanceDefinition,
    onChangeDefinitionField,
    onCloseDefinitionWorkspace,
    onDeleteDefinition,
    onSubmitDefinition,
  } = props;

  const [sidebarCollapsed, setSidebarCollapsed] = useState(false);
  const sidebarWorkspace = getSidebarWorkspace(activeWorkspace);

  return (
    <main className="monitor-layout">
      <MonitorSidebar
        activeWorkspace={sidebarWorkspace}
        collapsed={sidebarCollapsed}
        onNavigate={onNavigateWorkspace}
        onToggleCollapse={() => setSidebarCollapsed((current) => !current)}
      />

      <section className="monitor-stage">
        {activeError ? (
          <div className="error-banner" role="alert">
            {activeError}
          </div>
        ) : null}

        <div className="workspace-scroll">
          {activeWorkspace === "home" ? (
            <HomeWorkspace
              bootstrap={bootstrap}
              busy={bootstrapBusy}
              definitions={definitions}
              homeWorkspaceMode={homeWorkspaceMode}
              hostSessionStatus={hostSessionStatus}
              instances={instances}
              settings={settings}
              onAddDefinition={onAddDefinition}
              onEditDefinition={onEditDefinition}
              onLaunchHost={onLaunchHost}
              onOpenSettings={() => onNavigateWorkspace("settings")}
              onResumeDiscovery={onResumeDiscovery}
              onViewInstanceDefinition={onViewInstanceDefinition}
            />
          ) : null}

          {activeWorkspace === "help" ? (
            <HelpWorkspace
              bootstrap={bootstrap}
              openingLogKind={openingLogKind}
              settings={settings}
              versionText={MONITOR_VERSION_TEXT}
              onOpenLogDirectory={onOpenLogDirectory}
            />
          ) : null}

          {activeWorkspace === "settings" ? (
            <SettingsWorkspace
              busy={settingsBusy}
              fieldErrors={settingsFieldErrors}
              settingsDirty={settingsDirty}
              settingsDraft={settingsDraft}
              onChangeField={onChangeSettingsField}
              onSave={onSaveSettings}
            />
          ) : null}

          {activeWorkspace === "definition" ? (
            <DefinitionWorkspacePage
              workspace={definitionWorkspace}
              onChangeField={onChangeDefinitionField}
              onClose={onCloseDefinitionWorkspace}
              onDelete={onDeleteDefinition}
              onSubmit={onSubmitDefinition}
            />
          ) : null}
        </div>
      </section>
    </main>
  );
}

function MonitorSidebar(props: {
  activeWorkspace: SidebarWorkspace;
  collapsed: boolean;
  onNavigate: (workspace: SidebarWorkspace) => void;
  onToggleCollapse: () => void;
}) {
  const { activeWorkspace, collapsed, onNavigate, onToggleCollapse } = props;

  return (
    <aside className={`monitor-sidebar ${collapsed ? "collapsed" : ""}`}>
      <button
        type="button"
        className="sidebar-toggle"
        aria-label={collapsed ? "展开侧边栏" : "折叠侧边栏"}
        onClick={onToggleCollapse}
      >
        <MenuIcon />
      </button>

      <nav className="sidebar-nav" aria-label="Monitor 工作区">
        <SidebarButton
          active={activeWorkspace === "home"}
          collapsed={collapsed}
          icon={<HomeIcon />}
          label="主页"
          onClick={() => onNavigate("home")}
        />
        <SidebarButton
          active={activeWorkspace === "help"}
          collapsed={collapsed}
          icon={<HelpIcon />}
          label="帮助"
          onClick={() => onNavigate("help")}
        />

        <div className="sidebar-nav-bottom">
          <SidebarButton
            active={activeWorkspace === "settings"}
            collapsed={collapsed}
            icon={<SettingsIcon />}
            label="设置"
            onClick={() => onNavigate("settings")}
          />
        </div>
      </nav>
    </aside>
  );
}

function SidebarButton(props: {
  active: boolean;
  collapsed: boolean;
  icon: ReactNode;
  label: string;
  onClick: () => void;
}) {
  const { active, collapsed, icon, label, onClick } = props;

  return (
    <button
      type="button"
      className={`sidebar-button ${active ? "active" : ""}`}
      aria-current={active ? "page" : undefined}
      aria-label={label}
      onClick={onClick}
    >
      <span className="sidebar-icon">{icon}</span>
      {!collapsed ? <span className="sidebar-label">{label}</span> : null}
    </button>
  );
}

function HomeWorkspace(props: {
  bootstrap: BootstrapSnapshot | null;
  busy: boolean;
  definitions: AppDefinition[];
  homeWorkspaceMode: HomeWorkspaceMode;
  hostSessionStatus: HostSessionStatus;
  instances: AppInstance[];
  settings: SettingsSnapshot | null;
  onAddDefinition: () => void;
  onEditDefinition: (appId: string) => void;
  onLaunchHost: () => void;
  onOpenSettings: () => void;
  onResumeDiscovery: () => void;
  onViewInstanceDefinition: (instance: AppInstance) => void;
}) {
  const { homeWorkspaceMode, ...rest } = props;

  return homeWorkspaceMode === "status"
    ? <HomeStatusWorkspace {...rest} />
    : <HomeDiscoveryWorkspace {...rest} />;
}

function HomeDiscoveryWorkspace(props: {
  bootstrap: BootstrapSnapshot | null;
  busy: boolean;
  onLaunchHost: () => void;
  onOpenSettings: () => void;
  onResumeDiscovery: () => void;
}) {
  const { bootstrap, busy, onLaunchHost, onOpenSettings, onResumeDiscovery } = props;
  const requiresSettings = bootstrap?.phase === "settings_required" || !bootstrap?.hasConfiguredHostExecutable;

  return (
    <section className="workspace-view">
      <h1 className="sr-only">主页</h1>

      <div className="discovery-view">
        <div className="loader" aria-hidden="true" />
        <p className="status-title">{getDiscoveryTitle(bootstrap?.phase)}</p>
        <p className="status-path">目标位置：{bootstrap?.effectiveDataDir ?? "加载中"}</p>

        <div className="status-actions">
          <button type="button" className="button-secondary" onClick={onResumeDiscovery} disabled={busy}>
            重新扫描
          </button>
          <button
            type="button"
            onClick={requiresSettings ? onOpenSettings : onLaunchHost}
            disabled={busy && !requiresSettings}
          >
            {requiresSettings ? "前往设置" : busy ? "正在启动..." : "启动 Host"}
          </button>
        </div>
      </div>
    </section>
  );
}

function HomeStatusWorkspace(props: {
  bootstrap: BootstrapSnapshot | null;
  definitions: AppDefinition[];
  hostSessionStatus: HostSessionStatus;
  instances: AppInstance[];
  settings: SettingsSnapshot | null;
  onAddDefinition: () => void;
  onEditDefinition: (appId: string) => void;
  onViewInstanceDefinition: (instance: AppInstance) => void;
}) {
  const {
    bootstrap,
    definitions,
    hostSessionStatus,
    instances,
    settings,
    onAddDefinition,
    onEditDefinition,
    onViewInstanceDefinition,
  } = props;

  const [instancesCollapsed, setInstancesCollapsed] = useState(false);
  const [definitionsCollapsed, setDefinitionsCollapsed] = useState(false);

  return (
    <section className="workspace-view">
      <h1 className="sr-only">主页</h1>

      <header className="host-header">
        <p className={`host-endpoint session-${hostSessionStatus}`}>
          {bootstrap?.connection?.rpcEndpoint ?? "未连接"}
        </p>
        <p className="host-directory">
          数据目录：{bootstrap?.effectiveDataDir ?? settings?.effectiveDataDir ?? "加载中"}
        </p>
      </header>

      <InventorySection
        collapsed={instancesCollapsed}
        count={instances.length}
        title="App 实例"
        onToggle={() => setInstancesCollapsed((current) => !current)}
      >
        {instances.length === 0 ? (
          <EmptyState title="当前没有 App 实例" />
        ) : (
          <div className="list">
            {instances.map((instance) => (
              <article key={instance.instanceId} className="list-item">
                <span className="item-name">{instance.instanceId}</span>
                <div className="item-actions">
                  <button
                    type="button"
                    className="icon-button"
                    aria-label="查看定义"
                    title="查看定义"
                    onClick={() => onViewInstanceDefinition(instance)}
                  >
                    <ViewIcon />
                  </button>
                </div>
              </article>
            ))}
          </div>
        )}
      </InventorySection>

      <InventorySection
        action={(
          <button
            type="button"
            className="icon-button"
            aria-label="新增定义"
            title="新增定义"
            onClick={onAddDefinition}
          >
            <PlusIcon />
          </button>
        )}
        collapsed={definitionsCollapsed}
        count={definitions.length}
        title="App 定义"
        onToggle={() => setDefinitionsCollapsed((current) => !current)}
      >
        {definitions.length === 0 ? (
          <EmptyState title="当前没有 App 定义" />
        ) : (
          <div className="list">
            {definitions.map((definition) => (
              <article key={definition.appId} className="list-item">
                <span className="item-name">{definition.displayName || definition.appId}</span>
                <div className="item-actions">
                  <button
                    type="button"
                    className="icon-button"
                    aria-label="编辑"
                    title="编辑"
                    onClick={() => onEditDefinition(definition.appId)}
                  >
                    <EditIcon />
                  </button>
                </div>
              </article>
            ))}
          </div>
        )}
      </InventorySection>
    </section>
  );
}

function InventorySection(props: {
  action?: ReactNode;
  collapsed: boolean;
  count: number;
  children: ReactNode;
  title: string;
  onToggle: () => void;
}) {
  const { action, collapsed, count, children, title, onToggle } = props;

  return (
    <section className={`section ${collapsed ? "collapsed" : ""}`}>
      <div className="section-header">
        <button
          type="button"
          className="section-trigger"
          aria-expanded={!collapsed}
          onClick={onToggle}
        >
          <span className={`section-caret ${collapsed ? "collapsed" : ""}`}>
            <ChevronIcon />
          </span>
          <span>{title}</span>
          <span className="section-count">({count})</span>
        </button>

        {action ? <div className="section-action">{action}</div> : null}
      </div>

      {!collapsed ? <div className="section-body">{children}</div> : null}
    </section>
  );
}

function HelpWorkspace(props: {
  bootstrap: BootstrapSnapshot | null;
  openingLogKind: LogKind | null;
  settings: SettingsSnapshot | null;
  versionText: string;
  onOpenLogDirectory: (kind: LogKind) => void;
}) {
  const { bootstrap, openingLogKind, settings, versionText, onOpenLogDirectory } = props;
  const hostLogDirectory = formatHostLogDirectory(bootstrap?.effectiveDataDir ?? settings?.effectiveDataDir);

  return (
    <section className="workspace-view">
      <h1 className="view-title">帮助</h1>

      <div className="support-group">
        <div className="form-group">
          <label className="form-label">Host 日志</label>
          <div className="form-value-row">
            <div className="path-box">{hostLogDirectory}</div>
            <button
              type="button"
              className="icon-button"
              aria-label="打开 Host 日志"
              title="打开 Host 日志"
              onClick={() => onOpenLogDirectory("host")}
              disabled={openingLogKind !== null}
            >
              <OpenExternalIcon />
            </button>
          </div>
        </div>

        <div className="form-group">
          <label className="form-label">Monitor 日志</label>
          <div className="form-value-row">
            <div className="path-box">{settings?.monitorLogDirectory ?? "加载中"}</div>
            <button
              type="button"
              className="icon-button"
              aria-label="打开 Monitor 日志"
              title="打开 Monitor 日志"
              onClick={() => onOpenLogDirectory("monitor")}
              disabled={openingLogKind !== null}
            >
              <OpenExternalIcon />
            </button>
          </div>
        </div>

        <div className="form-group">
          <label className="form-label">版本</label>
          <div className="version-text">{versionText}</div>
        </div>
      </div>
    </section>
  );
}

function SettingsWorkspace(props: {
  busy: boolean;
  fieldErrors: SettingsFieldErrors;
  settingsDirty: boolean;
  settingsDraft: MonitorSettings;
  onChangeField: (field: keyof MonitorSettings, value: string) => void;
  onSave: () => void;
}) {
  const { busy, fieldErrors, settingsDirty, settingsDraft, onChangeField, onSave } = props;

  return (
    <section className="workspace-view">
      <h1 className="view-title">设置</h1>

      <div className="settings-form">
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

        <label className="field">
          <span>Host 数据目录 (DEVHUB_DATA_DIR)</span>
          <input
            type="text"
            value={settingsDraft.dataDirOverride ?? ""}
            placeholder="留空表示使用环境变量或平台默认目录"
            onChange={(event) => onChangeField("dataDirOverride", event.target.value)}
          />
          <FieldError message={fieldErrors.dataDirOverride} />
        </label>

        <div className="definition-actions">
          <div className="action-spacer" />
          <button type="button" onClick={onSave} disabled={busy || !settingsDirty}>
            {busy ? "保存中..." : "保存设置"}
          </button>
        </div>
      </div>
    </section>
  );
}

function DefinitionWorkspacePage(props: {
  workspace: DefinitionWorkspaceState | null;
  onChangeField: (field: keyof DefinitionFormState, value: string | boolean) => void;
  onClose: () => void;
  onDelete: () => void;
  onSubmit: () => void;
}) {
  const { workspace, onChangeField, onClose, onDelete, onSubmit } = props;

  if (!workspace) {
    return (
      <section className="workspace-view">
        <h1 className="view-title">App Definition</h1>
        <EmptyState title="正在准备 App Definition 工作区" />
      </section>
    );
  }

  const submitLabel = workspace.mode === "create" ? "创建定义" : "保存修改";
  const canDelete = workspace.mode === "edit" && !workspace.readOnly && !workspace.missing;
  const disableInputs = workspace.readOnly || workspace.loading || workspace.saving;

  return (
    <section className="workspace-view definition-page">
      <h1 className="view-title">{workspace.title}</h1>

      {workspace.submitError ? (
        <div className="inline-error" role="alert">
          {workspace.submitError}
        </div>
      ) : null}

      {workspace.loading ? (
        <EmptyState title="正在读取定义" />
      ) : workspace.missing ? (
        <EmptyState title={workspace.emptyStateMessage ?? "定义不可用"} />
      ) : (
        <>
          <div className="definition-form">
            <div className="form-grid">
              <label className="field">
                <span>App ID</span>
                <input
                  type="text"
                  value={workspace.form.appId}
                  disabled={disableInputs || workspace.mode !== "create"}
                  onChange={(event) => onChangeField("appId", event.target.value)}
                />
                <FieldIssues issues={workspace.fieldErrors["definition.appId"]} />
              </label>

              <label className="field">
                <span>显示名称</span>
                <input
                  type="text"
                  value={workspace.form.displayName}
                  disabled={disableInputs}
                  onChange={(event) => onChangeField("displayName", event.target.value)}
                />
                <FieldIssues issues={workspace.fieldErrors["definition.displayName"]} />
              </label>

              <label className="field field-full">
                <span>描述</span>
                <textarea
                  rows={3}
                  value={workspace.form.description}
                  disabled={disableInputs}
                  onChange={(event) => onChangeField("description", event.target.value)}
                />
                <FieldIssues issues={workspace.fieldErrors["definition.description"]} />
              </label>
            </div>

            <section className="form-section">
              <h2 className="section-title">能力</h2>
              <div className="check-grid">
                <label className="check-field">
                  <input
                    type="checkbox"
                    checked={workspace.form.enableRpc}
                    disabled={disableInputs}
                    onChange={(event) => onChangeField("enableRpc", event.target.checked)}
                  />
                  <span>RPC</span>
                </label>
                <label className="check-field">
                  <input
                    type="checkbox"
                    checked={workspace.form.enableEvents}
                    disabled={disableInputs}
                    onChange={(event) => onChangeField("enableEvents", event.target.checked)}
                  />
                  <span>Events</span>
                </label>
              </div>
              <FieldIssues issues={workspace.fieldErrors["definition.capabilities"]} />
              <FieldIssues issues={workspace.fieldErrors["definition.capabilities.rpc"]} />
              <FieldIssues issues={workspace.fieldErrors["definition.capabilities.events"]} />
            </section>

            <section className="form-section">
              <div className="form-section-header">
                <h2 className="section-title">启动配置</h2>
                <label className="check-field">
                  <input
                    type="checkbox"
                    checked={workspace.form.enableLaunch}
                    disabled={disableInputs}
                    onChange={(event) => onChangeField("enableLaunch", event.target.checked)}
                  />
                  <span>启用 launch</span>
                </label>
              </div>

              {workspace.form.enableLaunch ? (
                <div className="form-grid">
                  <label className="field">
                    <span>exePath</span>
                    <input
                      type="text"
                      value={workspace.form.launchExePath}
                      disabled={disableInputs}
                      onChange={(event) => onChangeField("launchExePath", event.target.value)}
                    />
                    <FieldIssues issues={workspace.fieldErrors["definition.launch.exePath"]} />
                  </label>

                  <label className="field">
                    <span>argsTemplate</span>
                    <input
                      type="text"
                      value={workspace.form.launchArgsTemplate}
                      disabled={disableInputs}
                      onChange={(event) => onChangeField("launchArgsTemplate", event.target.value)}
                    />
                    <FieldIssues issues={workspace.fieldErrors["definition.launch.argsTemplate"]} />
                  </label>

                  <label className="field">
                    <span>workingDirectory</span>
                    <input
                      type="text"
                      value={workspace.form.launchWorkingDirectory}
                      disabled={disableInputs}
                      onChange={(event) => onChangeField("launchWorkingDirectory", event.target.value)}
                    />
                    <FieldIssues issues={workspace.fieldErrors["definition.launch.workingDirectory"]} />
                  </label>

                  <label className="field">
                    <span>dedupeKeyTemplate</span>
                    <input
                      type="text"
                      value={workspace.form.launchDedupeKeyTemplate}
                      disabled={disableInputs}
                      onChange={(event) => onChangeField("launchDedupeKeyTemplate", event.target.value)}
                    />
                    <FieldIssues issues={workspace.fieldErrors["definition.launch.dedupeKeyTemplate"]} />
                  </label>
                </div>
              ) : null}

              <FieldIssues issues={workspace.fieldErrors["definition.launch"]} />
            </section>
          </div>
        </>
      )}

      <div className="definition-actions">
        {canDelete ? (
          <button type="button" className="button-danger" onClick={onDelete} disabled={workspace.saving}>
            删除定义
          </button>
        ) : workspace.readOnly ? (
          <span className="definition-note">只读模式不允许保存或删除。</span>
        ) : (
          <div className="action-spacer" />
        )}

        <div className="button-row">
          <button type="button" className="button-secondary" onClick={onClose} disabled={workspace.saving}>
            返回主页
          </button>
          {!workspace.readOnly && !workspace.missing && !workspace.loading ? (
            <button type="button" onClick={onSubmit} disabled={workspace.saving}>
              {workspace.saving ? "处理中..." : submitLabel}
            </button>
          ) : null}
        </div>
      </div>
    </section>
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
}) {
  return <div className="empty-state">{props.title}</div>;
}

function getDiscoveryTitle(phase?: BootstrapSnapshot["phase"]): string {
  switch (phase) {
    case "settings_required":
      return "需要先补充 Host 设置";
    case "launch_available":
      return "尚未连接到 DevHub Host";
    case "host_available":
      return "已连接到 DevHub Host";
    case "scanning":
    default:
      return "正在寻找 DevHub Host...";
  }
}

function MenuIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <path d="M3 6h18M3 12h18M3 18h18" />
    </svg>
  );
}

function HomeIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <path d="M3 10.5 12 3l9 7.5V20a1 1 0 0 1-1 1h-5v-7H9v7H4a1 1 0 0 1-1-1z" />
    </svg>
  );
}

function HelpIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <circle cx="12" cy="12" r="9" />
      <path d="M9.1 9a3 3 0 1 1 5.8 1c0 2-3 2.3-3 4" />
      <circle cx="12" cy="17.3" r="0.8" fill="currentColor" stroke="none" />
    </svg>
  );
}

function SettingsIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <path d="m12 3 1.7 2.7 3.1.7-.7 3.1 2.1 2.2-2.1 2.2.7 3.1-3.1.7L12 21l-1.7-2.7-3.1-.7.7-3.1-2.1-2.2 2.1-2.2-.7-3.1 3.1-.7z" />
      <circle cx="12" cy="12" r="3" />
    </svg>
  );
}

function OpenExternalIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <path d="M14 5h5v5" />
      <path d="M19 5 10 14" />
      <path d="M19 13v5a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1h5" />
    </svg>
  );
}

function PlusIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <path d="M12 5v14M5 12h14" />
    </svg>
  );
}

function EditIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <path d="m4 20 4.5-1 9.2-9.2a1.7 1.7 0 0 0 0-2.4l-1.1-1.1a1.7 1.7 0 0 0-2.4 0L5 15.5z" />
      <path d="M13 6.5 17.5 11" />
    </svg>
  );
}

function ViewIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <path d="M2 12s3.8-7 10-7 10 7 10 7-3.8 7-10 7-10-7-10-7Z" />
      <circle cx="12" cy="12" r="3" />
    </svg>
  );
}

function ChevronIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <path d="m8 10 4 4 4-4" />
    </svg>
  );
}
