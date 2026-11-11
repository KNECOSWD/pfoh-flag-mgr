import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { isPublicPage } from "../src/publicRoutes.js";
import {
  confirmRestorationMessage,
  conflictLead,
  needsFlagChoice,
  notRecorded,
  restorationPlan
} from "../src/inactiveHonoreeAdmin.js";

const app = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");
const page = readFileSync(new URL("../src/InactiveHonorees.tsx", import.meta.url), "utf8");
const helpers = readFileSync(new URL("../src/inactiveHonoreeAdmin.js", import.meta.url), "utf8");
const api = readFileSync(new URL("../src/api.ts", import.meta.url), "utf8");
const controller = readFileSync(new URL("../../backend/PFOH.Api/Controllers/AdminInactiveHonoreesController.cs", import.meta.url), "utf8");

test("the confirm step names each honoree and the flag that will be restored", () => {
  const free = restorationPlan({
    name: "Ada Lovelace",
    canReactivate: true,
    slotStatus: "free",
    formerFlagGridId: 4,
    formerFlagGridName: "C-11",
    slotDetail: null
  }, null);
  const none = restorationPlan({
    name: "Alan Turing",
    canReactivate: true,
    slotStatus: "none",
    formerFlagGridId: null,
    formerFlagGridName: null,
    slotDetail: null
  }, null);
  const taken = restorationPlan({
    name: "Grace Hopper",
    canReactivate: true,
    slotStatus: "taken",
    formerFlagGridId: 9,
    formerFlagGridName: "C-11",
    slotDetail: "It is assigned to Ada Lovelace."
  }, null);
  assert.equal(needsFlagChoice({ canReactivate: true, slotStatus: "taken" }), true);
  assert.equal(needsFlagChoice({ canReactivate: true, slotStatus: "free" }), false);
  assert.equal(taken.blocked, true);
  assert.match(conflictLead({
    formerFlagGridName: "C-11",
    slotStatus: "taken",
    slotDetail: "It is assigned to Ada Lovelace."
  }), /Flag C-11 is not open/);
  const chosen = restorationPlan({
    name: "Grace Hopper",
    canReactivate: true,
    slotStatus: "taken",
    formerFlagGridId: 9,
    formerFlagGridName: "C-11",
    slotDetail: "It is assigned to Ada Lovelace."
  }, { id: 12, flagGridName: "D-04" });
  assert.equal(chosen.blocked, false);
  assert.equal(confirmRestorationMessage([free, none, chosen]),
    "Restore Ada Lovelace to flag C-11. Restore Alan Turing as an active honoree with no flag position. Restore Grace Hopper to flag D-04.");
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
  assert.match(api, /\/api\/admin\/inactive-honorees/);
  assert.match(page, /htmlFor="inactive-honoree-search"/);
  assert.match(page, /className="btn btn-outline-secondary"[^>]*>Search</);
  assert.equal(page.match(/btn btn-primary/g)?.length ?? 0, 1);
  assert.match(page, /aria-label="Select all inactive honorees on this page"/);
  assert.match(page, /aria-label=\{row\.canReactivate \? `Select \$\{row\.name\}`/);
  assert.match(page, /htmlFor=\{`free-flag-\$\{row\.honoreeId\}`\}/);
  assert.doesNotMatch(page, /—/);
  assert.doesNotMatch(helpers, /—/);
  assert.doesNotMatch(page, /lorem|placeholder/i);
});
