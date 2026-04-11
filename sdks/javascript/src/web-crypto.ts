export function createRandomUuid(): string {
  const cryptoApi = globalThis.crypto;
  if (!cryptoApi || typeof cryptoApi.randomUUID !== "function") {
    throw new Error("globalThis.crypto.randomUUID() 不可用。");
  }

  return cryptoApi.randomUUID();
}
