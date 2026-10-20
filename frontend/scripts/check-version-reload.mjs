import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { KIOSK_VERSION_IDLE_MS, VERSION_POLL_MS, startVersionWatch, versionNeedsReload, versionUrl } from "../src/versionReload.js";

const app = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");

test("a changed version asks for a reload once and the same version does not", async () => {
  assert.equal(versionNeedsReload("2.0.20", "2.0.20"), false);
  assert.equal(versionNeedsReload("2.0.20", ""), false);
  assert.equal(versionNeedsReload("2.0.20", "2.0.21"), true);
  assert.ok(VERSION_POLL_MS >= 60_000, "the version check is not a tight loop");

  let prompts = 0;
  let reads = 0;
  const timers = [];
  const stop = startVersionWatch({
    loadedVersion: "2.0.20",
    loadRemote: async () => {
      reads += 1;
      return reads === 1 ? "2.0.20" : "2.0.21";
    },
    onPrompt: () => {
      prompts += 1;
    },
    intervalMs: VERSION_POLL_MS,
    setIntervalFn: (fn) => {
      timers.push(fn);
      return timers.length;
    },
    clearIntervalFn: () => {}
  });

  assert.equal(prompts, 0);
  assert.equal(reads, 1, "one check when the watch starts, not a burst");
  await timers[0]();
  assert.equal(prompts, 1);
  await timers[0]();
  assert.equal(prompts, 1, "the prompt does not repeat and does not reload by itself");
  assert.equal(timers.length, 1);
  stop();
});

test("a failed version check does not prompt and does not throw", async () => {
  let prompts = 0;
  startVersionWatch({
    loadedVersion: "2.0.20",
    loadRemote: async () => {
      throw new Error("offline");
    },
    onPrompt: () => {
      prompts += 1;
    },
    setIntervalFn: () => 1,
    clearIntervalFn: () => {}
  });
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.equal(prompts, 0);
  assert.equal(versionUrl("http://127.0.0.1:5080"), "http://127.0.0.1:5080/api/version");
});

test("a kiosk does not prompt and reloads once after 60 seconds without touch or pointer input", async () => {
  assert.equal(KIOSK_VERSION_IDLE_MS, 60_000);

  let now = 0;
  let prompts = 0;
  let reloads = 0;
  const timeouts = [];
  let activity = null;
  startVersionWatch({
    loadedVersion: "2.0.20",
    kiosk: true,
    loadRemote: async () => "2.0.21",
    onPrompt: () => {
      prompts += 1;
    },
    onReload: () => {
      reloads += 1;
    },
    now: () => now,
    listenActivity: (note) => {
      activity = note;
      return () => {};
    },
    setIntervalFn: () => 1,
    clearIntervalFn: () => {},
    setTimeoutFn: (fn, ms) => {
      timeouts.push({ fn, ms });
      return timeouts.length;
    },
    clearTimeoutFn: () => {}
  });
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.equal(prompts, 0, "the kiosk does not show the prompt");
  assert.equal(reloads, 0, "a fresh kiosk does not reload before it is idle");
  assert.equal(timeouts[0].ms, 60_000);

  now = 10_000;
  activity();
  await timeouts[0].fn();
  assert.equal(reloads, 0, "touch or pointer input postpones the reload");
  assert.equal(timeouts.at(-1).ms, 60_000);

  now = 70_000;
  await timeouts.at(-1).fn();
  assert.equal(reloads, 1);
  await timeouts.at(-1).fn();
  assert.equal(reloads, 1, "the kiosk reloads once");
});

test("a kiosk that is already idle reloads when the version changes and does not prompt", async () => {
  let now = 0;
  let prompts = 0;
  let reloads = 0;
  startVersionWatch({
    loadedVersion: "2.0.20",
    kiosk: () => true,
    loadRemote: async () => {
      now = KIOSK_VERSION_IDLE_MS;
      return "2.0.21";
    },
    onPrompt: () => {
      prompts += 1;
    },
    onReload: () => {
      reloads += 1;
    },
    now: () => now,
    listenActivity: () => () => {},
    setIntervalFn: () => 1,
    clearIntervalFn: () => {},
    setTimeoutFn: () => {
      throw new Error("an already idle kiosk should reload without waiting again");
    },
    clearTimeoutFn: () => {}
  });
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.equal(prompts, 0);
  assert.equal(reloads, 1);
});

test("the page shows a non-blocking reload prompt and does not reload on its own", () => {
  assert.match(app, /A new version is available\./);
  assert.match(app, /<button type="button" onClick=\{\(\) => window\.location\.reload\(\)\}>Reload<\/button>/);
  assert.match(app, /startVersionWatch\(/);
  assert.match(app, /kiosk: \(\) => kioskLockedRef\.current/);
  const kioskShell = app.slice(app.indexOf('className="kioskLock"'), app.indexOf("<PublicFlagMap"));
  assert.doesNotMatch(kioskShell, /versionReloadNotice|versionReloadPrompt|A new version is available/);
  assert.equal(app.split("window.location.reload()").length, 3, "reload is the button and the idle kiosk");
  assert.match(app, /onReload: \(\) => \{\s*if \(!kioskLockedRef\.current\) return;\s*window\.location\.reload\(\);/);
  assert.doesNotMatch(app, /setInterval\(\s*\(\)\s*=>\s*window\.location\.reload/);
});
