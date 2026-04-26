import type {
  AbandonedRequestFilter,
  AppInstance,
  DevHubClient,
  DevHubEventsClient,
  JsonRpcEventSession
} from "../../src/index.js";

declare const httpClient: DevHubClient;
declare const eventsClient: DevHubEventsClient;
declare const eventSession: JsonRpcEventSession;

const httpInstance: Promise<AppInstance> = httpClient.getInstance("inst-1");
const eventsInstance: Promise<AppInstance> = eventsClient.getInstance("inst-1");
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

// @ts-expect-error olderThanMs must be a number.
const invalidFilter: AbandonedRequestFilter = { olderThanMs: "1000" };

// @ts-expect-error method must be a string.
void eventsClient.clearAbandonedRequests({ method: 123 });

void httpInstance;
void eventsInstance;
void filter;
void abandonedCount;
void sessionAbandonedCount;
void clearedCount;
void invalidFilter;
