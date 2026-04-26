import type { AppDefinition, ValidationIssue } from "@devhub/sdk";

const CANONICAL_IDENTIFIER_REGEX = /^[A-Za-z0-9_](?:[A-Za-z0-9_.-]*[A-Za-z0-9_])?$/;

export interface DefinitionFormState {
  appId: string;
  scope: string;
  displayName: string;
  description: string;
  enableRpc: boolean;
  enableEvents: boolean;
  enableLaunch: boolean;
  launchExePath: string;
  launchArgsTemplate: string;
  launchWorkingDirectory: string;
  launchDedupeKeyTemplate: string;
}

export type DefinitionIssueMap = Record<string, ValidationIssue[]>;

export function createEmptyDefinitionForm(): DefinitionFormState {
  return {
    appId: "",
    scope: "",
    displayName: "",
    description: "",
    enableRpc: true,
    enableEvents: false,
    enableLaunch: false,
    launchExePath: "",
    launchArgsTemplate: "",
    launchWorkingDirectory: "",
    launchDedupeKeyTemplate: "",
  };
}

export function definitionToForm(definition: AppDefinition): DefinitionFormState {
  return {
    appId: definition.appId,
    scope: definition.scope ?? "",
    displayName: definition.displayName,
    description: definition.description ?? "",
    enableRpc: definition.capabilities?.rpc ?? true,
    enableEvents: definition.capabilities?.events ?? false,
    enableLaunch: definition.launch !== undefined,
    launchExePath: definition.launch?.exePath ?? "",
    launchArgsTemplate: definition.launch?.argsTemplate ?? "",
    launchWorkingDirectory: definition.launch?.workingDirectory ?? "",
    launchDedupeKeyTemplate: definition.launch?.dedupeKeyTemplate ?? "",
  };
}

export function definitionFormToModel(form: DefinitionFormState): AppDefinition {
  const definition: AppDefinition = {
    appId: form.appId,
    scope: form.scope,
    displayName: form.displayName.trim(),
  };

  const description = normalizeOptionalText(form.description);
  if (description) {
    definition.description = description;
  }

  if (form.enableRpc || form.enableEvents) {
    definition.capabilities = {};
    if (form.enableRpc) {
      definition.capabilities.rpc = true;
    }
    if (form.enableEvents) {
      definition.capabilities.events = true;
    }
  }

  if (form.enableLaunch) {
    definition.launch = {
      exePath: form.launchExePath.trim(),
    };

    const argsTemplate = normalizeOptionalText(form.launchArgsTemplate);
    if (argsTemplate) {
      definition.launch.argsTemplate = argsTemplate;
    }

    const workingDirectory = normalizeOptionalText(form.launchWorkingDirectory);
    if (workingDirectory) {
      definition.launch.workingDirectory = workingDirectory;
    }

    const dedupeKeyTemplate = normalizeOptionalText(form.launchDedupeKeyTemplate);
    if (dedupeKeyTemplate) {
      definition.launch.dedupeKeyTemplate = dedupeKeyTemplate;
    }
  }

  return definition;
}

export function areDefinitionFormsEqual(left: DefinitionFormState, right: DefinitionFormState): boolean {
  return JSON.stringify(definitionFormToModel(left)) === JSON.stringify(definitionFormToModel(right));
}

export function validateDefinitionIdentifiers(
  definition: Pick<AppDefinition, "appId" | "scope">,
): DefinitionIssueMap {
  const issues: ValidationIssue[] = [];
  const appId = definition.appId ?? "";
  const scope = definition.scope ?? "";

  if (appId.trim().length === 0) {
    issues.push(createValidationIssue("definition.appId", "required", "appId 不能为空。"));
  } else if (!isCanonicalIdentifier(appId)) {
    issues.push(createValidationIssue("definition.appId", "format", "appId 格式不合法。"));
  }

  if (scope !== "" && !isCanonicalIdentifier(scope)) {
    issues.push(createValidationIssue("definition.scope", "format", "scope 格式不合法。"));
  }

  return mapValidationIssues(issues);
}

export function mapValidationIssues(errors: readonly ValidationIssue[]): DefinitionIssueMap {
  const result: DefinitionIssueMap = {};

  for (const issue of errors) {
    if (!result[issue.path]) {
      result[issue.path] = [];
    }

    result[issue.path].push(issue);
  }

  return result;
}

function normalizeOptionalText(value: string): string | undefined {
  const trimmed = value.trim();
  return trimmed ? trimmed : undefined;
}

function createValidationIssue(path: string, code: string, message: string): ValidationIssue {
  return {
    path,
    code,
    message,
  };
}

function isCanonicalIdentifier(value: string): boolean {
  return value.length > 0 && CANONICAL_IDENTIFIER_REGEX.test(value);
}
