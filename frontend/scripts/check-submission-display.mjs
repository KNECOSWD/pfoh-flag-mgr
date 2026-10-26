import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import {
  dropWithdrawnSubmission,
  fieldChangeText,
  flagSpotLabel,
  submissionKindLabel,
  submissionPhotoKind
} from "../src/submissionDisplay.js";

test("my submissions prefer the pending photo over a missing public image", () => {
  assert.equal(submissionPhotoKind({
    honoreeImageUrl: null,
    latestChangeRequest: { photoFileName: "pending/66.png" }
  }), "pending");
  assert.equal(submissionPhotoKind({
    honoreeImageUrl: "https://example.test/photo.jpg",
    latestChangeRequest: { photoFileName: "photos/66.jpg" }
  }), "public");
  assert.equal(submissionPhotoKind({
    honoreeImageUrl: null,
    latestChangeRequest: { photoFileName: null }
  }), "none");
  assert.equal(submissionPhotoKind({
    latestChangeRequest: { photoFileName: "pending/../secret.jpg" }
  }), "none");
});

test("review badges and field diffs use the submission kind", () => {
  assert.equal(submissionKindLabel("New"), "New submission");
  assert.equal(submissionKindLabel(null), "New submission");
  assert.equal(submissionKindLabel("Change"), "Change");
  assert.equal(submissionKindLabel("Withdrawal"), "Removal request");
  assert.equal(fieldChangeText({ previous: "Private", proposed: "Sergeant" }), "Private → Sergeant");
  assert.equal(fieldChangeText({ previous: "  ", proposed: "" }), "(blank) → (blank)");
  assert.equal(flagSpotLabel("", null), "Not assigned");
  assert.equal(flagSpotLabel("A-01", 1), "A-01");
});

test("withdrawing a submission drops it from the visible lists", () => {
  const next = dropWithdrawnSubmission(
    [{ id: 4, honoreeName: "Ada" }, { id: 5, honoreeName: "Grace" }],
    [{ claimId: 4, changeRequestId: 15 }, { claimId: 9, changeRequestId: 20 }],
    4
  );

  assert.deepEqual(next.claims.map((claim) => claim.id), [5]);
  assert.deepEqual(next.reviews.map((item) => item.claimId), [9]);
});

test("my submissions open the honoree card for that claim", () => {
  const source = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");
  const start = source.indexOf('className="ownedFlagActions"');
  const slice = source.slice(start, start + 900);
  assert.ok(start >= 0);
  assert.match(slice, /Download \/ Print Honoree PDF/);
  assert.match(slice, /openSubmissionCard\(claim\)/);
  assert.match(source, /fetchAuthorizedBlob\(instance, account, mySubmissionCardUrl\(claim\.id\)\)/);
  assert.doesNotMatch(source, /window\.open/);
});

test("honoree records and the review queue share the Honoree Card viewer", () => {
  const source = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");
  const modalStart = source.indexOf("function renderCardPreview()");
  const modalEnd = source.indexOf('if (chrome.shell === "map")', modalStart);
  const modal = source.slice(modalStart, modalEnd);
  assert.ok(modalStart >= 0);
  assert.match(modal, /<h2 id="card-preview-title">\{cardPreview\.heading\}<\/h2>/);
  assert.match(source, /heading = honoreeCardHeading/);
  assert.match(modal, /\{cardPreview\.title\}/);
  assert.match(modal, />\s*Download\s*</);
  assert.match(modal, />\s*Print\s*</);
  assert.doesNotMatch(modal, /Administrator/);
  assert.doesNotMatch(modal, /className="eyebrow"/);

  const recordStart = source.indexOf('className="honoreeResults"');
  const recordEnd = source.indexOf('className="honoreeActionArea"', recordStart);
  const record = source.slice(recordStart, recordEnd);
  assert.match(record, /Download \/ Print Honoree PDF/);
  assert.match(record, /event\.preventDefault\(\)/);
  assert.match(record, /openPublicHonoreeCard\(/);
  assert.doesNotMatch(record, /target="_blank"/);

  const reviewStart = source.indexOf('className="secondary compactButton reviewPreviewButton"');
  const review = source.slice(reviewStart, reviewStart + 400);
  assert.match(review, /previewHonoreeCard\(item\)/);
  assert.match(review, />\s*Honoree Card\s*</);
  assert.doesNotMatch(source, /Preview card/);
});

test("the honoree card viewer fits the whole page", () => {
  const app = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");
  const css = readFileSync(new URL("../src/styles/admin-print.css", import.meta.url), "utf8");
  assert.match(app, /function honoreeCardViewUrl\(url: string\)/);
  assert.match(app, /#view=Fit&zoom=page-fit/);
  assert.match(app, /src=\{honoreeCardViewUrl\(cardPreview\.url\)\}/);
  assert.match(app, /cardPreviewLoadingMessage\(cardPreview\.heading\)/);
  const preview = readFileSync(new URL("../src/cardPreview.js", import.meta.url), "utf8");
  assert.match(preview, /Loading the honoree card/);
  assert.match(preview, /export const honoreeCardHeading = "Honoree Card"/);
  const opener = app.slice(app.indexOf("function openCardBlob"), app.indexOf("function openSubmissionCard"));
  assert.ok(opener.indexOf('phase: "loading"') >= 0 || opener.includes("beginCardPreview"));
  assert.ok(opener.indexOf("beginCardPreview") < opener.indexOf("load()"));
  assert.match(app, /className="modalOverlay appConfirmOverlay cardPreviewOverlay"/);

  const layout = css.slice(css.indexOf(".appConfirmOverlay.cardPreviewOverlay"));
  assert.ok(layout.length > 0);
  const modal = layout.slice(layout.indexOf(".appConfirmModal.cardPreviewModal"));
  assert.match(modal, /width:\s*100%/);
  assert.match(modal, /height:\s*100dvh/);
  assert.match(modal, /@media \(min-width: 761px\)[\s\S]*width:\s*94vw/);
  assert.match(modal, /@media \(min-width: 761px\)[\s\S]*height:\s*94vh/);
  assert.doesNotMatch(modal, /!important/);
});

test("the review list shows a badge, field diff, and card preview", () => {
  const source = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");
  assert.match(source, /submissionKindLabel\(item\.submissionKind\)/);
  assert.match(source, /Removed from field/);
  assert.match(source, /item\.submissionKind === "Withdrawal"/);
  assert.match(source, /fieldChangeText\(change\)/);
  assert.match(source, /adminCardPreviewUrl\(item\.changeRequestId\)/);
  assert.match(source, /dropWithdrawnSubmission\(myClaims, pendingReviews, claim\.id\)/);
  assert.match(source, /submissionPhotoKind\(claim\) === "pending"/);
});
