import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { createProfileLoadGate } from "../src/profileLoad.js";
import { isPublicApiPath } from "../src/publicApi.js";
import { createTokenCoordinator } from "../src/tokenRequest.js";

test("a signed-in account fetches its profile once during a re-render storm", () => {
  const gate = createProfileLoadGate();
  const loads = [];

  for (let render = 0; render < 100; render += 1) {
    if (gate.claim("matthew-account")) {
      loads.push("matthew-account");
    }
  }

  assert.deepEqual(loads, ["matthew-account"]);
  gate.settle("matthew-account");

  for (let render = 0; render < 20; render += 1) {
    assert.equal(gate.claim("matthew-account"), false);
  }
});

test("signing out and back in can load the profile again, and a different account loads once", () => {
  const gate = createProfileLoadGate();
  assert.equal(gate.claim(""), false);
  assert.equal(gate.claim("ada"), true);
  gate.settle("ada");
  assert.equal(gate.claim("ada"), false);
  assert.equal(gate.claim("grace"), true);
  gate.reset();
  assert.equal(gate.claim("ada"), true);
});

test("strict-mode remount before the profile returns can retry once, then stops", () => {
  const gate = createProfileLoadGate();
  assert.equal(gate.claim("matthew-account"), true);
  gate.release("matthew-account");
  assert.equal(gate.claim("matthew-account"), true);
  gate.settle("matthew-account");
  gate.release("matthew-account");
  assert.equal(gate.claim("matthew-account"), false);
});

test("the profile effect does not depend on the account object", () => {
  const source = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");
  const match = source.match(/profileApi\.get\([\s\S]*?\}, \[([^\]]*)\]\);/);
  assert.ok(match, "profile load effect was not found");
  assert.equal(match[1].replace(/\s+/g, " ").trim(), "isAuthenticated, accountKey");
});

test("search and lookups are public and do not wait on a token", () => {
  assert.equal(isPublicApiPath("/api/honorees/search"), true);
  assert.equal(isPublicApiPath("/api/honorees/search?q=test"), true);
  assert.equal(isPublicApiPath("/api/lookups/service-branches"), true);
  assert.equal(isPublicApiPath("/api/lookups/service-branch-categories"), true);
  assert.equal(isPublicApiPath("/api/profile"), false);
  assert.equal(isPublicApiPath("/api/admin/review/pending"), false);

  const api = readFileSync(new URL("../src/api.ts", import.meta.url), "utf8");
  assert.match(api, /if \(isPublicApiPath\(url\)\)/);
  assert.match(api, /publicRequest<ServiceBranch\[\]>/);
  assert.match(api, /publicRequest<ServiceBranchCategory\[\]>/);
});

test("parallel token requests share one silent call and do not redirect", async () => {
  const coordinator = createTokenCoordinator();
  let silentCalls = 0;
  let redirects = 0;
  let rejectSilent = () => {};

  const silent = () => {
    silentCalls += 1;
    return new Promise((_, reject) => {
      rejectSilent = () => reject(Object.assign(new Error("interaction_in_progress"), { interaction: true }));
    });
  };

  const acquire = () => coordinator.acquire({
    silent,
    redirect: async () => {
      redirects += 1;
    },
    isInteractionRequired: (error) => Boolean(error && error.interaction)
  });

  const first = acquire();
  const second = acquire();
  rejectSilent();
  const results = await Promise.allSettled([first, second]);

  assert.equal(silentCalls, 1);
  assert.equal(redirects, 0);
  assert.equal(results[0].status, "rejected");
  assert.equal(results[1].status, "rejected");
});

test("a single token request may redirect when sign-in is required", async () => {
  const coordinator = createTokenCoordinator();
  let redirects = 0;

  await assert.rejects(() => coordinator.acquire({
    silent: async () => {
      throw Object.assign(new Error("interaction_required"), { interaction: true });
    },
    redirect: async () => {
      redirects += 1;
    },
    isInteractionRequired: (error) => Boolean(error && error.interaction)
  }));

  assert.equal(redirects, 1);
});
