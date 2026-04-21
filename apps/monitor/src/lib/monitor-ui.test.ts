import type { AppDefinition } from "@devhub/sdk";
import { describe, expect, it } from "vitest";
import {
  createDefinitionIdentity,
  createMissingDefinitionForm,
  removeDefinition,
  sortDefinitions,
  upsertDefinition,
} from "./monitor-ui";

function createDefinition(overrides: Partial<AppDefinition> = {}): AppDefinition {
  return {
    appId: "demo.app",
    scope: null,
    displayName: "Demo App",
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
});
