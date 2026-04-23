import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const sdkSource = process.env.DEVHUB_MONITOR_SDK_SOURCE?.trim() || "release";
const tscEntrypoint = fileURLToPath(new URL("../node_modules/typescript/bin/tsc", import.meta.url));

if (sdkSource !== "release" && sdkSource !== "local-src") {
  console.error(`DEVHUB_MONITOR_SDK_SOURCE must be "release" or "local-src", received: ${sdkSource}`);
  process.exit(1);
}

const appTsConfig = sdkSource === "local-src" ? "tsconfig.local-src.json" : "tsconfig.release.json";

runTsc(["-p", appTsConfig, "--noEmit"]);
runTsc(["-p", "tsconfig.node.json", "--noEmit"]);

function runTsc(args) {
  const result = spawnSync(process.execPath, [tscEntrypoint, ...args], {
    stdio: "inherit",
  });

  if (result.error) {
    throw result.error;
  }

  if (typeof result.status === "number" && result.status !== 0) {
    process.exit(result.status);
  }
}
