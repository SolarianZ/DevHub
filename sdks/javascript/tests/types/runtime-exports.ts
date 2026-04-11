import type {
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

const runtime: HubRuntime = {
  protocolVersion: 1,
  pid: 12345,
  httpBaseUrl: "http://127.0.0.1:47231",
  wsUrl: "ws://127.0.0.1:47231/ws",
  tokenFile: "/tmp/devhub/runtime/token.txt",
  startedAtUtc: new Date("2026-03-15T00:00:00Z"),
  runtimeTuning: {
    leaseSeconds: 30,
    onlineThresholdSeconds: 90,
    launchDedupeWindowSeconds: 15
  }
};

const connection: RuntimeConnectionInfo = {
  runtimeDirectory: "/tmp/devhub/runtime",
  token: "token-1",
  runtime,
  rpcEndpoint: `${runtime.httpBaseUrl}/rpc`,
  websocketEndpoint: runtime.wsUrl
};
const resolver: RuntimeResolver = new FileSystemRuntimeResolver();

type _RuntimeResolverContract = Assert<Implements<FileSystemRuntimeResolver, RuntimeResolver>>;

void DATA_DIR_ENV;
void discoverRuntime;
void resolveDataDirectory;
void connection;
void resolver;
