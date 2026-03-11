import type { JsonObject } from "./models.js";

export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

export function ensureRecord(value: unknown, location: string): Record<string, unknown> {
  if (!isRecord(value)) {
    throw new Error(`${location} returned an invalid JSON object.`);
  }

  return value;
}

export function ensureJsonObject(value: unknown, propertyName: string): JsonObject {
  let parsed: unknown;
  try {
    parsed = JSON.parse(JSON.stringify(value)) as unknown;
  } catch (error) {
    throw new Error(`${propertyName} must be a JSON-serializable object.`, { cause: error });
  }

  if (!isRecord(parsed)) {
    throw new Error(`${propertyName} must serialize to a JSON object.`);
  }

  return value as JsonObject;
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
  if (typeof value !== "number" || Number.isNaN(value)) {
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
