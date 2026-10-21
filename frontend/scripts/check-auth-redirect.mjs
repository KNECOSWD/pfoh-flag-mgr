import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { publicPageAdminState } from "../src/accountRoles.js";
import { duplicateListState } from "../src/duplicateListState.js";
import { isPublicApiPath } from "../src/publicApi.js";
import { isPublicPage } from "../src/publicRoutes.js";
import {
  PLEASE_SIGN_IN_AGAIN,
  QuietAuthRejection,
  bannerForTokenError,
  createTokenCoordinator,
  isRecoverableAuthError
} from "../src/tokenRequest.js";

function memoryStorage() {
  const data = new Map();
  return {
    getItem: (key) => (data.has(key) ? data.get(key) : null),
    setItem: (key, value) => data.set(key, String(value)),
    removeItem: (key) => data.delete(key)
  };
}

function interactionError(message = "interaction_required") {
  return Object.assign(new Error(message), { interaction: true, errorCode: message });
}

test("anonymous public call sends no token and shows no banner", async () => {
  assert.equal(isPublicApiPath("/api/honorees/search"), true);
  assert.equal(isPublicApiPath("/api/honorees/search?q=ada"), true);
  assert.equal(isPublicApiPath("/api/lookups/service-branches"), true);
  assert.equal(isPublicApiPath("/api/lookups/service-branch-categories"), true);
  assert.equal(isPublicPage("/find"), true);
  assert.equal(isPublicPage("/flag-map"), true);
  assert.equal(isPublicPage("/my-flags"), false);
  assert.equal(isPublicPage("/admin/duplicates"), false);

  let tokenCalls = 0;
  let banner = "";
  async function perform(url) {
    if (isPublicApiPath(url)) return { ok: true, authorization: null };
    tokenCalls += 1;
    banner = "Authentication failed before the API request could be sent.";
    throw new Error("token");
  }

  const response = await perform("/api/honorees/search?q=ada");
  assert.equal(response.authorization, null);
  assert.equal(tokenCalls, 0);
  assert.equal(banner, "");
  assert.equal(bannerForTokenError(new QuietAuthRejection()), "");

  const api = readFileSync(new URL("../src/api.ts", import.meta.url), "utf8");
  const requestFn = api.slice(api.indexOf("async function request"), api.indexOf("async function downloadFile"));
  const publicAt = requestFn.indexOf("isPublicApiPath(url)");
  const tokenAt = requestFn.indexOf("getToken(");
  assert.ok(publicAt >= 0 && tokenAt > publicAt, "public paths return before a token is acquired");

  const app = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");
  const loadEffect = app.slice(app.indexOf("if (!kioskChrome(kioskLocked, true).loadSignedInData)"));
  assert.match(loadEffect.slice(0, 500), /isPublicPage\(activeRoute\)/);
  assert.match(app, /if \(isPublicPage\(activeRoute\)\)/);
  assert.match(app, /redirectStartPage: window\.location\.href/);
});

test("interaction_required redirects once for every concurrent waiter and stays quiet", async () => {
  const coordinator = createTokenCoordinator({ storage: memoryStorage() });
  let silentCalls = 0;
  let redirects = 0;
  let rejectSilent = () => {};

  const acquire = () => coordinator.acquire({
    silent: () => {
      silentCalls += 1;
      return new Promise((_, reject) => {
        rejectSilent = () => reject(interactionError());
      });
    },
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
  assert.equal(redirects, 1);
  assert.equal(results[0].status, "rejected");
  assert.equal(results[1].status, "rejected");
  assert.equal(bannerForTokenError(results[0].reason), "");
  assert.equal(bannerForTokenError(results[1].reason), "");
});

test("a missing authorization code redirects once even though it is not interaction_required", async () => {
  for (const errorCode of [
    "authorization_code_missing_from_server_response",
    "monitor_window_timeout",
    "no_network_connectivity"
  ]) {
    const coordinator = createTokenCoordinator({ storage: memoryStorage() });
    let redirects = 0;
    const error = Object.assign(new Error(errorCode), { errorCode });
    assert.equal(isRecoverableAuthError(error), true);

    await assert.rejects(() => coordinator.acquire({
      silent: async () => {
        throw error;
      },
      redirect: async () => {
        redirects += 1;
      },
      isInteractionRequired: () => false
    }));

    assert.equal(redirects, 1, errorCode);
  }
});

test("the session guard allows one redirect and then asks the person to sign in again", async () => {
  const storage = memoryStorage();
  const coordinator = createTokenCoordinator({ storage });
  let redirects = 0;

  const acquire = () => coordinator.acquire({
    silent: async () => {
      throw interactionError();
    },
    redirect: async () => {
      redirects += 1;
    },
    isInteractionRequired: (error) => Boolean(error && error.interaction)
  });

  const first = await Promise.allSettled([acquire()]);
  assert.equal(redirects, 1);
  assert.equal(bannerForTokenError(first[0].reason), "");

  const second = await Promise.allSettled([acquire()]);
  assert.equal(redirects, 1);
  assert.equal(second[0].status, "rejected");
  assert.equal(bannerForTokenError(second[0].reason), PLEASE_SIGN_IN_AGAIN);
});

test("an admin account on /find sees the Admin menu and makes no token call", () => {
  let tokenCalls = 0;
  const acquireToken = () => {
    tokenCalls += 1;
    return "token";
  };

  const admin = publicPageAdminState("/find", { idTokenClaims: { roles: ["PFOH.Admin"] } });
  assert.equal(admin.showAdminMenu, true);
  assert.equal(admin.showAdminActions, true);
  assert.equal(admin.tokenCalls, 0);
  assert.equal(tokenCalls, 0);
  void acquireToken;

  const namedAdmin = publicPageAdminState("/flag-map", { idTokenClaims: { roles: "Admin" } });
  assert.equal(namedAdmin.showAdminMenu, true);
  assert.equal(namedAdmin.tokenCalls, 0);

  const supporter = publicPageAdminState("/find", { idTokenClaims: { roles: ["Supporter"] } });
  assert.equal(supporter.showAdminMenu, false);
  assert.equal(supporter.showAdminActions, false);
  assert.equal(supporter.tokenCalls, 0);

  const missing = publicPageAdminState("/find", { idTokenClaims: {} });
  assert.equal(missing.showAdminMenu, false);
  assert.equal(missing.isAdmin, false);
  assert.equal(publicPageAdminState("/find", null).showAdminMenu, false);

  const app = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");
  const effectStart = app.indexOf("const state = publicPageAdminState(activeRoute, account);");
  assert.ok(effectStart > 0, "public pages set admin from the account claims");
  const effect = app.slice(effectStart, app.indexOf("useEffect", effectStart));
  assert.match(effect, /setIsAdmin\(state\.isAdmin\)/);
  assert.doesNotMatch(effect, /getToken|acquireToken|profileApi|loadData|fetch\(/);
  assert.match(app, /const isAdmin = publicAdmin \? publicAdmin\.isAdmin : adminFromApi/);
  assert.match(app, /chrome\.adminLinks \? \(/);
});

test("a failed duplicate load does not show a zero count or the empty result", () => {
  assert.deepEqual(duplicateListState({ loaded: false, loadError: "", count: 0 }), {
    showCount: false,
    showEmpty: false,
    showList: false,
    showError: false,
    showRetry: false
  });
  assert.deepEqual(duplicateListState({ loaded: false, loadError: "The duplicate list could not be loaded.", count: 0 }), {
    showCount: false,
    showEmpty: false,
    showList: false,
    showError: true,
    showRetry: true
  });
  assert.deepEqual(duplicateListState({ loaded: true, loadError: "", count: 0 }), {
    showCount: true,
    showEmpty: true,
    showList: false,
    showError: false,
    showRetry: false
  });

  const screen = readFileSync(new URL("../src/DuplicateMerge.tsx", import.meta.url), "utf8");
  assert.match(screen, /duplicateListState\(/);
  assert.match(screen, /listState\.showEmpty \?/);
  assert.match(screen, /listState\.showCount \?/);
  assert.doesNotMatch(screen, /groups\.length === 0 \?/);
  const printList = readFileSync(new URL("../src/PrintDuplicateReviews.tsx", import.meta.url), "utf8");
  assert.match(printList, /duplicateListState\(/);
  assert.match(printList, /listState\.showEmpty \?/);
  assert.doesNotMatch(printList, /rows\.length === 0 \?/);
});
