import type { RuntimeResolver } from "./runtime.js";

let defaultRuntimeResolverPromise: Promise<RuntimeResolver> | undefined;

export async function getRuntimeResolver(runtimeResolver?: RuntimeResolver): Promise<RuntimeResolver> {
  if (runtimeResolver) {
    return runtimeResolver;
  }

  defaultRuntimeResolverPromise ??= import("./runtime.js").then(
    ({ FileSystemRuntimeResolver }) => new FileSystemRuntimeResolver()
  );
  return defaultRuntimeResolverPromise;
}
