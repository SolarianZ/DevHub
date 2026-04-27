import type {
  AppDefinition,
  AppDefinitionIdentity,
  AppInstance,
  AppInstanceRegistration,
  DefinitionValidationResult,
  DevHubClient,
  VersionCompatibilityResult,
} from "@devhub/sdk";

export async function listAllDefinitions(hostClient: DevHubClient): Promise<AppDefinition[]> {
  return await hostClient.listDefinitions({
    scope: null,
  });
}

export async function getDefinitionCompat(
  client: DevHubClient,
  identity: Pick<AppDefinitionIdentity, "appId" | "scope">,
): Promise<AppDefinition> {
  return await client.getDefinition({
    appId: identity.appId,
    scope: normalizeScope(identity.scope),
  });
}

export async function validateDefinitionCompat(
  client: DevHubClient,
  definition: AppDefinition,
): Promise<DefinitionValidationResult> {
  return await client.validateDefinition({
    ...definition,
    scope: normalizeScope(definition.scope),
  });
}

export async function upsertDefinitionCompat(
  client: DevHubClient,
  definition: AppDefinition,
): Promise<AppDefinition> {
  return await client.upsertDefinition({
    ...definition,
    scope: normalizeScope(definition.scope),
  });
}

export async function deleteDefinitionCompat(
  client: DevHubClient,
  identity: Pick<AppDefinitionIdentity, "appId" | "scope">,
): Promise<void> {
  await client.deleteDefinition({
    appId: identity.appId,
    scope: normalizeScope(identity.scope),
  });
}

export async function registerInstanceCompat(
  client: DevHubClient,
  instance: AppInstanceRegistration,
  password: string,
): Promise<AppInstance> {
  return await client.registerInstance({
    ...instance,
    scope: normalizeScope(instance.scope),
  }, password);
}

export async function checkVersionCompatibilityCompat(
  client: DevHubClient,
): Promise<VersionCompatibilityResult> {
  return await client.checkVersionCompatibility();
}

function normalizeScope(scope: string | null | undefined): string {
  return scope ?? "";
}
