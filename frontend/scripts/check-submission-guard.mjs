import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import {
  matchKindLabel,
  matchReasonSentence,
  publicDuplicateHeading,
  samePersonLabel,
  samePersonNoLabel,
  similarEntryLead,
  submissionHero,
  submissionReference,
  submitAsNewLabel
} from "../src/submissionDuplicate.js";
import { submissionTokenFromPath } from "../src/submissionPath.js";

const token = "3f1c0b2a-4d5e-4f60-8a71-9b0c1d2e3f40";

test("the similar-entry prompt asks if this is the same person", () => {
  assert.equal(similarEntryLead, "Is this the same person?");
  assert.doesNotMatch(similarEntryLead, /we found a similar entry/i);
  assert.equal(publicDuplicateHeading, "We found someone similar already on the field");
  assert.equal(samePersonLabel, "Same person");
  assert.equal(submitAsNewLabel, "Submit as new");
  assert.equal(matchKindLabel("Approved"), "Approved");
  assert.equal(matchKindLabel("Pending"), "Pending review");
  assert.equal(samePersonNoLabel(1), "No");
  assert.equal(samePersonNoLabel(2), "No, none of these");
  assert.equal(matchReasonSentence("same full name and same service branch"), "Matched on: same full name and same service branch.");
  assert.equal(submissionReference("PFOH-7K3Q9X"), "PFOH-7K3Q9X");
  assert.notEqual(submissionReference("PFOH-7K3Q9X"), token);
  assert.equal(submissionHero("found").title, "SUBMISSION RECEIVED");
  assert.equal(submissionHero("missing").title, "SUBMISSION");
  assert.match(submissionHero("missing").subtitle, /does not match a submission/);
  assert.doesNotMatch(submissionHero("missing").subtitle, /reference number/i);
  assert.equal(submissionHero("loading").title, "SUBMISSION");
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
  assert.match(app, /Idempotency-Key|idempotencyKey/);
  assert.match(app, /similarMatches && similarMatches.length > 0 \? \([\s\S]*SubmissionDuplicatePrompt[\s\S]*\) : \([\s\S]*Submit for review/);
  assert.match(app, /PendingSubmissionPage/);
  assert.match(app, /Restore/);
  assert.match(app, /Superseded submissions/);
  assert.match(app, /same account on the same day/);
  assert.match(app, /submittedCentral/);
  assert.match(app, /submitterEmail/);
  assert.match(app, /keeperChangeRequestId/);
  assert.doesNotMatch(app, /These copies were replaced/);
  assert.doesNotMatch(app, /Submit as a new entry/);
  assert.doesNotMatch(app, /Back to the form/);
  assert.doesNotMatch(app, /Update this entry/);
  assert.match(prompt, />\s*Yes\s*</);
  assert.match(prompt, /samePersonNoLabel\(matches\.length\)/);
  assert.match(readFileSync(new URL("../src/submissionDuplicate.js", import.meta.url), "utf8"), /No, none of these/);
  assert.match(prompt, /badge text-bg-primary/);
  assert.match(prompt, /matchReasonSentence/);
  assert.match(prompt, /btn btn-primary/);
  assert.match(prompt, /btn btn-outline-primary/);
  assert.doesNotMatch(prompt, /Submit as a new entry/);
  assert.match(app, /variant=\{isAdmin \|\| duplicateMode === "admin-edit" \? "admin" : "public"\}/);
  const publicCard = prompt.slice(prompt.indexOf("function PublicDuplicateCard"), prompt.indexOf("function AdminDuplicatePrompt"));
  const adminCard = prompt.slice(prompt.indexOf("function AdminDuplicatePrompt"), prompt.indexOf("export function SubmissionDuplicatePrompt"));
  assert.ok(publicCard.length > 0 && adminCard.length > 0);
  assert.match(publicCard, /publicDuplicateHeading/);
  assert.match(publicCard, /samePersonLabel/);
  assert.match(publicCard, /submitAsNewLabel/);
  assert.match(publicCard, /<h3 id=\{headingId\}/);
  assert.match(publicCard, /<strong className="d-block fw-semibold mb-2">/);
  assert.doesNotMatch(publicCard, /<h4/);
  assert.equal(publicCard.match(/<h3 /g)?.length, 1);
  assert.match(publicCard, /role="region"/);
  assert.match(publicCard, /aria-labelledby=\{headingId\}/);
  assert.match(publicCard, /tabIndex=\{-1\}/);
  assert.match(publicCard, /focus\(\)/);
  assert.match(publicCard, /role="group"/);
  assert.match(publicCard, /col-12 col-sm-6 d-grid/);
  assert.match(publicCard, /btn btn-outline-primary/);
  assert.ok(publicCard.includes("</article>"));
  assert.equal(publicCard.indexOf("</article>"), publicCard.lastIndexOf("</article>"));
  assert.ok(publicCard.indexOf("samePersonLabel") > publicCard.indexOf("</article>"));
  assert.ok(publicCard.indexOf("submitAsNewLabel") > publicCard.indexOf("</article>"));
  assert.doesNotMatch(publicCard, /matchKindLabel/);
  assert.doesNotMatch(publicCard, /badge/);
  assert.doesNotMatch(publicCard, />\s*Yes\s*</);
  assert.doesNotMatch(publicCard, /btn btn-primary/);
  assert.doesNotMatch(publicCard, /!important/);
  assert.match(adminCard, />\s*Yes\s*</);
  assert.match(adminCard, /badge text-bg-primary/);
  assert.match(adminCard, /samePersonNoLabel\(matches\.length\)/);
  assert.match(adminCard, /similarEntryLead/);
  assert.doesNotMatch(prompt, /Back to the form/);
  assert.doesNotMatch(prompt, /Update this entry/);
  assert.match(app, /!submissionToken && activeRoute === item\.route/);
  assert.match(app, /!submissionToken && activeRoute === "\/honor-a-hero"/);
  assert.match(page, /What happens next/);
  assert.match(page, /Reference/);
  assert.match(page, /Submission not found/);
  assert.match(page, /onResolved/);
  assert.match(api, /\/api\/flag-claims\/duplicate-check/);
  assert.match(api, /\/api\/submissions\//);
  assert.match(api, /Idempotency-Key/);
  assert.match(api, /matchToken/);
  for (const file of files) {
    const source = readFileSync(new URL(file, import.meta.url), "utf8");
    if (file.endsWith("App.tsx") || file.endsWith("api.ts")) {
      assert.doesNotMatch(source, /We found a similar entry/);
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
