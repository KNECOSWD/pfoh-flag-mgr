import assert from "node:assert/strict";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";
import {
  browserTelemetrySettings,
  loadTelemetryConfig,
  noteBrowserRoute,
  pageViewFromLocation,
  redactTelemetryItem,
  redactTelemetryTarget,
  resetBrowserTelemetryState,
  startBrowserTelemetry,
  telemetryIsEnabled
} from "../src/browserTelemetry.js";
import {
  COOKIE_NOTE_ACCEPT,
  COOKIE_NOTE_DECLINE,
  COOKIE_NOTE_STORAGE_KEY,
  COOKIE_NOTE_TEXT,
  acceptCookieNote,
  cookieNoteChoice,
  cookieNoteVisible,
  declineCookieNote
} from "../src/cookieNote.js";

const TOKEN = "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";
const GUID = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";
const CODE = "PFOH-7K3Q9X";

function memoryStorage(initial) {
  const values = new Map(Object.entries(initial || {}));
  return {
    getItem: (key) => (values.has(key) ? values.get(key) : null),
    setItem: (key, value) => values.set(key, String(value))
  };
}

test("the telemetry initializer strips tokens, query strings, and fragments", () => {
  assert.equal(redactTelemetryTarget(`/reactivate/${TOKEN}?next=/admin#done`), `/reactivate/{token}`);
  assert.equal(redactTelemetryTarget(`/reactivate/short-token?x=1#y`), "/reactivate/{token}");
  assert.equal(redactTelemetryTarget(`/submission/${GUID}?code=${CODE}#top`), "/submission/{token}");
  assert.equal(redactTelemetryTarget(`/receipt/${CODE}`), "/receipt/{token}");
  assert.equal(
    redactTelemetryTarget(`https://planoflagsofhonor.org/flag-map?flag=1401#spot`),
    "https://planoflagsofhonor.org/flag-map"
  );
  assert.equal(redactTelemetryTarget("/find?q=SecretName"), "/find");
  assert.equal(redactTelemetryTarget("/find#SecretName"), "/find");

  const admin = {
    baseType: "PageviewData",
    baseData: { name: "/admin/review", uri: "/admin/review?token=abc#tab" }
  };
  assert.equal(redactTelemetryItem(admin), false);
  assert.equal(redactTelemetryItem({ baseType: "RemoteDependencyData", baseData: { name: "GET /api/profile" } }), false);
  assert.equal(pageViewFromLocation(`/submission/${GUID}?code=${CODE}`).name, "/submission/{token}");
  assert.equal(pageViewFromLocation("/admin/activity?x=1").track, false);
  assert.equal(pageViewFromLocation("/flag-map?flag=9").track, true);
});

test("page view settings stay on the free tier and do not identify the visitor", () => {
  const settings = browserTelemetrySettings("InstrumentationKey=00000000-0000-0000-0000-000000000000");
  assert.equal(settings.samplingPercentage, 100);
  assert.equal(settings.disableAjaxTracking, true);
  assert.equal(settings.disableFetchTracking, true);
  assert.equal(settings.enableAjaxPerfTracking, false);
  assert.equal(settings.disableCookiesUsage, undefined);
  assert.equal(settings.isCookieUseDisabled, undefined);
  assert.equal(settings.extensionConfig.AppInsightsCfgSyncPlugin.blkCdnCfg, true);
  assert.equal(settings.extensionConfig.AppInsightsCfgSyncPlugin.cfgUrl, "");
  const source = readFileSync(new URL("../src/browserTelemetry.js", import.meta.url), "utf8");
  assert.equal(source.includes("setAuthenticatedUserContext"), false);
  const tree = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8")
    + readFileSync(new URL("../src/main.tsx", import.meta.url), "utf8");
  assert.equal(tree.includes("setAuthenticatedUserContext"), false);
  assert.match(tree, /noteBrowserRoute\(window\.location\.pathname\)/);
  assert.match(tree, /ensureBrowserTelemetry\(/);
});

test("telemetry stays off without a connection string and does not load the SDK", async () => {
  resetBrowserTelemetryState();
  assert.deepEqual(await loadTelemetryConfig(async () => ({ ok: true, json: async () => ({ enabled: false }) })), { enabled: false });
  assert.deepEqual(
    await loadTelemetryConfig(async () => ({ ok: true, json: async () => ({ enabled: true, connectionString: "  " }) })),
    { enabled: false }
  );
  assert.deepEqual(await loadTelemetryConfig(async () => { throw new Error("down"); }), { enabled: false });
  assert.deepEqual(await loadTelemetryConfig(async () => ({ ok: false, json: async () => ({}) })), { enabled: false });

  let loaded = false;
  const started = await startBrowserTelemetry({
    fetchConfig: async () => ({ enabled: false }),
    loadSdk: async () => {
      loaded = true;
      throw new Error("SDK should stay unloaded");
    }
  });
  assert.equal(started.enabled, false);
  assert.equal(loaded, false);
  assert.equal(telemetryIsEnabled(), false);
});

test("a route change tracks the redacted page and skips admin pages", async () => {
  resetBrowserTelemetryState();
  const views = [];
  let identified = false;
  class FakeInsights {
    constructor(options) {
      this.options = options;
      this.initializers = [];
    }

    loadAppInsights() {}

    addTelemetryInitializer(fn) {
      this.initializers.push(fn);
    }

    trackPageView(view) {
      for (const fn of this.initializers) fn({ baseType: "PageviewData", baseData: { ...view } });
      views.push(view);
    }

    setAuthenticatedUserContext() {
      identified = true;
    }
  }

  noteBrowserRoute(`/reactivate/${TOKEN}?x=1#y`);
  const started = await startBrowserTelemetry({
    fetchConfig: async () => ({
      enabled: true,
      connectionString: "InstrumentationKey=00000000-0000-0000-0000-000000000000"
    }),
    loadSdk: async () => ({ ApplicationInsights: FakeInsights })
  });
  assert.equal(started.enabled, true);
  assert.equal(telemetryIsEnabled(), true);
  assert.deepEqual(views, [{ name: "/reactivate/{token}", uri: "/reactivate/{token}" }]);
  noteBrowserRoute("/admin/review?tab=1");
  assert.equal(views.length, 1);
  noteBrowserRoute("/flag-map?flag=1401#spot");
  assert.deepEqual(views[1], { name: "/flag-map", uri: "/flag-map" });
  noteBrowserRoute("/flag-map");
  assert.equal(views.length, 2);
  assert.equal(identified, false);
  resetBrowserTelemetryState();
});

test("the cookie note is opt-in and stays hidden after No thanks", () => {
  assert.equal(
    COOKIE_NOTE_TEXT,
    "We use cookies to count visits and see which pages are used, so we can improve this site. They are not used for ads, and we don't sell your information. The counts are stored with Microsoft Azure."
  );
  assert.equal(COOKIE_NOTE_TEXT.includes("\u2014"), false);
  assert.equal(COOKIE_NOTE_ACCEPT, "OK");
  assert.equal(COOKIE_NOTE_DECLINE, "No thanks");
  const storage = memoryStorage();
  assert.equal(cookieNoteChoice(storage), "");
  assert.equal(cookieNoteVisible("", "/find"), true);
  assert.equal(cookieNoteVisible("", "/admin/review"), false);
  assert.equal(acceptCookieNote(storage), true);
  assert.equal(cookieNoteChoice(storage), "accepted");
  assert.equal(cookieNoteVisible("accepted", "/find"), false);
  storage.setItem(COOKIE_NOTE_STORAGE_KEY, "");
  const declined = memoryStorage();
  assert.equal(declineCookieNote(declined), true);
  assert.equal(cookieNoteChoice(declined), "declined");
  assert.equal(cookieNoteVisible("declined", "/flag-map"), false);
  declined.setItem(COOKIE_NOTE_STORAGE_KEY, "");
  assert.equal(cookieNoteChoice(declined), "");

  const note = readFileSync(new URL("../src/CookieNote.tsx", import.meta.url), "utf8");
  assert.match(note, /COOKIE_NOTE_TEXT/);
  assert.match(note, /btn-outline-secondary/);
  assert.match(note, /btn-link/);
  assert.doesNotMatch(note, /btn-primary/);
  assert.match(note, /toast-container position-fixed bottom-0/);
  assert.match(note, /id="cookie-note"/);
  assert.equal(note.includes(COOKIE_NOTE_TEXT), false);
  const main = readFileSync(new URL("../src/main.tsx", import.meta.url), "utf8");
  assert.match(main, /cookieNoteChoice\(browserStorage\(\)\) === "accepted"/);

  let copies = 0;
  function walk(dir) {
    for (const name of readdirSync(dir)) {
      const path = join(dir, name);
      if (statSync(path).isDirectory()) {
        walk(path);
        continue;
      }
      if (!/\.(js|tsx|ts|mjs)$/.test(name)) continue;
      const text = readFileSync(path, "utf8");
      if (text.includes(COOKIE_NOTE_TEXT)) copies += 1;
    }
  }
  walk(new URL("../src", import.meta.url).pathname);
  assert.equal(copies, 1);
});
