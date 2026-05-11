import { fileURLToPath } from "node:url";
import type {
  DevHubRuntimeView,
  HubRuntime,
  RuntimeConnectionInfo,
  RuntimeResolver
} from "../../src/index.js";
import {
  DATA_DIR_ENV,
  FileSystemRuntimeResolver,
  discoverRuntime,
  resolveDataDirectory
} from "../../src/runtime.js";

type Assert<T extends true> = T;
type Implements<A, B> = A extends B ? true : false;

const TEST_RUNTIME_DIRECTORY = absoluteTestPath("devhub/runtime");
const TEST_TOKEN_FILE = absoluteTestPath("devhub/runtime/token.txt");

const runtime: HubRuntime = {
  protocolVersion: 1,
  pid: 12345,
  httpBaseUrl: "http://127.0.0.1:47231",
  wsUrl: "ws://127.0.0.1:47231/ws",
  tokenFile: TEST_TOKEN_FILE,
  startedAtUtc: new Date("2026-03-15T00:00:00Z"),
  runtimeTuning: {
    leaseSeconds: 30,
    onlineThresholdSeconds: 90,
    launchDedupeWindowSeconds: 15,
    launchRegisterTimeoutSeconds: 45
  }
};

const connection: RuntimeConnectionInfo = {
  runtimeDirectory: TEST_RUNTIME_DIRECTORY,
  token: "token-1",
  runtime,
  rpcEndpoint: `${runtime.httpBaseUrl}/rpc`,
  websocketEndpoint: runtime.wsUrl
};
const runtimeView: DevHubRuntimeView = {
  protocolVersion: 1,
  pid: 12345,
  startedAtUtc: new Date("2026-03-15T00:00:00Z"),
  hubVersion: "0.7.0-test"
};
const resolver: RuntimeResolver = new FileSystemRuntimeResolver();

type _RuntimeResolverContract = Assert<Implements<FileSystemRuntimeResolver, RuntimeResolver>>;

void DATA_DIR_ENV;
void discoverRuntime;
void resolveDataDirectory;
void connection;
void runtimeView;
void resolver;

function absoluteTestPath(relativePath: string): string {
  return fileURLToPath(new URL(`../../../.tmp-test-paths/${relativePath}`, import.meta.url));
}
