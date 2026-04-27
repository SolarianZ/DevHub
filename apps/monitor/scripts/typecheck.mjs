import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const tscEntrypoint = fileURLToPath(new URL("../node_modules/typescript/bin/tsc", import.meta.url));

runTsc(["-p", "tsconfig.sdk-source.json", "--noEmit"]);
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
