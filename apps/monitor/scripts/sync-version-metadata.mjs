import fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const monitorDir = path.resolve(fileURLToPath(new URL("..", import.meta.url)));
const monitorPackageJsonPath = path.join(monitorDir, "package.json");
const generatedDir = path.join(monitorDir, "src", "generated");
const generatedMetadataPath = path.join(generatedDir, "version-metadata.json");
const monitorSdkSourceEnv = "DEVHUB_MONITOR_SDK_SOURCE";
const releaseSdkSource = "release";
const localSdkSource = "local-src";
const defaultSdkSource = localSdkSource;

async function main() {
  const sdkSource = readMonitorSdkSource(process.env);
  const monitorVersion = await readPackageVersion(monitorPackageJsonPath);
  const sdkVersion = await readPackageVersion(resolveSdkPackageJsonPath(sdkSource));
  const payload = {
    monitorVersion,
    sdkVersion,
  };
  const nextText = `${JSON.stringify(payload, null, 2)}\n`;
  const currentText = await fs.readFile(generatedMetadataPath, "utf8").catch((error) => {
    if (error && typeof error === "object" && "code" in error && error.code === "ENOENT") {
      return null;
    }

    throw error;
  });

  if (currentText === nextText) {
    return;
  }

  await fs.mkdir(generatedDir, { recursive: true });
  await fs.writeFile(generatedMetadataPath, nextText, "utf8");
}

function readMonitorSdkSource(env) {
  const candidate = env[monitorSdkSourceEnv]?.trim();
  if (!candidate) {
    return defaultSdkSource;
  }

  if (candidate === releaseSdkSource || candidate === localSdkSource) {
    return candidate;
  }

  throw new Error(
    `${monitorSdkSourceEnv} 必须为 ${releaseSdkSource} 或 ${localSdkSource}，实际收到：${candidate}`,
  );
}

function resolveSdkPackageJsonPath(sdkSource) {
  if (sdkSource === localSdkSource) {
    return path.resolve(monitorDir, "..", "..", "sdks", "javascript", "package.json");
  }

  return path.resolve(monitorDir, "node_modules", "@devhub", "sdk", "package.json");
}

async function readPackageVersion(packageJsonPath) {
  const packageJson = JSON.parse(await fs.readFile(packageJsonPath, "utf8"));
  const version = packageJson.version;

  if (typeof version !== "string" || version.trim().length === 0) {
    throw new Error(`无法从 ${packageJsonPath} 读取非空 version。`);
  }

  return version.trim();
}

await main();
