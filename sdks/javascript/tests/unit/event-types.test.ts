import { expect, it } from "vitest";
import {
  ALL_EVENT_TYPES,
  APP_DEFINITION_DELETED,
  APP_DEFINITION_UPSERTED,
  APP_INSTANCE_REGISTERED,
  APP_INSTANCE_UNREGISTERED,
  INVOCATION_COMPLETED,
  INVOCATION_DELIVERED,
  INVOCATION_FAILED,
  INVOCATION_QUEUED,
  SUPPORTED_EVENT_TYPES,
  ensureSupportedEventType
} from "../../src/event-types.js";

it("M5_TS_UT_005 应按规范公开受支持事件类型列表", () => {
  expect(SUPPORTED_EVENT_TYPES).toEqual([
    APP_DEFINITION_UPSERTED,
    APP_DEFINITION_DELETED,
    APP_INSTANCE_REGISTERED,
    APP_INSTANCE_UNREGISTERED,
    INVOCATION_QUEUED,
    INVOCATION_DELIVERED,
    INVOCATION_COMPLETED,
    INVOCATION_FAILED
  ]);
  expect([...ALL_EVENT_TYPES]).toEqual([...SUPPORTED_EVENT_TYPES]);
});

it("M5_TS_UT_005 ensureSupportedEventType 应返回规范事件类型并拒绝未知值", () => {
  expect(ensureSupportedEventType(APP_INSTANCE_REGISTERED, "type")).toBe(APP_INSTANCE_REGISTERED);
  expect(() => ensureSupportedEventType("unknown.type", "type")).toThrow(/supported DevHub event type/i);
});
