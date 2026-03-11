import type { JsonObject, JsonValue } from "./models.js";

export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

export function ensureRecord(value: unknown, location: string): Record<string, unknown> {
  if (!isRecord(value)) {
    throw new Error(`${location} returned an invalid JSON object.`);
  }

  return value;
}

export function ensureJsonValue(value: unknown, propertyName: string): JsonValue {
  return validateJsonValue(value, propertyName, new WeakSet<object>());
}

export function ensureJsonObject(value: unknown, propertyName: string): JsonObject {
  const parsed = ensureJsonValue(value, propertyName);
  if (!isRecord(parsed)) {
    throw new Error(`${propertyName} 必须为 JSON 对象。`);
  }

  return parsed;
}

export function readObject(payload: Record<string, unknown>, location: string, key: string): Record<string, unknown> {
  if (!(key in payload)) {
    throw new Error(`${location}.${key} is required.`);
  }

  const value = payload[key];
  if (!isRecord(value)) {
    throw new Error(`${location}.${key} must be an object.`);
  }

  return value;
}

export function readArray(payload: Record<string, unknown>, location: string, key: string): unknown[] {
  if (!(key in payload)) {
    throw new Error(`${location}.${key} is required.`);
  }

  const value = payload[key];
  if (!Array.isArray(value)) {
    throw new Error(`${location}.${key} must be an array.`);
  }

  return value;
}

export function readString(payload: Record<string, unknown>, location: string, key: string): string {
  if (!(key in payload)) {
    throw new Error(`${location}.${key} is required.`);
  }

  const value = payload[key];
  if (typeof value !== "string" || !value.trim()) {
    throw new Error(`${location}.${key} must be a non-empty string.`);
  }

  return value;
}

export function readOptionalString(payload: Record<string, unknown>, location: string, key: string): string | undefined {
  if (!(key in payload)) {
    return undefined;
  }

  const value = payload[key];
  if (value === null || value === undefined) {
    return undefined;
  }

  if (typeof value !== "string") {
    throw new Error(`${location}.${key} must be a string.`);
  }

  return value;
}

export function readOptionalStringOrNull(
  payload: Record<string, unknown>,
  location: string,
  key: string
): string | null | undefined {
  if (!(key in payload)) {
    return undefined;
  }

  const value = payload[key];
  if (value === null) {
    return null;
  }

  if (typeof value !== "string") {
    throw new Error(`${location}.${key} must be a string or null.`);
  }

  return value;
}

export function readBoolean(payload: Record<string, unknown>, location: string, key: string): boolean {
  if (!(key in payload)) {
    throw new Error(`${location}.${key} is required.`);
  }

  const value = payload[key];
  if (typeof value !== "boolean") {
    throw new Error(`${location}.${key} must be a boolean.`);
  }

  return value;
}

export function readOptionalBoolean(payload: Record<string, unknown>, location: string, key: string): boolean | undefined {
  if (!(key in payload)) {
    return undefined;
  }

  const value = payload[key];
  if (value === null || value === undefined) {
    return undefined;
  }

  if (typeof value !== "boolean") {
    throw new Error(`${location}.${key} must be a boolean.`);
  }

  return value;
}

export function readNumber(payload: Record<string, unknown>, location: string, key: string): number {
  if (!(key in payload)) {
    throw new Error(`${location}.${key} is required.`);
  }

  const value = payload[key];
  if (typeof value !== "number" || Number.isNaN(value)) {
    throw new Error(`${location}.${key} must be a number.`);
  }

  return value;
}

export function readPositiveInt(payload: Record<string, unknown>, location: string, key: string): number {
  const value = readNumber(payload, location, key);
  if (!Number.isInteger(value) || value < 1) {
    throw new Error(`${location}.${key} must be an integer greater than or equal to 1.`);
  }

  return value;
}

export function readOptionalIntAtLeast(
  payload: Record<string, unknown>,
  location: string,
  key: string,
  minimumValue: number
): number | undefined {
  if (!(key in payload)) {
    return undefined;
  }

  const value = payload[key];
  if (typeof value !== "number" || Number.isNaN(value) || !Number.isInteger(value) || value < minimumValue) {
    throw new Error(`${location}.${key} must be an integer greater than or equal to ${minimumValue}.`);
  }

  return value;
}

export function readDate(payload: Record<string, unknown>, location: string, key: string): Date {
  const value = readString(payload, location, key);
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    throw new Error(`${location}.${key} must be an ISO-8601 date string.`);
  }

  return date;
}

export function ensureRequiredInputString(value: unknown, propertyName: string): string {
  if (typeof value !== "string" || !value.trim()) {
    throw new Error(`${propertyName} 不能为空。`);
  }

  return value;
}

export function ensureOptionalInputString(
  value: unknown,
  propertyName: string,
  allowEmpty: boolean,
  emptyMessage = `${propertyName} 不能为空。`,
  allowNull = false
): string | null | undefined {
  if (value === undefined) {
    return undefined;
  }

  if (value === null) {
    if (allowNull) {
      return null;
    }

    throw new Error(`${propertyName} 类型非法。`);
  }

  if (typeof value !== "string") {
    throw new Error(`${propertyName} 类型非法。`);
  }

  if (!allowEmpty && !value.trim()) {
    throw new Error(emptyMessage);
  }

  return value;
}

export function ensureOptionalInputStringOrNull(value: unknown, propertyName: string): string | null | undefined {
  return ensureOptionalInputString(value, propertyName, true, `${propertyName} 不能为空。`, true);
}

export function ensureInputBoolean(value: unknown, propertyName: string): boolean {
  if (typeof value !== "boolean") {
    throw new Error(`${propertyName} 必须为布尔值。`);
  }

  return value;
}

export function ensureOptionalInputBoolean(value: unknown, propertyName: string): boolean | undefined {
  if (value === undefined || value === null) {
    return undefined;
  }

  return ensureInputBoolean(value, propertyName);
}

export function ensureInputNumber(value: unknown, propertyName: string): number {
  if (typeof value !== "number" || !Number.isFinite(value)) {
    throw new Error(`${propertyName} must be a number.`);
  }

  return value;
}

export function ensureOptionalInputIntegerAtLeast(
  value: unknown,
  propertyName: string,
  minimumValue: number,
  errorMessage: string
): number | undefined {
  if (value === undefined || value === null) {
    return undefined;
  }

  if (typeof value !== "number" || Number.isNaN(value) || !Number.isInteger(value) || value < minimumValue) {
    throw new Error(errorMessage);
  }

  return value;
}

export function ensureOptionalInputIntegerInRange(
  value: unknown,
  propertyName: string,
  minimumValue: number,
  maximumValue: number,
  errorMessage: string
): number | undefined {
  if (value === undefined || value === null) {
    return undefined;
  }

  if (
    typeof value !== "number"
    || Number.isNaN(value)
    || !Number.isInteger(value)
    || value < minimumValue
    || value > maximumValue
  ) {
    throw new Error(errorMessage);
  }

  return value;
}

function validateJsonValue(value: unknown, path: string, ancestors: WeakSet<object>): JsonValue {
  if (value === null) {
    return null;
  }

  switch (typeof value) {
    case "string":
    case "boolean":
      return value;
    case "number":
      if (!Number.isFinite(value)) {
        throw new Error(`${path} 必须为有限数字。`);
      }

      return value;
    case "object":
      if (Array.isArray(value)) {
        return validateJsonArray(value, path, ancestors);
      }

      if (!isPlainObject(value)) {
        throw new Error(`${path} 必须为普通对象。`);
      }

      return validateJsonObject(value, path, ancestors);
    default:
      throw new Error(`${path} 包含不支持的 JSON 类型。`);
  }
}

function validateJsonArray(value: unknown[], path: string, ancestors: WeakSet<object>): JsonValue[] {
  if (ancestors.has(value)) {
    throw new Error(`${path} 不能包含循环引用。`);
  }

  ancestors.add(value);
  try {
    const result: JsonValue[] = [];
    for (let index = 0; index < value.length; index += 1) {
      if (!(index in value)) {
        throw new Error(`${path}[${index}] 不能为数组空洞。`);
      }

      result.push(validateJsonValue(value[index], `${path}[${index}]`, ancestors));
    }

    return result;
  } finally {
    ancestors.delete(value);
  }
}

function validateJsonObject(
  value: Record<string, unknown>,
  path: string,
  ancestors: WeakSet<object>
): JsonObject {
  if (ancestors.has(value)) {
    throw new Error(`${path} 不能包含循环引用。`);
  }

  if (Object.getOwnPropertySymbols(value).some((symbol) => Object.prototype.propertyIsEnumerable.call(value, symbol))) {
    throw new Error(`${path} 不能包含 symbol 属性。`);
  }

  ancestors.add(value);
  try {
    const result: JsonObject = {};
    for (const [key, item] of Object.entries(value)) {
      result[key] = validateJsonValue(item, `${path}.${key}`, ancestors);
    }

    return result;
  } finally {
    ancestors.delete(value);
  }
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  if (!isRecord(value)) {
    return false;
  }

  const prototype = Object.getPrototypeOf(value);
  return prototype === Object.prototype || prototype === null;
}
