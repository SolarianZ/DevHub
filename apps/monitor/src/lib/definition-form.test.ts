import { describe, expect, it } from "vitest";
import type { AppDefinition } from "@devhub/sdk";
import {
  createEmptyDefinitionForm,
  definitionFormToModel,
  definitionToForm,
  mapValidationIssues,
  validateDefinitionIdentifiers,
} from "./definition-form";

describe("definition-form helpers", () => {
  it("converts canonical identifiers while still trimming non-identifier text fields", () => {
    const form = createEmptyDefinitionForm();
    form.appId = "Sample.App_01";
    form.scope = "";
    form.displayName = " Sample App ";
    form.description = "   ";
    form.enableEvents = true;
    form.enableLaunch = true;
    form.launchExePath = " /tmp/devhub ";
    form.launchArgs = "--app\n{appId}\n\n";
    form.launchArgsTemplate = "   ";

    expect(definitionFormToModel(form)).toEqual<AppDefinition>({
      appId: "Sample.App_01",
      scope: "",
      displayName: "Sample App",
      capabilities: {
        rpc: true,
        events: true,
      },
      launch: {
        exePath: "/tmp/devhub",
        args: ["--app", "{appId}"],
      },
    });
  });

  it("allows launch configuration without exePath and prefers structured args over argsTemplate", () => {
    const form = createEmptyDefinitionForm();
    form.appId = "demo.app";
    form.scope = "";
    form.displayName = "Demo App";
    form.enableLaunch = true;
    form.launchArgs = "--label=Workspace A\n--scope\n{scopeOrGlobal}";
    form.launchArgsTemplate = "--legacy {scopeOrGlobal}";

    expect(definitionFormToModel(form)).toEqual<AppDefinition>({
      appId: "demo.app",
      scope: "",
      displayName: "Demo App",
      capabilities: {
        rpc: true,
      },
      launch: {
        args: ["--label=Workspace A", "--scope", "{scopeOrGlobal}"],
      },
    });
  });

  it("preserves raw identifier input without implicit normalization", () => {
    const form = createEmptyDefinitionForm();
    form.appId = " sample.app ";
    form.scope = " workspace-a ";
    form.displayName = "Sample App";

    expect(definitionFormToModel(form)).toEqual<AppDefinition>({
      appId: " sample.app ",
      scope: " workspace-a ",
      displayName: "Sample App",
      capabilities: {
        rpc: true,
      },
    });
  });

  it("restores launch and capability state when loading a definition into the form", () => {
    const definition: AppDefinition = {
      appId: "demo.app",
      scope: "workspace-a",
      displayName: "Demo",
      description: "Monitor test definition",
      capabilities: {
        rpc: false,
        events: true,
      },
      launch: {
        args: ["--app", "{appId}"],
        argsTemplate: "--headless",
        workingDirectory: "/tmp",
        dedupeKeyTemplate: "demo",
      },
    };

    expect(definitionToForm(definition)).toEqual({
      appId: "demo.app",
      scope: "workspace-a",
      displayName: "Demo",
      description: "Monitor test definition",
      enableRpc: false,
      enableEvents: true,
      enableLaunch: true,
      launchExePath: "",
      launchArgs: "--app\n{appId}",
      launchArgsTemplate: "--headless",
      launchWorkingDirectory: "/tmp",
      launchDedupeKeyTemplate: "demo",
    });
  });

  it("accepts only the latest canonical appId and scope grammar", () => {
    expect(validateDefinitionIdentifiers({
      appId: "Sample.App_01",
      scope: "Workspace-A.v2",
    })).toEqual({});

    expect(validateDefinitionIdentifiers({
      appId: ".sample.app",
      scope: "Workspace-A.v2",
    })).toEqual({
      "definition.appId": [
        {
          path: "definition.appId",
          code: "format",
          message: "appId 格式不合法。",
        },
      ],
    });

    expect(validateDefinitionIdentifiers({
      appId: "Sample.App_01",
      scope: "workspace.",
    })).toEqual({
      "definition.scope": [
        {
          path: "definition.scope",
          code: "format",
          message: "scope 格式不合法。",
        },
      ],
    });

    expect(validateDefinitionIdentifiers({
      appId: "  ",
      scope: "",
    })).toEqual({
      "definition.appId": [
        {
          path: "definition.appId",
          code: "required",
          message: "appId 不能为空。",
        },
      ],
    });
  });

  it("groups validation issues by their field path", () => {
    expect(
      mapValidationIssues([
        {
          path: "definition.appId",
          code: "required",
          message: "appId 不能为空。",
        },
        {
          path: "definition.appId",
          code: "format",
          message: "appId 格式不合法。",
        },
        {
          path: "definition.launch.exePath",
          code: "required",
          message: "exePath 不能为空。",
        },
      ]),
    ).toEqual({
      "definition.appId": [
        {
          path: "definition.appId",
          code: "required",
          message: "appId 不能为空。",
        },
        {
          path: "definition.appId",
          code: "format",
          message: "appId 格式不合法。",
        },
      ],
      "definition.launch.exePath": [
        {
          path: "definition.launch.exePath",
          code: "required",
          message: "exePath 不能为空。",
        },
      ],
    });
  });
});
