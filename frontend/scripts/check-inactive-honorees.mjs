import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { isPublicPage } from "../src/publicRoutes.js";
import { confirmReactivationMessage, notRecorded } from "../src/inactiveHonoreeAdmin.js";

const app = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");
const page = readFileSync(new URL("../src/InactiveHonorees.tsx", import.meta.url), "utf8");
const helpers = readFileSync(new URL("../src/inactiveHonoreeAdmin.js", import.meta.url), "utf8");
const api = readFileSync(new URL("../src/api.ts", import.meta.url), "utf8");
const controller = readFileSync(new URL("../../backend/PFOH.Api/Controllers/AdminInactiveHonoreesController.cs", import.meta.url), "utf8");

test("the confirm step says the approval queue and no flag", () => {
  assert.equal(
    confirmReactivationMessage([{ name: "Ada Lovelace" }, { name: "Alan Turing" }]),
    "Send Ada Lovelace to the approval queue with no flag assigned. Send Alan Turing to the approval queue with no flag assigned."
  );
  assert.equal(notRecorded, "Not recorded");
});

test("inactive honorees stay behind the administrator gate", () => {
  assert.equal(isPublicPage("/admin/inactive"), false);
  assert.match(app, /"\/admin\/inactive"/);
  const guestStart = app.indexOf("{isAdminRoute && !kioskLocked && !isAdmin ? (");
  const toolsStart = app.indexOf("{isAdminRoute && chrome.adminLinks ? (");
  assert.ok(guestStart > 0 && toolsStart > guestStart);
  const guest = app.slice(guestStart, toolsStart);
  assert.doesNotMatch(guest, /InactiveHonorees/);
  assert.match(guest, /inactive honorees/);
  const tools = app.slice(toolsStart, app.indexOf("{isAdminReviewRoute ? ("));
  assert.match(tools, /<InactiveHonorees/);
  assert.match(app, /\{ route: "\/admin\/inactive", label: "Inactive honorees" \}/);
  const nav = app.match(/const adminNav: \{ route: AppRoute; label: string \}\[\] = \[[\s\S]*?\];/);
  assert.ok(nav);
  assert.match(nav[0], /Inactive honorees/);
  assert.doesNotMatch(app.slice(app.indexOf("const siteNav"), app.indexOf("const adminNav")), /\/admin\/inactive/);
  assert.match(controller, /\[Authorize\(Policy = "AdminOnly"\)\]/);
  assert.doesNotMatch(controller, /free-slots/);
  assert.match(api, /\/api\/admin\/inactive-honorees/);
  assert.doesNotMatch(api, /free-slots/);
  assert.match(page, /htmlFor="inactive-honoree-search"/);
  assert.match(page, /aria-label="Select all inactive honorees on this page"/);
  assert.match(page, /aria-label=\{row\.canReactivate \? `Select \$\{row\.name\}`/);
  assert.match(page, /className="btn btn-outline-secondary"[^>]*>Search</);
  assert.equal(page.match(/btn btn-primary/g)?.length ?? 0, 1);
  assert.match(page, /approval queue with no flag assigned/);
  assert.doesNotMatch(page, /original flag|open flag|Choose an open flag|free flag/i);
  assert.doesNotMatch(helpers, /original flag|open flag|Choose an open flag/i);
  assert.doesNotMatch(page, /—/);
  assert.doesNotMatch(helpers, /—/);
  assert.doesNotMatch(page, /lorem|placeholder/i);
});
