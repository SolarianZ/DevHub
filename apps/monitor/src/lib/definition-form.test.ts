import { describe, expect, it } from "vitest";
import type { AppDefinition } from "@devhub/sdk";
import {
  createEmptyDefinitionForm,
  definitionFormToModel,
  definitionToForm,
  mapValidationIssues,
} from "./definition-form";

describe("definition-form helpers", () => {
  it("converts an empty scope to the Global definition identity", () => {
    const form = createEmptyDefinitionForm();
    form.appId = " sample.app ";
    form.scope = "";
    form.displayName = " Sample App ";
    form.description = "   ";
    form.enableEvents = true;
    form.enableLaunch = true;
    form.launchExePath = " /tmp/devhub ";
    form.launchArgsTemplate = "   ";

    expect(definitionFormToModel(form)).toEqual<AppDefinition>({
      appId: "sample.app",
      scope: "",
      displayName: "Sample App",
      capabilities: {
        rpc: true,
        events: true,
      },
      launch: {
        exePath: "/tmp/devhub",
      },
    });
  });

  it("preserves a non-empty scope verbatim when converting a form", () => {
    const form = createEmptyDefinitionForm();
    form.appId = "sample.app";
    form.scope = "  workspace-a  ";
    form.displayName = "Sample App";

    expect(definitionFormToModel(form)).toEqual<AppDefinition>({
      appId: "sample.app",
      scope: "  workspace-a  ",
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
        exePath: "/Applications/DevHub.Host",
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
      launchExePath: "/Applications/DevHub.Host",
      launchArgsTemplate: "--headless",
      launchWorkingDirectory: "/tmp",
      launchDedupeKeyTemplate: "demo",
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
