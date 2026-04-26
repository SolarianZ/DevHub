import type {
  AppInstance,
  DevHubClient,
  DevHubEventsClient
} from "../../src/index.js";

declare const httpClient: DevHubClient;
declare const eventsClient: DevHubEventsClient;

const httpInstance: Promise<AppInstance> = httpClient.getInstance("inst-1");
const eventsInstance: Promise<AppInstance> = eventsClient.getInstance("inst-1");

// @ts-expect-error getInstance requires an instance id argument.
void httpClient.getInstance();

// @ts-expect-error getInstance only accepts string instance ids.
void eventsClient.getInstance(null);

void httpInstance;
void eventsInstance;
