import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { reactivationTokenFromPath, signInReturnHref } from "../src/reactivationPath.js";
import { confirmReviewQueueMessage } from "../src/reactivationQueue.js";

const token = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQ";

test("a reactivation link keeps its token and returns there after sign-in", () => {
  assert.equal(token.length, 43);
  assert.equal(reactivationTokenFromPath(`/reactivate/${token}`), token);
  assert.equal(reactivationTokenFromPath(`/reactivate/${token}/`), token);
  assert.equal(reactivationTokenFromPath("/find"), null);
  assert.equal(reactivationTokenFromPath("/reactivate/short"), null);
  const href = `https://flags.example.test/reactivate/${token}`;
  assert.equal(signInReturnHref(href), href);

  const app = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");
  const page = readFileSync(new URL("../src/ReactivationPage.tsx", import.meta.url), "utf8");
  assert.match(app, /redirectStartPage: signInReturnHref\(window\.location\.href\)/);
  assert.match(app, /reactivationTokenFromPath\(window\.location\.pathname\)/);
  assert.match(page, /Request reactivation/);
  assert.doesNotMatch(page, /—/);
  const admin = readFileSync(new URL("../src/AdminReactivation.tsx", import.meta.url), "utf8");
  const queue = readFileSync(new URL("../src/reactivationQueue.js", import.meta.url), "utf8");
  assert.doesNotMatch(admin, /—/);
  assert.doesNotMatch(queue, /—/);
  assert.equal(
    confirmReviewQueueMessage([{ name: "Ada Lovelace" }, { name: "Grace Hopper" }, { name: "Alan Turing" }]),
    "Add Ada Lovelace, Grace Hopper, and Alan Turing to the review queue?"
  );
  assert.equal(admin.match(/className="btn btn-primary"/g)?.length, 2);
  assert.match(admin, /hidden=\{view !== "links"\}/);
  assert.match(admin, /hidden=\{view !== "queue"\}/);
  assert.match(admin, /Generate links/);
  assert.match(admin, /Add to review queue/);
  assert.match(admin, /aria-label="Select all visible records"/);
});
