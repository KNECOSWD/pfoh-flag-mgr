import assert from "node:assert/strict";
import test from "node:test";
import { decideAdminLoad, isAdminAuthFailure } from "../src/adminAccess.js";

test("only 401 and 403 mean the account is not an administrator", () => {
  assert.equal(isAdminAuthFailure(401), true);
  assert.equal(isAdminAuthFailure(403), true);
  assert.equal(isAdminAuthFailure(500), false);
  assert.equal(isAdminAuthFailure(0), false);
  assert.equal(isAdminAuthFailure(200), false);
});

test("a pending-review failure keeps the administrator tabs and reports that panel", () => {
  const decision = decideAdminLoad([
    { name: "review", ok: false, status: 500, message: "Pending honoree reviews could not be loaded." },
    { name: "printing", ok: true, status: 200, message: "" },
    { name: "flagMap", ok: true, status: 200, message: "" }
  ]);

  assert.equal(decision.isAdmin, true);
  assert.equal(decision.errors.review, "Pending honoree reviews could not be loaded.");
  assert.equal(decision.errors.printing, "");
  assert.equal(decision.errors.flagMap, "");
});

test("401 or 403 hides administrator tabs instead of showing a panel error", () => {
  for (const status of [401, 403]) {
    const decision = decideAdminLoad([
      { name: "review", ok: false, status, message: "Not an administrator." },
      { name: "printing", ok: false, status, message: "Not an administrator." },
      { name: "flagMap", ok: false, status, message: "Not an administrator." }
    ]);

    assert.equal(decision.isAdmin, false, String(status));
    assert.equal(decision.errors.review, "");
    assert.equal(decision.errors.printing, "");
    assert.equal(decision.errors.flagMap, "");
  }
});

test("other panel failures stay visible even when every administrator request fails", () => {
  const decision = decideAdminLoad([
    { name: "review", ok: false, status: 500, message: "Pending honoree reviews could not be loaded." },
    { name: "printing", ok: false, status: 0, message: "The honoree print queue could not be loaded." },
    { name: "flagMap", ok: false, status: 502, message: "The flag map could not be loaded." }
  ]);

  assert.equal(decision.isAdmin, true);
  assert.equal(decision.errors.review, "Pending honoree reviews could not be loaded.");
  assert.equal(decision.errors.printing, "The honoree print queue could not be loaded.");
  assert.equal(decision.errors.flagMap, "The flag map could not be loaded.");
});
