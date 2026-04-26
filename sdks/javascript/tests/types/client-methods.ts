import type {
  AbandonedRequestFilter,
  AppInstance,
  DevHubClient,
  DevHubEventsClient,
  JsonRpcEventSession,
  VersionCompatibilityResult
} from "../../src/index.js";
import { SDK_VERSION } from "../../src/index.js";

declare const httpClient: DevHubClient;
declare const eventsClient: DevHubEventsClient;
declare const eventSession: JsonRpcEventSession;

const httpInstance: Promise<AppInstance> = httpClient.getInstance("inst-1");
const eventsInstance: Promise<AppInstance> = eventsClient.getInstance("inst-1");
const httpHostVersion: Promise<string> = httpClient.getHostVersion();
const eventsHostVersion: Promise<string> = eventsClient.getHostVersion();
const httpCompatibility: Promise<VersionCompatibilityResult> = httpClient.checkVersionCompatibility();
const eventsCompatibility: Promise<VersionCompatibilityResult> = eventsClient.checkVersionCompatibility();
const sdkVersion: string = SDK_VERSION;
const filter: AbandonedRequestFilter = {
  olderThanMs: 60_000,
  appId: "sample.app",
  method: "hub.apps.listInstances"
};
const abandonedCount: number = eventsClient.getAbandonedRequestCount(filter);
const sessionAbandonedCount: number = eventSession.getAbandonedRequestCount();
const clearedCount: number = eventSession.clearAbandonedRequests({
  appId: "sample.app"
});

// @ts-expect-error getInstance requires an instance id argument.
void httpClient.getInstance();

// @ts-expect-error getInstance only accepts string instance ids.
void eventsClient.getInstance(null);

// @ts-expect-error getHostVersion does not accept arguments.
void httpClient.getHostVersion("unexpected");

// @ts-expect-error olderThanMs must be a number.
const invalidFilter: AbandonedRequestFilter = { olderThanMs: "1000" };

// @ts-expect-error method must be a string.
void eventsClient.clearAbandonedRequests({ method: 123 });

void httpInstance;
void eventsInstance;
void httpHostVersion;
void eventsHostVersion;
void httpCompatibility;
void eventsCompatibility;
void sdkVersion;
void filter;
void abandonedCount;
void sessionAbandonedCount;
void clearedCount;
void invalidFilter;
