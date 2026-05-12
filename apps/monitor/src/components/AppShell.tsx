import type {
  AppDefinition,
  AppDefinitionIdentity,
  AppInstance,
  VersionCompatibilityResult,
} from "@devhub/sdk";
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
  type RpcTestWorkspaceViewModel,
  type SettingsFieldErrors,
  type SidebarWorkspace,
  createDefinitionIdentity,
  definitionIdentityKey,
  formatDefinitionScopeLabel,
  formatHostLogDirectory,
  getSidebarWorkspace,
} from "../lib/monitor-ui";
import {
  formatVersionCompatibilityHostVersion,
  formatVersionCompatibilityStatusLabel,
  formatVersionGuidanceMessage,
  getVersionGuidanceTitle,
} from "../lib/version-guidance";
import { MONITOR_VERSION_METADATA } from "../lib/version-metadata";

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
  versionCompatibility: VersionCompatibilityResult | null;
  definitionWorkspace: DefinitionWorkspaceState | null;
  rpcTestWorkspace: RpcTestWorkspaceViewModel;
  onNavigateWorkspace: (workspace: SidebarWorkspace) => void;
  onOpenLogDirectory: (kind: LogKind) => void;
  onLaunchHost: () => void;
  onResumeDiscovery: () => void;
  onChangeSettingsField: (
    field: keyof MonitorSettings,
    value: MonitorSettings[keyof MonitorSettings],
  ) => void;
  onSelectHostExecutablePath: () => void;
  onSelectDataDirectory: () => void;
  onSaveSettings: () => void;
  onAddDefinition: () => void;
  onEditDefinition: (identity: AppDefinitionIdentity) => void;
  onViewInstanceDefinition: (instance: AppInstance) => void;
  onChangeRpcTestDraft: (value: string) => void;
  onValidateRpcTestRequest: () => void;
  onSendRpcTestRequest: () => void;
  onCancelRpcTestRequest: () => void;
  onChangeDefinitionField: (field: keyof DefinitionFormState, value: string | boolean) => void;
  onCloseDefinitionWorkspace: () => void;
  onDeleteDefinition: () => void;
  onSubmitDefinition: () => void;
}

const MONITOR_VERSION_TEXT = MONITOR_VERSION_METADATA.monitorVersion;
const SDK_VERSION_TEXT = MONITOR_VERSION_METADATA.sdkVersion;
const INVENTORY_DESCRIPTION_FALLBACK = "未提供 App 描述";

interface InventoryItemViewModel {
  key: string;
  appId: string;
  scopeLabel: string;
  title: string;
  description: string;
  actionAccessibleName: string;
  actionTitle: string;
  actionIcon: ReactNode;
  onAction: () => void;
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
    versionCompatibility,
    definitionWorkspace,
    rpcTestWorkspace,
    onNavigateWorkspace,
    onOpenLogDirectory,
    onLaunchHost,
    onResumeDiscovery,
    onChangeSettingsField,
    onSelectHostExecutablePath,
    onSelectDataDirectory,
    onSaveSettings,
    onAddDefinition,
    onEditDefinition,
    onViewInstanceDefinition,
    onChangeRpcTestDraft,
    onValidateRpcTestRequest,
    onSendRpcTestRequest,
    onCancelRpcTestRequest,
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

        {settings?.loadWarning ? (
          <div className="warning-banner" role="status">
            <p>{settings.loadWarning.message}</p>
            {settings.loadWarning.backupFilePath ? (
              <p>备份文件：{settings.loadWarning.backupFilePath}</p>
            ) : null}
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
              versionCompatibility={versionCompatibility}
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
              hostSessionStatus={hostSessionStatus}
              openingLogKind={openingLogKind}
              settings={settings}
              monitorVersionText={MONITOR_VERSION_TEXT}
              sdkVersionText={SDK_VERSION_TEXT}
              versionCompatibility={versionCompatibility}
              onOpenLogDirectory={onOpenLogDirectory}
            />
          ) : null}

          {activeWorkspace === "test" ? (
            <TestWorkspace
              workspace={rpcTestWorkspace}
              onCancel={onCancelRpcTestRequest}
              onChangeDraft={onChangeRpcTestDraft}
              onSend={onSendRpcTestRequest}
              onValidate={onValidateRpcTestRequest}
            />
          ) : null}

          {activeWorkspace === "settings" ? (
            <SettingsWorkspace
              busy={settingsBusy}
              fieldErrors={settingsFieldErrors}
              settingsDirty={settingsDirty}
              settingsDraft={settingsDraft}
              showHideHostCommandLineWindowOption={settings?.platform === "windows"}
              onChangeField={onChangeSettingsField}
              onSelectHostExecutablePath={onSelectHostExecutablePath}
              onSelectDataDirectory={onSelectDataDirectory}
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

function WorkspaceContainer(props: {
  children: ReactNode;
  className?: string;
}) {
  const className = props.className ? `workspace-view ${props.className}` : "workspace-view";

  return (
    <section className={className}>
      <div className="workspace-container">{props.children}</div>
    </section>
  );
}

function PageCard(props: {
  children: ReactNode;
  className?: string;
}) {
  const className = props.className ? `page-card ${props.className}` : "page-card";
  return <div className={className}>{props.children}</div>;
}

function InfoCard(props: {
  children: ReactNode;
  className?: string;
}) {
  const className = props.className ? `info-card ${props.className}` : "info-card";
  return <div className={className}>{props.children}</div>;
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
          active={activeWorkspace === "test"}
          collapsed={collapsed}
          icon={<TestIcon />}
          label="测试"
          onClick={() => onNavigate("test")}
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
      className={`sidebar-button${collapsed ? " collapsed" : ""}${active ? " active" : ""}`}
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
  versionCompatibility: VersionCompatibilityResult | null;
  onAddDefinition: () => void;
  onEditDefinition: (identity: AppDefinitionIdentity) => void;
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
  const showLaunchAction = bootstrap !== null
    && bootstrap.phase !== "host_incompatible"
    && (requiresSettings || bootstrap.phase === "launch_available");
  const showRecoveryActions = bootstrap?.phase === "host_incompatible";

  return (
    <WorkspaceContainer className="home-workspace">
      <h1 className="sr-only">主页</h1>

      <PageCard className="discovery-card">
        <div className="discovery-view">
          <div className="loader" aria-hidden="true" />
          <p className="status-title">{getDiscoveryTitle(bootstrap)}</p>
          <p className="status-path">目标位置：{bootstrap?.effectiveDataDir ?? "加载中"}</p>
          {bootstrap?.lastProblem?.message ? (
            <p className="status-detail">{bootstrap.lastProblem.message}</p>
          ) : null}

          {showLaunchAction || showRecoveryActions ? (
            <div className="status-actions">
              {showLaunchAction ? (
                <button
                  type="button"
                  onClick={requiresSettings ? onOpenSettings : onLaunchHost}
                  disabled={busy && !requiresSettings}
                >
                  {requiresSettings ? "前往设置" : busy ? "正在启动..." : "启动 Host"}
                </button>
              ) : null}
              {showRecoveryActions ? (
                <>
                  <button type="button" onClick={onResumeDiscovery} disabled={busy}>
                    {busy ? "正在重新扫描..." : "重新扫描"}
                  </button>
                  <button type="button" onClick={onOpenSettings}>
                    前往设置
                  </button>
                </>
              ) : null}
            </div>
          ) : null}
        </div>
      </PageCard>
    </WorkspaceContainer>
  );
}

function HomeStatusWorkspace(props: {
  bootstrap: BootstrapSnapshot | null;
  definitions: AppDefinition[];
  hostSessionStatus: HostSessionStatus;
  instances: AppInstance[];
  settings: SettingsSnapshot | null;
  versionCompatibility: VersionCompatibilityResult | null;
  onAddDefinition: () => void;
  onEditDefinition: (identity: AppDefinitionIdentity) => void;
  onViewInstanceDefinition: (instance: AppInstance) => void;
}) {
  const {
    bootstrap,
    definitions,
    hostSessionStatus,
    instances,
    settings,
    versionCompatibility,
    onAddDefinition,
    onEditDefinition,
    onViewInstanceDefinition,
  } = props;

  const [instancesCollapsed, setInstancesCollapsed] = useState(false);
  const [definitionsCollapsed, setDefinitionsCollapsed] = useState(false);
  const showInventories = hostSessionStatus === "connected";
  const versionNotice = versionCompatibility && versionCompatibility.status !== "compatible"
    ? versionCompatibility
    : null;
  const definitionIndex = new Map(definitions.map((definition) => [definitionIdentityKey(definition), definition]));
  const instanceItems = instances.map((instance) =>
    createInstanceInventoryItem(
      instance,
      definitionIndex.get(definitionIdentityKey(createDefinitionIdentity(instance.appId, instance.scope))),
      onViewInstanceDefinition,
    ),
  );
  const definitionItems = definitions.map((definition) =>
    createDefinitionInventoryItem(definition, onEditDefinition),
  );

  return (
    <WorkspaceContainer className="home-workspace">
      <h1 className="sr-only">主页</h1>

      <PageCard className="host-summary-card">
        <header className="host-header">
          <p className={`host-endpoint session-${hostSessionStatus}`}>
            {bootstrap?.connection?.rpcEndpoint ?? "未连接"}
          </p>
          <p className="host-directory">
            数据目录：{bootstrap?.effectiveDataDir ?? settings?.effectiveDataDir ?? "加载中"}
          </p>
        </header>
      </PageCard>

      {versionNotice ? (
        <div className="warning-banner" role="status">
          <p>{getVersionGuidanceTitle(versionNotice.status)}</p>
          <p>{formatVersionGuidanceMessage(versionNotice)}</p>
        </div>
      ) : null}

      {showInventories ? (
        <>
          <InventorySection
            collapsed={instancesCollapsed}
            count={instanceItems.length}
            title="App 实例"
            onToggle={() => setInstancesCollapsed((current) => !current)}
          >
            {instanceItems.length === 0 ? (
              <EmptyState title="当前没有 App 实例" />
            ) : (
              <InventoryList items={instanceItems} />
            )}
          </InventorySection>

          <InventorySection
            action={(
              <button
                type="button"
                className="icon-button icon-button-prominent"
                aria-label="新增定义"
                title="新增定义"
                onClick={onAddDefinition}
              >
                <PlusIcon />
              </button>
            )}
            collapsed={definitionsCollapsed}
            count={definitionItems.length}
            title="App 定义"
            onToggle={() => setDefinitionsCollapsed((current) => !current)}
          >
            {definitionItems.length === 0 ? (
              <EmptyState title="当前没有 App 定义" />
            ) : (
              <InventoryList items={definitionItems} />
            )}
          </InventorySection>
        </>
      ) : null}
    </WorkspaceContainer>
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
    <section className={`page-card section ${collapsed ? "collapsed" : ""}`}>
      <header className="section-header">
        <button
          type="button"
          className="section-trigger"
          aria-expanded={!collapsed}
          onClick={onToggle}
        >
          <span className={`section-caret ${collapsed ? "collapsed" : ""}`}>
            <ChevronIcon />
          </span>
          <span className="section-title-group">
            <span>{title}</span>
            <span className="section-count">({count})</span>
          </span>
        </button>

        {action ? <div className="section-action">{action}</div> : null}
      </header>

      {!collapsed ? <div className="section-body">{children}</div> : null}
    </section>
  );
}

function InventoryList(props: {
  items: InventoryItemViewModel[];
}) {
  const { items } = props;

  return (
    <div className="list">
      {items.map((item) => (
        <article key={item.key} className="list-item inventory-item" data-app-id={item.appId}>
          <div className="inventory-item-content">
            <div className="inventory-item-heading">
              <span className="inventory-item-name" title={item.title}>
                {item.title}
              </span>
              <div className="inventory-item-meta">
                <span className="inventory-item-app-id" title={item.appId}>
                  {item.appId}
                </span>
                <span className="inventory-item-scope" title={item.scopeLabel}>
                  {item.scopeLabel}
                </span>
              </div>
            </div>
            <p className="inventory-item-description" title={item.description}>
              {item.description}
            </p>
          </div>

          <div className="item-actions">
            <button
              type="button"
              className="icon-button"
              aria-label={item.actionAccessibleName}
              title={item.actionTitle}
              onClick={item.onAction}
            >
              {item.actionIcon}
            </button>
          </div>
        </article>
      ))}
    </div>
  );
}

function createDefinitionInventoryItem(
  definition: AppDefinition,
  onEditDefinition: (identity: AppDefinitionIdentity) => void,
): InventoryItemViewModel {
  const title = normalizeInventoryText(definition.displayName, definition.appId);
  const scopeLabel = formatDefinitionScopeLabel(definition.scope);
  const identity = createDefinitionIdentity(definition.appId, definition.scope);

  return {
    key: definitionIdentityKey(identity),
    appId: definition.appId,
    scopeLabel,
    title,
    description: normalizeInventoryText(definition.description, INVENTORY_DESCRIPTION_FALLBACK),
    actionAccessibleName: `编辑定义：${title}（${definition.appId}，${scopeLabel}）`,
    actionIcon: <EditIcon />,
    actionTitle: "编辑",
    onAction: () => onEditDefinition(identity),
  };
}

function createInstanceInventoryItem(
  instance: AppInstance,
  definition: AppDefinition | undefined,
  onViewInstanceDefinition: (instance: AppInstance) => void,
): InventoryItemViewModel {
  const title = normalizeInventoryText(definition?.displayName, instance.appId);
  const scopeLabel = formatDefinitionScopeLabel(instance.scope);

  return {
    key: instance.instanceId,
    appId: instance.appId,
    scopeLabel,
    title,
    description: normalizeInventoryText(
      definition?.description,
      "未找到精确 App Definition；该实例仅支持在线路由，不具备离线队列或自动启动能力",
    ),
    actionAccessibleName: `查看定义：${instance.instanceId}（${instance.appId}，${scopeLabel}）`,
    actionIcon: <ViewIcon />,
    actionTitle: "查看定义",
    onAction: () => onViewInstanceDefinition(instance),
  };
}

function normalizeInventoryText(value: string | undefined, fallback: string): string {
  const normalized = value?.trim();
  return normalized && normalized.length > 0 ? normalized : fallback;
}

function HelpWorkspace(props: {
  bootstrap: BootstrapSnapshot | null;
  hostSessionStatus: HostSessionStatus;
  openingLogKind: LogKind | null;
  settings: SettingsSnapshot | null;
  monitorVersionText: string;
  sdkVersionText: string;
  versionCompatibility: VersionCompatibilityResult | null;
  onOpenLogDirectory: (kind: LogKind) => void;
}) {
  const {
    bootstrap,
    hostSessionStatus,
    openingLogKind,
    settings,
    monitorVersionText,
    sdkVersionText,
    versionCompatibility,
    onOpenLogDirectory,
  } = props;
  const hostLogDirectory = formatHostLogDirectory(bootstrap?.effectiveDataDir ?? settings?.effectiveDataDir);
  const hasActiveHostSession = bootstrap?.phase === "host_available" && hostSessionStatus !== "idle";
  const hostVersionText = versionCompatibility
    ? formatVersionCompatibilityHostVersion(versionCompatibility.hostVersion)
    : hasActiveHostSession
      ? "检查中"
      : "未连接";
  const compatibilityText = versionCompatibility
    ? formatVersionCompatibilityStatusLabel(versionCompatibility.status)
    : hasActiveHostSession
      ? "检查中"
      : "未知";

  return (
    <WorkspaceContainer className="help-workspace">
      <h1 className="view-title">帮助</h1>

      <div className="info-grid support-group">
        <InfoCard>
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
        </InfoCard>

        <InfoCard>
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
        </InfoCard>

        <InfoCard>
          <div className="form-group">
            <label className="form-label">Monitor 版本</label>
            <div className="version-text">{monitorVersionText}</div>
          </div>
        </InfoCard>

        <InfoCard>
          <div className="form-group">
            <label className="form-label">内置 JS SDK 版本</label>
            <div className="version-text">{sdkVersionText}</div>
          </div>
        </InfoCard>

        <InfoCard>
          <div className="form-group">
            <label className="form-label">当前 Host 版本</label>
            <div className="version-text">{hostVersionText}</div>
          </div>
        </InfoCard>

        <InfoCard>
          <div className="form-group">
            <label className="form-label">兼容状态</label>
            <div className="version-text">{compatibilityText}</div>
          </div>
        </InfoCard>
      </div>
    </WorkspaceContainer>
  );
}

function TestWorkspace(props: {
  workspace: RpcTestWorkspaceViewModel;
  onChangeDraft: (value: string) => void;
  onValidate: () => void;
  onSend: () => void;
  onCancel: () => void;
}) {
  const { workspace, onChangeDraft, onValidate, onSend, onCancel } = props;

  return (
    <WorkspaceContainer className="test-workspace">
      <h1 className="view-title">测试</h1>

      <div className="support-group">
        <PageCard className="test-meta-card">
          <div className="form-group">
            <label className="form-label" htmlFor="rpc-test-endpoint">
              RPC 地址
            </label>
            <div id="rpc-test-endpoint" className="path-box">
              {workspace.rpcEndpoint ?? "未连接"}
            </div>
          </div>
        </PageCard>

        {!workspace.available ? (
          <div className="empty-state" role="status">
            当前没有可用 Host 连接，发送请求前请等待主页恢复连接状态。
          </div>
        ) : null}

        <PageCard className="test-panel">
          <label className="field">
            <span>JSON-RPC 请求文本</span>
            <textarea
              className="test-request-text"
              value={workspace.draft}
              placeholder={workspace.draftPlaceholder}
              spellCheck={false}
              onChange={(event) => onChangeDraft(event.target.value)}
              disabled={workspace.requestStatus === "waiting"}
            />
          </label>

          <div className="test-toolbar">
            <div className="button-row test-toolbar-actions">
              <button type="button" onClick={onValidate} disabled={!workspace.canValidate}>
                校验
              </button>
              <button type="button" onClick={onSend} disabled={!workspace.canSend}>
                发送请求
              </button>
              <button
                type="button"
                className="button-secondary"
                onClick={onCancel}
                disabled={!workspace.canCancel}
              >
                取消等待
              </button>
            </div>

            <span className={`test-status-pill status-${workspace.requestStatus}`}>
              {workspace.requestStatusLabel}
            </span>
          </div>

          {workspace.validationFeedback ? (
            <div
              className={`feedback-banner ${
                workspace.validationFeedback.kind === "success"
                  ? "feedback-success"
                  : "feedback-error"
              }`}
              role={workspace.validationFeedback.kind === "success" ? "status" : "alert"}
            >
              {workspace.validationFeedback.message}
            </div>
          ) : null}

          {workspace.requestError ? (
            <div className="inline-error" role="alert">
              {workspace.requestError}
            </div>
          ) : null}

          <div className="test-status-grid">
            <InfoCard>
              <div className="form-group">
                <label className="form-label" htmlFor="rpc-test-status">
                  请求状态
                </label>
                <div id="rpc-test-status" className="path-box status-box">
                  {workspace.requestStatusDetail}
                </div>
              </div>
            </InfoCard>

            <InfoCard className="test-result-card">
              <div className="form-group">
                <label className="form-label" htmlFor="rpc-test-result">
                  结果文本
                </label>
                <pre id="rpc-test-result" className="test-result-box">
                  {workspace.resultText ?? "当前没有可展示的响应。"}
                </pre>
              </div>
            </InfoCard>
          </div>
        </PageCard>
      </div>
    </WorkspaceContainer>
  );
}

function SettingsWorkspace(props: {
  busy: boolean;
  fieldErrors: SettingsFieldErrors;
  settingsDirty: boolean;
  settingsDraft: MonitorSettings;
  showHideHostCommandLineWindowOption: boolean;
  onChangeField: (
    field: keyof MonitorSettings,
    value: MonitorSettings[keyof MonitorSettings],
  ) => void;
  onSelectHostExecutablePath: () => void;
  onSelectDataDirectory: () => void;
  onSave: () => void;
}) {
  const {
    busy,
    fieldErrors,
    settingsDirty,
    settingsDraft,
    showHideHostCommandLineWindowOption,
    onChangeField,
    onSelectHostExecutablePath,
    onSelectDataDirectory,
    onSave,
  } = props;

  return (
    <WorkspaceContainer className="settings-workspace">
      <h1 className="view-title">设置</h1>

      <div className="settings-form">
        <PageCard>
          <label className="field">
            <span>Host 可执行文件路径</span>
            <div className="field-input-row">
              <input
                type="text"
                value={settingsDraft.hostExecutablePath ?? ""}
                placeholder="请选择 DevHub.Host 可执行文件路径"
                onChange={(event) => onChangeField("hostExecutablePath", event.target.value)}
              />
              <button
                type="button"
                className="icon-button"
                aria-label="选择 Host 可执行文件"
                title="选择 Host 可执行文件"
                onClick={onSelectHostExecutablePath}
                disabled={busy}
              >
                <FileIcon />
              </button>
            </div>
            <FieldError message={fieldErrors.hostExecutablePath} />
          </label>
        </PageCard>

        <PageCard>
          <label className="field">
            <span>Host 数据目录</span>
            <div className="field-input-row">
              <input
                type="text"
                value={settingsDraft.dataDirOverride ?? ""}
                placeholder="留空表示使用环境变量或平台默认目录"
                onChange={(event) => onChangeField("dataDirOverride", event.target.value)}
              />
              <button
                type="button"
                className="icon-button"
                aria-label="选择 Host 数据目录"
                title="选择 Host 数据目录"
                onClick={onSelectDataDirectory}
                disabled={busy}
              >
                <FolderIcon />
              </button>
            </div>
            <FieldError message={fieldErrors.dataDirOverride} />
          </label>
        </PageCard>

        {showHideHostCommandLineWindowOption ? (
          <PageCard>
            <div className="field">
              <span>启动行为</span>
              <label className="check-field">
                <input
                  type="checkbox"
                  checked={settingsDraft.hideHostCommandLineWindow ?? true}
                  onChange={(event) => onChangeField("hideHostCommandLineWindow", event.target.checked)}
                  disabled={busy}
                />
                <span>隐藏 Host 命令行窗口</span>
              </label>
            </div>
          </PageCard>
        ) : null}

        <PageCard className="page-actions settings-actions">
          <div className="definition-actions">
            <div className="action-spacer" />
            <button type="button" onClick={onSave} disabled={busy || !settingsDirty}>
              {busy ? "保存中..." : "保存设置"}
            </button>
          </div>
        </PageCard>
      </div>
    </WorkspaceContainer>
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
      <WorkspaceContainer className="definition-page">
        <div className="workspace-title-row">
          <button
            type="button"
            className="icon-button"
            aria-label="返回主页"
            title="返回主页"
            onClick={onClose}
          >
            <BackIcon />
          </button>
          <h1 className="view-title">App Definition</h1>
        </div>
        <PageCard>
          <EmptyState title="正在准备 App Definition 工作区" />
        </PageCard>
      </WorkspaceContainer>
    );
  }

  const submitLabel = workspace.mode === "create" ? "创建定义" : "保存修改";
  const canDelete = workspace.mode === "edit" && !workspace.readOnly && !workspace.missing;
  const disableInputs = workspace.readOnly || workspace.loading || workspace.saving;

  return (
    <WorkspaceContainer className="definition-page">
      <div className="workspace-title-row">
        <button
          type="button"
          className="icon-button"
          aria-label="返回主页"
          title="返回主页"
          onClick={onClose}
          disabled={workspace.saving}
        >
          <BackIcon />
        </button>
        <h1 className="view-title">{workspace.title}</h1>
      </div>

      {workspace.submitError ? (
        <div className="inline-error" role="alert">
          {workspace.submitError}
        </div>
      ) : null}

      {workspace.loading ? (
        <PageCard>
          <EmptyState title="正在读取定义" />
        </PageCard>
      ) : workspace.missing ? (
        <PageCard>
          <EmptyState title={workspace.emptyStateMessage ?? "定义不可用"} />
        </PageCard>
      ) : (
        <>
          <div className="definition-form">
            <PageCard className="form-section">
              <h2 className="section-title">基础信息</h2>
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
                  <span>scope</span>
                  <input
                    aria-label="scope"
                    type="text"
                    value={workspace.form.scope}
                    disabled={disableInputs || workspace.mode !== "create"}
                    onChange={(event) => onChangeField("scope", event.target.value)}
                  />
                  <FieldIssues issues={workspace.fieldErrors["definition.scope"]} />
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
            </PageCard>

            <PageCard className="form-section">
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
            </PageCard>

            <PageCard className="form-section">
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

                  <label className="field field-full">
                    <span>args</span>
                    <textarea
                      value={workspace.form.launchArgs}
                      disabled={disableInputs}
                      onChange={(event) => onChangeField("launchArgs", event.target.value)}
                    />
                    <FieldIssues issues={workspace.fieldErrors["definition.launch.args"]} />
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
            </PageCard>
          </div>
        </>
      )}

      {canDelete || workspace.readOnly || (!workspace.readOnly && !workspace.missing && !workspace.loading) ? (
        <PageCard className="page-actions definition-action-card">
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

            {!workspace.readOnly && !workspace.missing && !workspace.loading ? (
              <div className="button-row">
                <button type="button" onClick={onSubmit} disabled={workspace.saving}>
                  {workspace.saving ? "处理中..." : submitLabel}
                </button>
              </div>
            ) : null}
          </div>
        </PageCard>
      ) : null}
    </WorkspaceContainer>
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

function getDiscoveryTitle(snapshot?: BootstrapSnapshot | null): string {
  switch (snapshot?.phase) {
    case "settings_required":
      return "需要先补充 Host 设置";
    case "host_incompatible":
      return "当前 Host 版本不受支持";
    case "launch_available":
    case "scanning":
      return "正在搜索 DevHub Host";
    case "host_available":
      return "已连接到 DevHub Host";
    default:
      return "正在搜索 DevHub Host";
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

function TestIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <path d="M9 3h6" />
      <path d="M10 3v5.2a3 3 0 0 1-.63 1.84L6.7 13.5A4.5 4.5 0 0 0 10.2 21h3.6a4.5 4.5 0 0 0 3.5-7.5l-2.67-3.46A3 3 0 0 1 14 8.2V3" />
      <path d="M8 15h8" />
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
    <svg viewBox="0 0 24 24" aria-hidden="true" className="settings-icon">
      <circle cx="12" cy="12" r="3" />
      <path d="M19.4 15a1.7 1.7 0 0 0 .34 1.83l.05.05a2 2 0 0 1-2.82 2.83l-.06-.06a1.7 1.7 0 0 0-1.82-.34 1.7 1.7 0 0 0-1.03 1.57V21a2 2 0 0 1-4 0v-.09a1.7 1.7 0 0 0-1.03-1.57 1.7 1.7 0 0 0-1.82.34l-.06.06a2 2 0 0 1-2.82-2.83l.05-.05A1.7 1.7 0 0 0 4.6 15a1.7 1.7 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.7 1.7 0 0 0 4.6 8a1.7 1.7 0 0 0-.34-1.82l-.05-.06a2 2 0 1 1 2.82-2.82l.06.05A1.7 1.7 0 0 0 8.91 4.6h.01A1.7 1.7 0 0 0 10 3.09V3a2 2 0 0 1 4 0v.09a1.7 1.7 0 0 0 1.08 1.51h.01a1.7 1.7 0 0 0 1.82-.34l.06-.05a2 2 0 0 1 2.82 2.82l-.05.06A1.7 1.7 0 0 0 19.4 8v.01a1.7 1.7 0 0 0 1.51.99H21a2 2 0 0 1 0 4h-.09A1.7 1.7 0 0 0 19.4 15Z" />
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

function FileIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <path d="M8 3h6l5 5v13a1 1 0 0 1-1 1H8a3 3 0 0 1-3-3V6a3 3 0 0 1 3-3Z" />
      <path d="M14 3v5h5" />
    </svg>
  );
}

function FolderIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <path d="M3 8a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a3 3 0 0 1-3 3H6a3 3 0 0 1-3-3Z" />
      <path d="M3 10h18" />
    </svg>
  );
}

function BackIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <path d="M15 18 9 12l6-6" />
      <path d="M9 12h10" />
    </svg>
  );
}
