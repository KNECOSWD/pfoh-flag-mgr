import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { createElement as h } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { adminAttentionItems, showAdminAttention } from "../src/adminAttention.js";
import { AdminAttentionBanner } from "../src/adminAttentionBanner.js";
import { canRequestAdminAttention, requestAdminAttentionCounts } from "../src/adminAttentionLoad.js";
import { isPublicApiPath } from "../src/publicApi.js";
import { isPublicPage } from "../src/publicRoutes.js";

const app = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");
const api = readFileSync(new URL("../src/api.ts", import.meta.url), "utf8");
const bannerSource = readFileSync(new URL("../src/adminAttentionBanner.js", import.meta.url), "utf8");
const duplicateMerge = readFileSync(new URL("../src/DuplicateMerge.tsx", import.meta.url), "utf8");
const mergeHistory = readFileSync(new URL("../src/MergeHistory.tsx", import.meta.url), "utf8");

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
  assert.match(bannerSource, /"aria-label": "Administrator notifications"/);
  assert.match(bannerSource, /className: "adminAlertBanner"/);
  assert.match(bannerSource, /href: item\.route/);
  assert.match(app, /<AdminAttentionBanner/);
  const bannerAt = app.indexOf("<AdminAttentionBanner");
  const flagMapSkip = app.indexOf("{!isPublicFlagMapRoute ? (");
  assert.ok(bannerAt > 0 && flagMapSkip > bannerAt, "the bar is rendered on public routes, including /find and the public flag map");
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

function findItems(pendingReview, duplicatesForReview) {
  return adminAttentionItems({ pendingReview, duplicatesForReview }).map((item) => ({
    ...item,
    route: item.route
  }));
}

test("the attention bar renders on /find for an administrator", () => {
  const items = findItems(2, 3);
  const html = renderToStaticMarkup(h(AdminAttentionBanner, {
    show: canRequestAdminAttention({ isAuthenticated: true, isAdmin: true, loadSignedInData: true }) && items.length > 0,
    items,
    totalCount: items.reduce((total, item) => total + item.count, 0),
    activeRoute: "/find",
    onNavigate: () => {}
  }));

  assert.match(html, /class="adminAlertBanner"/);
  assert.match(html, /aria-label="Administrator notifications"/);
  assert.match(html, /Admin attention needed/);
  assert.match(html, /href="\/admin\/review"/);
  assert.match(html, />2</);
  assert.match(html, /Pending review/);
  assert.match(html, /href="\/admin\/duplicates"/);
  assert.match(html, />3</);
  assert.match(html, /Duplicates/);
  assert.equal(isPublicPage("/find"), true);
});

test("a non-admin or signed-out visitor does not render the bar or request counts", async () => {
  let calls = 0;
  const loadPending = async () => {
    calls += 1;
    return { items: [{ id: 1 }], openDuplicateGroupCount: 4 };
  };
  const gates = [
    { isAuthenticated: false, isAdmin: false, loadSignedInData: true },
    { isAuthenticated: true, isAdmin: false, loadSignedInData: true },
    { isAuthenticated: false, isAdmin: true, loadSignedInData: true },
    { isAuthenticated: true, isAdmin: true, loadSignedInData: false }
  ];

  for (const gate of gates) {
    assert.equal(canRequestAdminAttention(gate), false);
    const html = renderToStaticMarkup(h(AdminAttentionBanner, {
      show: canRequestAdminAttention(gate) && findItems(2, 3).length > 0,
      items: findItems(2, 3),
      totalCount: 5,
      activeRoute: "/find",
      onNavigate: () => {}
    }));
    assert.equal(html, "");
    const result = await requestAdminAttentionCounts(gate, loadPending);
    assert.equal(result.requested, false);
    assert.equal(result.pending, null);
  }

  assert.equal(calls, 0);
  const attention = app.slice(app.indexOf("async function refreshAttentionCounts"), app.indexOf("async function loadAdminData"));
  assert.match(attention, /canRequestAdminAttention|requestAdminAttentionCounts/);
  assert.match(attention, /adminApi\.pending/);
  assert.doesNotMatch(attention, /setInterval|setTimeout/);
});

test("attention counts load once and refetch after an administrator action", async () => {
  const payloads = [
    { items: [{ id: 1 }, { id: 2 }], openDuplicateGroupCount: 1 },
    { items: [{ id: 2 }], openDuplicateGroupCount: 0 }
  ];
  let calls = 0;
  const loadPending = async () => payloads[calls++];
  const gate = { isAuthenticated: true, isAdmin: true, loadSignedInData: true };

  const before = await requestAdminAttentionCounts(gate, loadPending);
  assert.equal(before.requested, true);
  assert.equal(before.pendingReview, 2);
  assert.equal(before.duplicatesForReview, 1);

  const after = await requestAdminAttentionCounts(gate, loadPending);
  assert.equal(after.requested, true);
  assert.equal(after.pendingReview, 1);
  assert.equal(after.duplicatesForReview, 0);
  assert.equal(calls, 2);

  const approve = app.slice(app.indexOf("async function approveReview"), app.indexOf("async function rejectReview"));
  const reject = app.slice(app.indexOf("async function rejectReview"), app.indexOf("async function openSelectedPrintPdf"));
  assert.match(approve, /refreshAttentionCounts\(\)/);
  assert.match(reject, /refreshAttentionCounts\(\)/);
  assert.match(app, /onCountsChanged=\{\(\) => \{ void refreshAttentionCounts\(\); \}\}/);
  assert.match(duplicateMerge, /onCountsChanged\?\.\(\)/);
  assert.match(duplicateMerge, /mergeDuplicates/);
  assert.match(duplicateMerge, /markNotDuplicates/);
  assert.match(mergeHistory, /onCountsChanged\?\.\(\)/);
  assert.match(mergeHistory, /undoMerge/);
  assert.match(app, /attentionLoadedForRef\.current === key/);
  assert.doesNotMatch(app.slice(app.indexOf("void refreshAttentionCounts();"), app.indexOf("const timers")), /setInterval/);
});
