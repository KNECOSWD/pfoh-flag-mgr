import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { VERSION_POLL_MS, startVersionWatch, versionNeedsReload, versionUrl } from "../src/versionReload.js";

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

test("the page shows a non-blocking reload prompt and does not reload on its own", () => {
  assert.match(app, /A new version is available\./);
  assert.match(app, /<button type="button" onClick=\{\(\) => window\.location\.reload\(\)\}>Reload<\/button>/);
  assert.match(app, /startVersionWatch\(/);
  assert.equal(app.split("window.location.reload()").length, 2, "reload runs only from the button");
  assert.doesNotMatch(app, /setInterval\(\s*\(\)\s*=>\s*window\.location\.reload/);
});
