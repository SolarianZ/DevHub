import { readFile, writeFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";

const packageJsonPath = fileURLToPath(new URL("../package.json", import.meta.url));
const outputPath = fileURLToPath(new URL("../src/sdk-version.ts", import.meta.url));

const packageJson = JSON.parse(await readFile(packageJsonPath, "utf-8"));

if (typeof packageJson.version !== "string" || !packageJson.version.trim()) {
  throw new Error("package.json.version 必须为非空字符串。");
}

const content = [
  "/**",
  " * 当前 JS/TS SDK 包版本。",
  " * 此文件由 scripts/sync-sdk-version.mjs 根据 package.json 生成。",
  " */",
  `export const SDK_VERSION = ${JSON.stringify(packageJson.version)};`,
  ""
].join("\n");

let currentContent = "";
try {
  currentContent = await readFile(outputPath, "utf-8");
} catch {
  currentContent = "";
}

if (currentContent !== content) {
  await writeFile(outputPath, content, "utf-8");
}
