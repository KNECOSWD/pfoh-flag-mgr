import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { adminAttentionItems, showAdminAttention } from "../src/adminAttention.js";
import { isPublicApiPath } from "../src/publicApi.js";
import { isPublicPage } from "../src/publicRoutes.js";

const app = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");
const api = readFileSync(new URL("../src/api.ts", import.meta.url), "utf8");

function loadAdminData() {
  return app.slice(app.indexOf("async function loadAdminData"), app.indexOf("async function loadPrintHistory"));
}

test("the duplicates prompt shows the count, hides at zero, and matches the other prompts", () => {
  const duplicatesOnly = adminAttentionItems({
    pendingReview: 0,
    readyToPrint: 0,
    duplicatesForReview: 4,
    multipleClaimAlerts: 0
  });
  assert.deepEqual(duplicatesOnly, [
    {
      key: "duplicates-for-review",
      label: "Duplicates",
      count: 4,
      route: "/admin/duplicates"
    }
  ]);

  const hidden = adminAttentionItems({
    pendingReview: 2,
    readyToPrint: 1,
    duplicatesForReview: 0,
    multipleClaimAlerts: 0
  });
  assert.deepEqual(hidden.map((item) => item.label), ["Pending review", "Ready to print"]);
  assert.equal(hidden.find((item) => item.key === "duplicates-for-review"), undefined);

  const together = adminAttentionItems({
    pendingReview: 2,
    readyToPrint: 1,
    duplicatesForReview: 3,
    removalQueue: 3,
    multipleClaimAlerts: 1
  });
  assert.deepEqual(together.map((item) => item.key), [
    "pending-review",
    "ready-to-print",
    "duplicates-for-review",
    "removal-queue",
    "multiple-claim-alerts"
  ]);
  assert.equal(together.find((item) => item.key === "removal-queue")?.label, "Removal queue");
  assert.equal(together.find((item) => item.key === "removal-queue")?.route, "/admin/removal-queue");
  assert.equal(adminAttentionItems({ removalQueue: 0 }).find((item) => item.key === "removal-queue"), undefined);
  assert.equal(showAdminAttention(true, together), true);
  assert.equal(showAdminAttention(true, adminAttentionItems({ duplicatesForReview: 0 })), false);
  assert.equal(showAdminAttention(false, together), false);
});

test("the count comes from the existing pending request and adds no authenticated fetch", () => {
  const load = loadAdminData();
  assert.match(load, /if \(!account\) return;/);
  assert.equal(load.match(/loadAdminPanel\(/g).length, 4);
  assert.equal(load.match(/Promise\.all\(/g).length, 1);
  assert.doesNotMatch(load, /duplicateGroups/);
  assert.doesNotMatch(load, /\/api\/admin\/duplicates/);
  assert.match(load, /setOpenDuplicateGroups\(pendingResult\.value\.openDuplicateGroupCount\)/);
  assert.match(load, /setOpenDuplicateGroups\(0\)/);

  const pending = api.slice(api.indexOf("pending:"), api.indexOf("printQueue:"));
  assert.match(pending, /\/api\/admin\/review\/pending/);
  assert.match(pending, /X-Open-Duplicate-Groups/);
  assert.match(pending, /openDuplicateGroupCount/);
  assert.doesNotMatch(pending, /duplicates\/groups/);

  assert.match(app, /duplicatesForReview: openDuplicateGroups/);
  assert.match(app, /removalQueue: removalQueueCount/);
  assert.match(load, /removalQueueCount/);
  assert.match(load, /setRemovalQueueCount\(0\)/);
  assert.match(app, /chrome\.adminBanner && adminAlertItems\.length > 0/);
  assert.match(app, /aria-label="Administrator notifications"/);
  assert.match(app, /href=\{item\.route\}/);
});

test("non-admins and anonymous visitors get no duplicate prompt and no token call", () => {
  assert.equal(isPublicPage("/find"), true);
  assert.equal(isPublicPage("/flag-map"), true);
  assert.equal(isPublicApiPath("/api/honorees/search"), true);
  assert.equal(isPublicApiPath("/api/admin/review/pending"), false);
  assert.equal(isPublicPage("/admin/duplicates"), false);

  const requestFn = api.slice(api.indexOf("async function request"), api.indexOf("async function downloadFile"));
  const publicAt = requestFn.indexOf("isPublicApiPath(url)");
  const tokenAt = requestFn.indexOf("getToken(");
  assert.ok(publicAt >= 0 && tokenAt > publicAt, "public paths return before a token is acquired");

  const loadEffect = app.slice(app.indexOf("if (!kioskChrome(kioskLocked, true).loadSignedInData)"));
  assert.match(loadEffect.slice(0, 400), /isPublicPage\(activeRoute\)/);
  assert.match(loadAdminData(), /if \(!account\) return;/);

  const denied = loadAdminData().slice(loadAdminData().indexOf("if (!decision.isAdmin)"));
  assert.match(denied, /setIsAdmin\(false\)/);
  assert.match(denied, /setOpenDuplicateGroups\(0\)/);
  assert.equal(showAdminAttention(false, adminAttentionItems({ duplicatesForReview: 4 })), false);
});
