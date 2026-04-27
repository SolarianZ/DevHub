import type { AppDefinition } from "@devhub/sdk";
import type { BootstrapSnapshot, MonitorRuntimeConnectionInfo } from "./models";
import { describe, expect, it } from "vitest";
import {
  createDefinitionIdentity,
  createMissingDefinitionForm,
  definitionIdentityKey,
  formatDefinitionScopeLabel,
  getHomeWorkspaceMode,
  removeDefinition,
  sortDefinitions,
  upsertDefinition,
} from "./monitor-ui";

function createDefinition(overrides: Partial<AppDefinition> = {}): AppDefinition {
  return {
    appId: "demo.app",
    scope: "",
    displayName: "Demo App",
    ...overrides,
  };
}

function createConnection(overrides: Partial<MonitorRuntimeConnectionInfo> = {}): MonitorRuntimeConnectionInfo {
  const runtimeOverrides = overrides.runtime ?? {};

  return {
    runtimeDirectory: overrides.runtimeDirectory ?? "/tmp/devhub/runtime",
    token: overrides.token ?? "test-token",
    rpcEndpoint: overrides.rpcEndpoint ?? "http://127.0.0.1:4123/rpc",
    websocketEndpoint: overrides.websocketEndpoint ?? "ws://127.0.0.1:4123/ws",
    runtime: {
      protocolVersion: 1,
      pid: 4321,
      httpBaseUrl: "http://127.0.0.1:4123",
      wsUrl: "ws://127.0.0.1:4123/ws",
      tokenFile: "/tmp/devhub/runtime/token.txt",
      startedAtUtc: "2026-04-12T02:03:04Z",
      runtimeTuning: {
        leaseSeconds: 30,
        onlineThresholdSeconds: 15,
        launchDedupeWindowSeconds: 5,
      },
      hubVersion: "0.7.0",
      ...runtimeOverrides,
    },
  };
}

function createBootstrapSnapshot(overrides: Partial<BootstrapSnapshot> = {}): BootstrapSnapshot {
  return {
    generation: 1,
    phase: "host_available",
    effectiveDataDir: "/tmp/devhub",
    dataDirSource: "settings_override",
    settings: {
      dataDirOverride: "/tmp/devhub",
      hostExecutablePath: "/tmp/DevHub.Host",
      hideHostCommandLineWindow: true,
    },
    hasConfiguredHostExecutable: true,
    connection: createConnection(),
    lastProblem: null,
    ...overrides,
  };
}

describe("monitor-ui definition helpers", () => {
  it("replaces only the exact scoped definition when upserting", () => {
    const globalDefinition = createDefinition({
      displayName: "Demo App Global",
    });
    const scopedDefinition = createDefinition({
      scope: "workspace-a",
      displayName: "Demo App Scoped",
    });
    const updatedScopedDefinition = createDefinition({
      scope: "workspace-a",
      displayName: "Demo App Scoped Updated",
    });

    expect(
      upsertDefinition([globalDefinition, scopedDefinition], updatedScopedDefinition),
    ).toEqual(
      sortDefinitions([globalDefinition, updatedScopedDefinition]),
    );
  });

  it("removes only the targeted scoped definition", () => {
    const globalDefinition = createDefinition({
      displayName: "Demo App Global",
    });
    const scopedDefinition = createDefinition({
      scope: "workspace-a",
      displayName: "Demo App Scoped",
    });

    expect(
      removeDefinition(
        [globalDefinition, scopedDefinition],
        createDefinitionIdentity("demo.app", "workspace-a"),
      ),
    ).toEqual([globalDefinition]);
  });

  it("preserves the requested identity in missing definition forms", () => {
    expect(
      createMissingDefinitionForm(createDefinitionIdentity("demo.app", "workspace-a")),
    ).toMatchObject({
      appId: "demo.app",
      scope: "workspace-a",
      enableRpc: false,
    });

    expect(
      createMissingDefinitionForm(createDefinitionIdentity("demo.app", null)),
    ).toMatchObject({
      appId: "demo.app",
      scope: "",
      enableRpc: false,
    });
  });

  it("keeps Global and literal global as distinct identities", () => {
    expect(definitionIdentityKey(createDefinitionIdentity("demo.app", null))).not.toBe(
      definitionIdentityKey(createDefinitionIdentity("demo.app", "global")),
    );
    expect(formatDefinitionScopeLabel(null)).toBe("scope：Global");
    expect(formatDefinitionScopeLabel("global")).toBe("scope：global");
    expect(
      sortDefinitions([
        createDefinition({
          scope: "global",
          displayName: "Literal Global",
        }),
        createDefinition({
          scope: "",
          displayName: "Global",
        }),
      ]),
    ).toEqual([
      createDefinition({
        scope: "",
        displayName: "Global",
      }),
      createDefinition({
        scope: "global",
        displayName: "Literal Global",
      }),
    ]);
  });

  it("preserves canonical non-empty scope text verbatim in definition identities", () => {
    expect(createDefinitionIdentity("demo.app", "Workspace-A.v2")).toEqual({
      appId: "demo.app",
      scope: "Workspace-A.v2",
    });
  });

  it("uses the backend bootstrap phase as the single source of truth for home mode", () => {
    expect(getHomeWorkspaceMode(createBootstrapSnapshot())).toBe("status");
    expect(
      getHomeWorkspaceMode(createBootstrapSnapshot({
        phase: "host_incompatible",
        connection: null,
        lastProblem: {
          code: "host_incompatible",
          message: "当前 Host 版本不受支持",
        },
      })),
    ).toBe("discovery");
    expect(
      getHomeWorkspaceMode(createBootstrapSnapshot({
        connection: null,
      })),
    ).toBe("discovery");
  });
});
