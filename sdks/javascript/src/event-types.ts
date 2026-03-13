export const APP_INSTANCE_REGISTERED = "app.instance.registered";
export const APP_INSTANCE_UNREGISTERED = "app.instance.unregistered";
export const INVOCATION_QUEUED = "invocation.queued";
export const INVOCATION_DELIVERED = "invocation.delivered";
export const INVOCATION_COMPLETED = "invocation.completed";
export const INVOCATION_FAILED = "invocation.failed";

const SUPPORTED_EVENT_TYPES = [
  APP_INSTANCE_REGISTERED,
  APP_INSTANCE_UNREGISTERED,
  INVOCATION_QUEUED,
  INVOCATION_DELIVERED,
  INVOCATION_COMPLETED,
  INVOCATION_FAILED
] as const;

export const ALL_EVENT_TYPES = new Set<string>(SUPPORTED_EVENT_TYPES);

export function ensureSupportedEventType(value: string, propertyName: string): string {
  if (!ALL_EVENT_TYPES.has(value)) {
    throw new Error(`${propertyName} must be a supported DevHub event type.`);
  }

  return value;
}
