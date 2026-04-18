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
  formatBootstrapDescription,
  formatBootstrapHeadline,
  formatDataDirSource,
  formatDefinitionCapabilities,
  formatHostLogDirectory,
  formatPhaseLabel,
  formatRelativeTime,
  formatScope,
  formatSessionLabel,
  getRuntimePort,
  getSidebarWorkspace,
  isInstanceOffline,
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
    inventoryMessage,
    nowTick,
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
  const workspaceMeta = getWorkspaceMeta(activeWorkspace, homeWorkspaceMode, definitionWorkspace);

  return (
    <main className="monitor-layout">
      <MonitorSidebar
        activeWorkspace={sidebarWorkspace}
        collapsed={sidebarCollapsed}
        onNavigate={onNavigateWorkspace}
        onToggleCollapse={() => setSidebarCollapsed((current) => !current)}
      />

      <section className="monitor-stage">
        <header className="workspace-hero">
          <div className="workspace-copy">
            <p className="eyebrow">{workspaceMeta.eyebrow}</p>
            <h1>{workspaceMeta.title}</h1>
            <p className="workspace-description">{workspaceMeta.description}</p>
          </div>

          <div className="workspace-status">
            <span className={`phase phase-${bootstrap?.phase ?? "loading"}`}>
              {formatPhaseLabel(bootstrap?.phase)}
            </span>
            <span className={`session-badge session-${hostSessionStatus}`}>
              {formatSessionLabel(hostSessionStatus)}
            </span>
          </div>
        </header>

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
              inventoryMessage={inventoryMessage}
              nowTick={nowTick}
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
              onOpenLogDirectory={onOpenLogDirectory}
            />
          ) : null}

          {activeWorkspace === "settings" ? (
            <SettingsWorkspace
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

      <div className="sidebar-brand">
        <span className="sidebar-brand-mark">DH</span>
        <div className="sidebar-brand-copy">
          <strong>DevHub Monitor</strong>
          <span>桌面工作区</span>
        </div>
      </div>

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
  inventoryMessage: string;
  nowTick: number;
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
    <section className="workspace-stack">
      <article className="panel panel-primary hero-panel">
        <p className="eyebrow">Discovery</p>
        <h2>{formatBootstrapHeadline(bootstrap?.phase)}</h2>
        <p className="panel-copy">{formatBootstrapDescription(bootstrap)}</p>

        <div className="hero-highlight-row">
          <div className="hero-highlight">
            <span className="summary-label">当前数据目录</span>
            <strong>{bootstrap?.effectiveDataDir ?? "加载中"}</strong>
            <span className="summary-meta">
              来源：{formatDataDirSource(bootstrap?.dataDirSource)}
            </span>
          </div>
          <div className="hero-highlight">
            <span className="summary-label">当前连接</span>
            <strong>{bootstrap?.connection?.rpcEndpoint ?? "尚未发现可用 Host"}</strong>
            <span className="summary-meta">
              {bootstrap?.connection?.runtimeDirectory ?? "Monitor 会持续扫描当前运行环境。"}
            </span>
          </div>
        </div>

        <div className="button-row">
          <button type="button" className="button-secondary" onClick={onResumeDiscovery} disabled={busy}>
            重新扫描
          </button>
          <button
            type="button"
            onClick={requiresSettings ? onOpenSettings : onLaunchHost}
            disabled={busy && !requiresSettings}
          >
            {requiresSettings ? "前往设置" : busy ? "正在启动..." : "启动 DevHub Host"}
          </button>
        </div>
      </article>

      <section className="page-grid">
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
              <strong>保持在主页等待发现</strong>
              <p>当 Host 变为可用时，主页会直接切换成连接与资产视图，不需要跳转到其他页面。</p>
            </div>
            <div className="stack-item">
              <strong>缺少路径时进入设置</strong>
              <p>如果当前无法启动 Host，可前往“设置”补充绝对路径并重新保存。</p>
            </div>
            <div className="stack-item">
              <strong>排障入口集中在帮助页</strong>
              <p>需要查看日志时，可切换到“帮助”直接打开 Host 或 Monitor 日志目录。</p>
            </div>
          </div>

          <div className="problem-card">
            {bootstrap?.lastProblem?.message ?? "当前没有记录到需要处理的连接问题。"}
          </div>
        </article>
      </section>
    </section>
  );
}

function HomeStatusWorkspace(props: {
  bootstrap: BootstrapSnapshot | null;
  definitions: AppDefinition[];
  hostSessionStatus: HostSessionStatus;
  instances: AppInstance[];
  inventoryMessage: string;
  nowTick: number;
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
    inventoryMessage,
    nowTick,
    settings,
    onAddDefinition,
    onEditDefinition,
    onViewInstanceDefinition,
  } = props;

  const [definitionsCollapsed, setDefinitionsCollapsed] = useState(false);
  const [instancesCollapsed, setInstancesCollapsed] = useState(false);

  return (
    <section className="workspace-stack">
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
            {bootstrap?.connection?.rpcEndpoint ?? "等待重新发现可用 Host。"}
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

      <InventorySection
        action={(
          <button type="button" className="icon-action-button" onClick={onAddDefinition}>
            <PlusIcon />
            新增定义
          </button>
        )}
        collapsed={definitionsCollapsed}
        count={definitions.length}
        onToggle={() => setDefinitionsCollapsed((current) => !current)}
        title="应用定义"
      >
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
                    <span className="chip subtle-chip">{definition.appId}</span>
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
                    <EditIcon />
                    编辑
                  </button>
                </div>
              </article>
            ))}
          </div>
        )}
      </InventorySection>

      <InventorySection
        collapsed={instancesCollapsed}
        count={instances.length}
        onToggle={() => setInstancesCollapsed((current) => !current)}
        title="应用实例"
      >
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
      </InventorySection>
    </section>
  );
}

function InventorySection(props: {
  action?: ReactNode;
  collapsed: boolean;
  count: number;
  children: ReactNode;
  onToggle: () => void;
  title: string;
}) {
  const { action, collapsed, count, children, onToggle, title } = props;

  return (
    <section className={`inventory-section ${collapsed ? "collapsed" : ""}`}>
      <div className="inventory-section-header">
        <button
          type="button"
          className="inventory-section-trigger"
          aria-expanded={!collapsed}
          onClick={onToggle}
        >
          <span className={`section-caret ${collapsed ? "collapsed" : ""}`}>
            <ChevronIcon />
          </span>
          <span>{title}</span>
          <span className="section-count">{count}</span>
        </button>

        {action ? <div className="inventory-section-action">{action}</div> : null}
      </div>

      {!collapsed ? <div className="inventory-section-body">{children}</div> : null}
    </section>
  );
}

function HelpWorkspace(props: {
  bootstrap: BootstrapSnapshot | null;
  openingLogKind: LogKind | null;
  settings: SettingsSnapshot | null;
  onOpenLogDirectory: (kind: LogKind) => void;
}) {
  const { bootstrap, openingLogKind, settings, onOpenLogDirectory } = props;
  const hostLogDirectory = formatHostLogDirectory(bootstrap?.effectiveDataDir ?? settings?.effectiveDataDir);

  return (
    <section className="workspace-stack">
      <article className="panel panel-primary">
        <p className="eyebrow">Support</p>
        <h2>日志与支持</h2>
        <p className="panel-copy">
          遇到启动、连接或运行异常时，可直接打开日志目录进行排查；Monitor 不会在主界面内嵌日志阅读器。
        </p>
      </article>

      <section className="page-grid">
        <article className="panel support-card">
          <header className="panel-header">
            <h2>Host 日志</h2>
          </header>
          <p className="support-path">{hostLogDirectory}</p>
          <button
            type="button"
            className="button-secondary"
            onClick={() => onOpenLogDirectory("host")}
            disabled={openingLogKind !== null}
          >
            <OpenExternalIcon />
            {openingLogKind === "host" ? "正在打开 Host 日志..." : "打开 Host 日志"}
          </button>
        </article>

        <article className="panel support-card">
          <header className="panel-header">
            <h2>Monitor 日志</h2>
          </header>
          <p className="support-path">{settings?.monitorLogDirectory ?? "加载中"}</p>
          <button
            type="button"
            className="button-secondary"
            onClick={() => onOpenLogDirectory("monitor")}
            disabled={openingLogKind !== null}
          >
            <OpenExternalIcon />
            {openingLogKind === "monitor" ? "正在打开 Monitor 日志..." : "打开 Monitor 日志"}
          </button>
        </article>
      </section>

      <article className="panel">
        <header className="panel-header">
          <h2>运行信息</h2>
        </header>
        <dl className="detail-list">
          <div>
            <dt>生效数据目录</dt>
            <dd>{bootstrap?.effectiveDataDir ?? settings?.effectiveDataDir ?? "加载中"}</dd>
          </div>
          <div>
            <dt>设置文件</dt>
            <dd>{settings?.settingsFilePath ?? "加载中"}</dd>
          </div>
          <div>
            <dt>当前 RPC</dt>
            <dd>{bootstrap?.connection?.rpcEndpoint ?? "尚未连接"}</dd>
          </div>
          <div>
            <dt>Host 版本</dt>
            <dd>{bootstrap?.connection?.runtime.hubVersion ?? "未知"}</dd>
          </div>
        </dl>
      </article>
    </section>
  );
}

function SettingsWorkspace(props: {
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
          两个路径字段都只接受绝对路径。保存成功后，Monitor 会回到主页并用新的配置重新发现 Host。
        </p>

        <div className="workspace-footer sticky-footer">
          <span className="subtle">
            {settingsDirty ? "当前草稿尚未保存。" : "当前设置已与磁盘内容同步。"}
          </span>
          <div className="button-row">
            <button type="button" onClick={onSave} disabled={busy || !settingsDirty}>
              {busy ? "保存中..." : "保存设置"}
            </button>
          </div>
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
      <article className="panel">
        <EmptyState
          title="正在准备 App Definition 工作区"
          description="Monitor 正在同步当前页面状态，请稍候。"
        />
      </article>
    );
  }

  const submitLabel = workspace.mode === "create" ? "创建定义" : "保存修改";
  const canDelete = workspace.mode === "edit" && !workspace.readOnly && !workspace.missing;
  const disableInputs = workspace.readOnly || workspace.loading || workspace.saving;
  const modeLabel = workspace.readOnly
    ? "只读"
    : workspace.mode === "create"
      ? "新建"
      : "编辑";

  return (
    <section className="workspace-stack definition-stack">
      <article className="panel command-panel">
        <div className="command-bar">
          <div className="chip-row">
            <span className="chip subtle-chip">{modeLabel}</span>
            {workspace.form.appId ? (
              <span className="chip subtle-chip">{workspace.form.appId}</span>
            ) : null}
          </div>

          <button type="button" className="button-secondary" onClick={onClose} disabled={workspace.saving}>
            <ArrowLeftIcon />
            返回主页
          </button>
        </div>
      </article>

      {workspace.submitError ? <div className="inline-error">{workspace.submitError}</div> : null}

      {workspace.loading ? (
        <article className="panel">
          <EmptyState
            title="正在读取定义"
            description="Monitor 会先同步 Host 中的最新定义，再决定是进入编辑模式还是只读视图。"
          />
        </article>
      ) : workspace.missing ? (
        <article className="panel">
          <EmptyState
            title="定义不可用"
            description={workspace.emptyStateMessage ?? "请求的 App Definition 当前不可用。"}
          />
        </article>
      ) : (
        <article className="panel">
          {workspace.readOnly ? (
            <div className="status-banner compact-banner">
              <strong>当前为只读视图</strong>
              <p>只读模式不会提供保存或删除操作，但你仍然可以查看当前定义的完整配置。</p>
            </div>
          ) : null}

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

          <section className="section-block">
            <header className="section-header">
              <h3>能力</h3>
            </header>
            <div className="toggle-grid">
              <label className="toggle-item">
                <input
                  type="checkbox"
                  checked={workspace.form.enableRpc}
                  disabled={disableInputs}
                  onChange={(event) => onChangeField("enableRpc", event.target.checked)}
                />
                <span>RPC</span>
              </label>
              <label className="toggle-item">
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

          <section className="section-block">
            <header className="section-header">
              <h3>启动配置</h3>
              <label className="toggle-item">
                <input
                  type="checkbox"
                  checked={workspace.form.enableLaunch}
                  disabled={disableInputs}
                  onChange={(event) => onChangeField("enableLaunch", event.target.checked)}
                />
                <span>启用 launch</span>
              </label>
            </header>
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
            ) : (
              <p className="subtle">未启用 launch 配置时，Host 不会接收 launch 字段。</p>
            )}
            <FieldIssues issues={workspace.fieldErrors["definition.launch"]} />
          </section>
        </article>
      )}

      <footer className="workspace-footer sticky-footer">
        {canDelete ? (
          <button type="button" className="button-danger" onClick={onDelete} disabled={workspace.saving}>
            删除定义
          </button>
        ) : (
          <span className="subtle">
            {workspace.readOnly ? "只读模式不允许保存或删除。" : "删除操作仅在编辑现有定义时可用。"}
          </span>
        )}
        <div className="button-row">
          <button type="button" className="button-secondary" onClick={onClose} disabled={workspace.saving}>
            返回主页
          </button>
          {!workspace.readOnly && !workspace.missing ? (
            <button type="button" onClick={onSubmit} disabled={workspace.saving}>
              {workspace.saving ? "处理中..." : submitLabel}
            </button>
          ) : null}
        </div>
      </footer>
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

function getWorkspaceMeta(
  activeWorkspace: MonitorWorkspace,
  homeWorkspaceMode: HomeWorkspaceMode,
  definitionWorkspace: DefinitionWorkspaceState | null,
) {
  switch (activeWorkspace) {
    case "help":
      return {
        eyebrow: "DevHub Support",
        title: "帮助",
        description: "集中查看日志入口与当前运行信息，排查连接、启动和资产同步问题。",
      };
    case "settings":
      return {
        eyebrow: "Monitor Settings",
        title: "设置",
        description: "更新数据目录和 Host 启动路径。保存成功后，主页会继续当前发现或连接流程。",
      };
    case "definition":
      return {
        eyebrow: "App Definition",
        title: definitionWorkspace?.title ?? "App Definition",
        description: definitionWorkspace?.subtitle ?? "在独立工作区中查看或维护 App Definition。",
      };
    case "home":
    default:
      return {
        eyebrow: "DevHub Monitor",
        title: "主页",
        description: homeWorkspaceMode === "status"
          ? "主页会持续展示当前 Host 连接、应用定义和应用实例，不需要跳转到独立状态页。"
          : "主页会持续扫描并尝试连接 DevHub Host，待连接可用后会在原位切换成运行视图。",
      };
  }
}

function MenuIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <path d="M4 7h16M4 12h16M4 17h16" />
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

function ChevronIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <path d="m8 10 4 4 4-4" />
    </svg>
  );
}

function ArrowLeftIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <path d="M19 12H5" />
      <path d="m11 18-6-6 6-6" />
    </svg>
  );
}
