import {
  APP_INSTANCE_REGISTERED,
  SUPPORTED_EVENT_TYPES,
  type DevHubEvent,
  type DevHubEventType
} from "../../src/index.js";

type Assert<T extends true> = T;
type IsEqual<A, B> = (
  (<T>() => T extends A ? 1 : 2) extends (<T>() => T extends B ? 1 : 2) ? true : false
);

const eventType: DevHubEventType = APP_INSTANCE_REGISTERED;
const supportedEventTypes: readonly DevHubEventType[] = SUPPORTED_EVENT_TYPES;
const event: DevHubEvent = {
  subscriptionId: "sub-1",
  type: APP_INSTANCE_REGISTERED,
  timeUtc: new Date("2026-03-15T00:00:00Z")
};

type _EventTypeMatchesSupportedList = Assert<IsEqual<(typeof SUPPORTED_EVENT_TYPES)[number], DevHubEventType>>;

void eventType;
void supportedEventTypes;
void event;
