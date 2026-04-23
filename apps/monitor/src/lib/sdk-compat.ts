import type {
  AppDefinition,
  AppDefinitionIdentity,
  AppInstance,
  AppInstanceRegistration,
  DefinitionValidationResult,
  DevHubClient,
} from "@devhub/sdk";

type LegacyScope = string | null;
type MonitorScope = string | null | undefined;
type LegacyDefinitionIdentity = Omit<AppDefinitionIdentity, "scope"> & { scope: LegacyScope };
type LegacyDefinition = Omit<AppDefinition, "scope"> & { scope: LegacyScope };
type LegacyInstanceRegistration = Omit<AppInstanceRegistration, "scope"> & { scope: string };

interface LegacyDefinitionsClient {
  listDefinitions(): Promise<AppDefinition[]>;
  getDefinition(identity: LegacyDefinitionIdentity): Promise<AppDefinition>;
  validateDefinition(definition: LegacyDefinition): Promise<DefinitionValidationResult>;
  upsertDefinition(definition: LegacyDefinition): Promise<AppDefinition>;
  deleteDefinition(identity: LegacyDefinitionIdentity): Promise<void>;
  registerInstance(instance: LegacyInstanceRegistration, password: string): Promise<AppInstance>;
}

interface ScopedDefinitionsClient {
  listDefinitions(request: { scope: string | null }): Promise<AppDefinition[]>;
}

export function usesExplicitStringScope(client: Pick<DevHubClient, "listDefinitions">): boolean {
  return client.listDefinitions.length > 0;
}

export async function listAllDefinitions(hostClient: DevHubClient): Promise<AppDefinition[]> {
  if (usesExplicitStringScope(hostClient)) {
    return await (hostClient as DevHubClient & ScopedDefinitionsClient).listDefinitions({
      scope: null,
    });
  }

  return await (hostClient as DevHubClient & LegacyDefinitionsClient).listDefinitions();
}

export async function getDefinitionCompat(
  client: DevHubClient,
  identity: Pick<AppDefinitionIdentity, "appId" | "scope">,
): Promise<AppDefinition> {
  if (usesExplicitStringScope(client)) {
    return await client.getDefinition({
      appId: identity.appId,
      scope: normalizeModernScope(identity.scope),
    });
  }

  return await (client as DevHubClient & LegacyDefinitionsClient).getDefinition({
    appId: identity.appId,
    scope: normalizeLegacyScope(identity.scope),
  });
}

export async function validateDefinitionCompat(
  client: DevHubClient,
  definition: AppDefinition,
): Promise<DefinitionValidationResult> {
  if (usesExplicitStringScope(client)) {
    return await client.validateDefinition({
      ...definition,
      scope: normalizeModernScope(definition.scope),
    });
  }

  return await (client as DevHubClient & LegacyDefinitionsClient).validateDefinition({
    ...definition,
    scope: normalizeLegacyScope(definition.scope),
  });
}

export async function upsertDefinitionCompat(
  client: DevHubClient,
  definition: AppDefinition,
): Promise<AppDefinition> {
  if (usesExplicitStringScope(client)) {
    return await client.upsertDefinition({
      ...definition,
      scope: normalizeModernScope(definition.scope),
    });
  }

  return await (client as DevHubClient & LegacyDefinitionsClient).upsertDefinition({
    ...definition,
    scope: normalizeLegacyScope(definition.scope),
  });
}

export async function deleteDefinitionCompat(
  client: DevHubClient,
  identity: Pick<AppDefinitionIdentity, "appId" | "scope">,
): Promise<void> {
  if (usesExplicitStringScope(client)) {
    await client.deleteDefinition({
      appId: identity.appId,
      scope: normalizeModernScope(identity.scope),
    });
    return;
  }

  await (client as DevHubClient & LegacyDefinitionsClient).deleteDefinition({
    appId: identity.appId,
    scope: normalizeLegacyScope(identity.scope),
  });
}

export async function registerInstanceCompat(
  client: DevHubClient,
  instance: AppInstanceRegistration,
  password: string,
): Promise<AppInstance> {
  if (usesExplicitStringScope(client)) {
    return await client.registerInstance({
      ...instance,
      scope: normalizeModernScope(instance.scope),
    }, password);
  }

  return await (client as DevHubClient & LegacyDefinitionsClient).registerInstance({
    ...instance,
    scope: normalizeLegacyInstanceScope(instance.scope),
  }, password);
}

function normalizeLegacyScope(scope: MonitorScope): LegacyScope {
  return scope && scope.length > 0 ? scope : null;
}

function normalizeLegacyInstanceScope(scope: MonitorScope): string {
  return scope ?? "";
}

function normalizeModernScope(scope: MonitorScope): string {
  return scope ?? "";
}
