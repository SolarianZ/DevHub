export const APP_INSTANCE_REGISTERED = "app.instance.registered";
export const APP_INSTANCE_UNREGISTERED = "app.instance.unregistered";
export const INVOCATION_QUEUED = "invocation.queued";
export const INVOCATION_DELIVERED = "invocation.delivered";
export const INVOCATION_COMPLETED = "invocation.completed";
export const INVOCATION_FAILED = "invocation.failed";

export const ALL_EVENT_TYPES = new Set([
  APP_INSTANCE_REGISTERED,
  APP_INSTANCE_UNREGISTERED,
  INVOCATION_QUEUED,
  INVOCATION_DELIVERED,
  INVOCATION_COMPLETED,
  INVOCATION_FAILED
]);

export { DevHubEventsClient } from "./events-client.js";
