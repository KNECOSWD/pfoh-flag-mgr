import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import {
  CACHE_ONLY_ACCESS_TOKEN,
  accountIsAdmin,
  clearStickyAdmin,
  lookupCachedAdminRole,
  noteServerAdmin,
  readStickyAdmin,
  resolvePublicAdmin
} from "../src/accountRoles.js";
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

function jwt(payload) {
  const body = Buffer.from(JSON.stringify(payload)).toString("base64url");
  return `header.${body}.sig`;
}

test("admin menu on public pages follows the server flag and the cached API access token", async () => {
  const storage = memoryStorage();
  const admin = { homeAccountId: "admin-1", idTokenClaims: {} };
  noteServerAdmin(storage, admin.homeAccountId, true);
  const afterAdminPage = resolvePublicAdmin({
    account: admin,
    sticky: readStickyAdmin(storage, admin.homeAccountId),
    cachedApiAdmin: false
  });
  assert.equal(readStickyAdmin(storage, admin.homeAccountId), true);
  assert.equal(accountIsAdmin(admin), false);
  assert.equal(afterAdminPage.showAdminMenu, true);
  assert.equal(afterAdminPage.showAdminActions, true);

  const fresh = { homeAccountId: "admin-2", idTokenClaims: {} };
  let networkCalls = 0;
  const silentCalls = [];
  const cached = await lookupCachedAdminRole({
    account: fresh,
    scopes: ["api://pfoh/access_as_user"],
    cacheLookupPolicy: CACHE_ONLY_ACCESS_TOKEN,
    acquireTokenSilent: async (request) => {
      silentCalls.push(request);
      return { accessToken: jwt({ roles: ["PFOH.Admin"] }) };
    }
  });
  assert.equal(networkCalls, 0);
  assert.equal(silentCalls.length, 1);
  assert.equal(silentCalls[0].cacheLookupPolicy, CACHE_ONLY_ACCESS_TOKEN);
  assert.equal(cached.isAdmin, true);
  assert.equal(cached.swallowed, false);
  const fromCache = resolvePublicAdmin({ account: fresh, sticky: false, cachedApiAdmin: cached.isAdmin });
  assert.equal(fromCache.showAdminMenu, true);

  let redirects = 0;
  let banners = 0;
  const missed = await lookupCachedAdminRole({
    account: fresh,
    cacheLookupPolicy: CACHE_ONLY_ACCESS_TOKEN,
    acquireTokenSilent: async () => {
      throw Object.assign(new Error("no_tokens_found"), { errorCode: "no_tokens_found" });
    }
  });
  assert.equal(missed.called, true);
  assert.equal(missed.swallowed, true);
  assert.equal(missed.isAdmin, false);
  assert.equal(redirects, 0);
  assert.equal(banners, 0);
  const hidden = resolvePublicAdmin({ account: fresh, sticky: false, cachedApiAdmin: false });
  assert.equal(hidden.showAdminMenu, false);
  assert.equal(hidden.showAdminActions, false);

  const supporterToken = await lookupCachedAdminRole({
    account: { homeAccountId: "person", idTokenClaims: { roles: ["Supporter"] } },
    cacheLookupPolicy: CACHE_ONLY_ACCESS_TOKEN,
    acquireTokenSilent: async () => ({ accessToken: jwt({ roles: ["Supporter"] }) })
  });
  assert.equal(supporterToken.isAdmin, false);
  assert.equal(resolvePublicAdmin({
    account: { homeAccountId: "person", idTokenClaims: { roles: ["Supporter"] } },
    sticky: false,
    cachedApiAdmin: false
  }).showAdminMenu, false);

  let anonymousCalls = 0;
  const anonymous = await lookupCachedAdminRole({
    account: null,
    cacheLookupPolicy: CACHE_ONLY_ACCESS_TOKEN,
    acquireTokenSilent: async () => {
      anonymousCalls += 1;
      return { accessToken: jwt({ roles: ["PFOH.Admin"] }) };
    }
  });
  assert.equal(anonymous.called, false);
  assert.equal(anonymousCalls, 0);
  assert.equal(resolvePublicAdmin({ account: null, sticky: false, cachedApiAdmin: false }).showAdminMenu, false);

  clearStickyAdmin(storage, admin.homeAccountId);
  assert.equal(readStickyAdmin(storage, admin.homeAccountId), false);

  noteServerAdmin(storage, "admin-a", true);
  noteServerAdmin(storage, "admin-b", true);
  clearStickyAdmin(storage, "admin-a");
  assert.equal(readStickyAdmin(storage, "admin-a"), false);
  assert.equal(readStickyAdmin(storage, "admin-b"), true);

  const idTokenOnly = resolvePublicAdmin({
    account: { homeAccountId: "id-only", idTokenClaims: { roles: ["Admin"] } },
    sticky: false,
    cachedApiAdmin: false
  });
  assert.equal(idTokenOnly.showAdminMenu, true);

  const app = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");
  assert.match(app, /cacheLookupPolicy: CacheLookupPolicy\.AccessToken/);
  assert.match(app, /instance\.acquireTokenSilent\(request\)/);
  assert.match(app, /noteServerAdmin\(window\.sessionStorage, account\.homeAccountId, true\)/);
  assert.match(app, /clearStickyAdmin\(window\.sessionStorage, account\?\.homeAccountId\)/);
  const lookupEffect = app.slice(app.indexOf("lookupCachedAdminRole({"), app.indexOf("}, [activeRoute, accountKey, adminFromApi, instance]);"));
  assert.doesNotMatch(lookupEffect, /sharedTokenRequest|getToken\(|acquireTokenRedirect|setError\(/);
  assert.match(app, /const isAdmin = publicAdmin \? publicAdmin\.isAdmin : adminFromApi/);
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
