import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { isRetryableNetworkError, withNetworkRetry } from "../src/tokenRequest.js";

test("network failures retry once and auth or server errors do not", async () => {
  assert.equal(isRetryableNetworkError(new Error("Failed to fetch")), true);
  assert.equal(isRetryableNetworkError(Object.assign(new Error("The user aborted a request."), { name: "AbortError" })), true);
  assert.equal(isRetryableNetworkError(new Error("Network request failed")), true);
  assert.equal(isRetryableNetworkError(new Error("The operation was cancelled")), true);
  assert.equal(isRetryableNetworkError(new Error("Request timeout")), true);
  assert.equal(isRetryableNetworkError(Object.assign(new Error("Authentication failed before the API request could be sent."), { status: 0 })), false);
  assert.equal(isRetryableNetworkError(Object.assign(new Error("nope"), { status: 401 })), false);
  assert.equal(isRetryableNetworkError(Object.assign(new Error("nope"), { status: 403 })), false);
  assert.equal(isRetryableNetworkError(Object.assign(new Error("nope"), { status: 500 })), false);

  let calls = 0;
  const value = await withNetworkRetry(async () => {
    calls += 1;
    if (calls === 1) throw new Error("Failed to fetch");
    return "ok";
  });
  assert.equal(value, "ok");
  assert.equal(calls, 2);

  let forbiddenCalls = 0;
  await assert.rejects(() => withNetworkRetry(async () => {
    forbiddenCalls += 1;
    throw Object.assign(new Error("Forbidden"), { status: 403 });
  }));
  assert.equal(forbiddenCalls, 1);
});

test("authorized requests retry a failed fetch once", () => {
  const api = readFileSync(new URL("../src/api.ts", import.meta.url), "utf8");
  assert.match(api, /withNetworkRetry/);
  assert.match(api, /function fetchAuthorizedBlob/);
  assert.match(api, /requestTimeoutSignal/);
  const request = api.slice(api.indexOf("async function request"), api.indexOf("async function downloadFile"));
  assert.match(request, /withNetworkRetry/);
});
