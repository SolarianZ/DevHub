import type { RuntimeResolver } from "./runtime.js";

let defaultRuntimeResolverPromise: Promise<RuntimeResolver> | undefined;
const runtimeModulePath = "./runtime.js";

export async function getRuntimeResolver(runtimeResolver?: RuntimeResolver): Promise<RuntimeResolver> {
  if (runtimeResolver) {
    return runtimeResolver;
  }

  defaultRuntimeResolverPromise ??= import(
    /* @vite-ignore */ runtimeModulePath
  ).then(
    ({ FileSystemRuntimeResolver }) => new FileSystemRuntimeResolver()
  );
  return defaultRuntimeResolverPromise;
}
