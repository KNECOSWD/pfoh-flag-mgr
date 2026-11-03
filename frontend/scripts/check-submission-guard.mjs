import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import {
  matchKindLabel,
  proceedLabel,
  similarEntryHelp,
  similarEntryLead,
  submissionReference,
  updateChoiceHelp
} from "../src/submissionDuplicate.js";
import { submissionTokenFromPath } from "../src/submissionPath.js";

const token = "3f1c0b2a-4d5e-4f60-8a71-9b0c1d2e3f40";

test("the similar-entry prompt names the match and stays friendly", () => {
  assert.equal(similarEntryLead, "A similar honoree is already on file.");
  assert.doesNotMatch(similarEntryLead, /we found a similar entry/i);
  assert.doesNotMatch(similarEntryHelp, /we found a similar entry/i);
  assert.equal(matchKindLabel("Approved"), "Approved");
  assert.equal(matchKindLabel("Pending"), "Pending review");
  assert.match(updateChoiceHelp({ kind: "Approved", canUpdateDirectly: false, isAdmin: false }), /correction/);
  assert.match(updateChoiceHelp({ kind: "Pending", canUpdateDirectly: true, isAdmin: false }), /same submitter email/);
  assert.match(updateChoiceHelp({ kind: "Pending", canUpdateDirectly: false, isAdmin: false }), /different email/);
  assert.match(updateChoiceHelp({ kind: "Approved", canUpdateDirectly: true, isAdmin: true }), /administrator/i);
  assert.equal(proceedLabel("nominate"), "Submit as a new entry");
  assert.equal(proceedLabel("admin-edit"), "Save this record anyway");
  assert.equal(submissionReference(token), token);
});

test("the pending review page is a guid path, not a sequential id", () => {
  assert.equal(submissionTokenFromPath(`/submission/${token}`), token);
  assert.equal(submissionTokenFromPath(`/submission/${token}/`), token);
  assert.equal(submissionTokenFromPath("/submission/1"), null);
  assert.equal(submissionTokenFromPath("/submission/42"), null);
  assert.equal(submissionTokenFromPath("/find"), null);
});

test("the form, page, and queue use the guard without an em dash", () => {
  const files = [
    "../src/submissionDuplicate.js",
    "../src/submissionPath.js",
    "../src/SubmissionDuplicatePrompt.tsx",
    "../src/PendingSubmissionPage.tsx",
    "../src/App.tsx",
    "../src/api.ts"
  ];
  const app = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");
  const prompt = readFileSync(new URL("../src/SubmissionDuplicatePrompt.tsx", import.meta.url), "utf8");
  const page = readFileSync(new URL("../src/PendingSubmissionPage.tsx", import.meta.url), "utf8");
  const api = readFileSync(new URL("../src/api.ts", import.meta.url), "utf8");
  assert.match(app, /submitLockRef/);
  assert.match(app, /disabled=\{saving \|\| similarMatches !== null\}/);
  assert.match(app, /Idempotency-Key|idempotencyKey/);
  assert.match(app, /SubmissionDuplicatePrompt/);
  assert.match(app, /PendingSubmissionPage/);
  assert.match(app, /Restore/);
  assert.match(app, /Superseded submissions/);
  assert.match(prompt, /Update this entry/);
  assert.match(prompt, /badge text-bg-primary/);
  assert.match(page, /What happens next/);
  assert.match(page, /Reference/);
  assert.match(api, /\/api\/flag-claims\/duplicate-check/);
  assert.match(api, /\/api\/submissions\//);
  assert.match(api, /Idempotency-Key/);
  for (const file of files) {
    const source = readFileSync(new URL(file, import.meta.url), "utf8");
    if (file.endsWith("App.tsx") || file.endsWith("api.ts")) {
      const start = source.indexOf("similarEntryLead") >= 0 ? source : source;
      assert.doesNotMatch(start, /We found a similar entry/);
    }
    if (!file.endsWith("App.tsx") && !file.endsWith("api.ts")) {
      assert.doesNotMatch(source, /—/);
      assert.doesNotMatch(source, /We found a similar entry/);
    }
  }
  assert.doesNotMatch(prompt, /—/);
  assert.doesNotMatch(page, /—/);
  assert.doesNotMatch(readFileSync(new URL("../src/submissionDuplicate.js", import.meta.url), "utf8"), /—/);
});
