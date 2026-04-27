import fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const monitorDir = path.resolve(fileURLToPath(new URL("..", import.meta.url)));
const monitorPackageJsonPath = path.join(monitorDir, "package.json");
const sdkPackageJsonPath = path.resolve(monitorDir, "..", "..", "sdks", "javascript", "package.json");
const generatedDir = path.join(monitorDir, "src", "generated");
const generatedMetadataPath = path.join(generatedDir, "version-metadata.json");

async function main() {
  const monitorVersion = await readPackageVersion(monitorPackageJsonPath);
  const sdkVersion = await readPackageVersion(sdkPackageJsonPath);
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

async function readPackageVersion(packageJsonPath) {
  const packageJson = JSON.parse(await fs.readFile(packageJsonPath, "utf8"));
  const version = packageJson.version;

  if (typeof version !== "string" || version.trim().length === 0) {
    throw new Error(`无法从 ${packageJsonPath} 读取非空 version。`);
  }

  return version.trim();
}

await main();
