import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { PDFDocument } from "pdf-lib";
import {
  mergePrintBatch,
  preparePrintBatch,
  printBatchFailureSummary,
  printBatchProgressText,
  printCardLabel,
  printFailureStepLabel
} from "../src/printBatch.js";
import {
  cardPreviewLoadingMessage,
  honoreeCardHeading,
  printCenterHeading,
  printSelectionSubtitle
} from "../src/cardPreview.js";

test("one selected card and twenty-eight selected cards title the shared viewer", () => {
  assert.equal(printCenterHeading, "Print Center");
  assert.equal(honoreeCardHeading, "Honoree Card");
  assert.equal(printSelectionSubtitle(1), "1 card selected");
  assert.equal(printSelectionSubtitle(28), "28 cards selected");
  assert.equal(cardPreviewLoadingMessage(printCenterHeading), "Loading the print PDF…");
  assert.equal(cardPreviewLoadingMessage(honoreeCardHeading), "Loading the honoree card…");
});

test("Print Center row and merged download open the Honoree Card viewer", () => {
  const source = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");
  assert.equal(source.split("cardPreviewOverlay").length - 1, 1);

  const printStart = source.indexOf('aria-label="Printing"');
  const printEnd = source.indexOf("isMyFlagsRoute", printStart);
  const print = source.slice(printStart, printEnd);
  assert.ok(printStart >= 0 && printEnd > printStart);
  assert.match(print, /Download merged PDF/);
  assert.match(print, /onClick=\{openSelectedPrintPdf\}/);
  assert.match(print, /Open PDF/);
  assert.match(print, /openPublicHonoreeCard\(item\.honoreeId, item\.honoreeName\)/);
  assert.match(print, /event\.preventDefault\(\)/);
  assert.doesNotMatch(print, /target="_blank"/);
  assert.doesNotMatch(print, /downloadMergedPrintPdf/);
  assert.doesNotMatch(print, /printCenterHeading/);

  const row = source.slice(source.indexOf("async function openPublicHonoreeCard"), source.indexOf("function previewHonoreeCard"));
  assert.ok(row.indexOf("beginCardPreview") < row.indexOf("await fetch"));
  assert.doesNotMatch(row, /printCenterHeading/);

  const merged = source.slice(source.indexOf("async function openSelectedPrintPdf"), source.indexOf("async function markSelectedPrinted"));
  assert.match(merged, /const count = selectedPrintIds\.length/);
  assert.match(merged, /printSelectionSubtitle\(count\)/);
  assert.match(merged, /beginCardPreview\(title, printCenterHeading, downloadName\)/);
  assert.match(merged, /failCardPreview/);
  assert.match(merged, /planPrintBatch/);
  assert.match(merged, /preparePrintBatch\(/);
  assert.match(merged, /fetchQueuedCardPdf/);
  assert.match(merged, /regenerateHonoreePdf\(instance, account, card\.honoreeId, signal\)/);
  assert.match(merged, /mergePrintBatch\(/);
  assert.match(merged, /finishCardPreview\(requestId, doneTitle, url, true, printCenterHeading, downloadName\)/);
  assert.ok(merged.indexOf("beginCardPreview(title, printCenterHeading") < merged.indexOf("planPrintBatch"));
  assert.ok(merged.indexOf("preparePrintBatch(") < merged.indexOf("mergePrintBatch("));
  assert.doesNotMatch(merged, /fetchMergedPrintPdf|openCardBlob|setTimeout/);
  assert.doesNotMatch(merged, /setError\(\s*reportable/);
  assert.doesNotMatch(source, /downloadMergedPrintPdf|fetchMergedPrintPdf/);

  const close = source.slice(source.indexOf("function closeCardPreview"), source.indexOf("function beginCardPreview"));
  assert.match(close, /printBatchAbort\.current\?\.abort\(\)/);
});

test("the per-card PDF request carries no timeout, so a large batch is never aborted by a timer", () => {
  const api = readFileSync(new URL("../src/api.ts", import.meta.url), "utf8");
  const card = api.slice(api.indexOf("async function fetchQueuedCardPdf"), api.indexOf("async function publicRequest"));
  assert.ok(card.length > 0);
  assert.doesNotMatch(card, /requestTimeoutSignal|setTimeout|AbortController/);
  assert.match(card, /signal\s*\n?\s*\}\);/);
  assert.match(card, /code === "pdf-missing"/);
  assert.match(card, /signal\?\.aborted \|\| !isRetryableNetworkError/);

  const regenerate = api.slice(api.indexOf("regenerateHonoreePdf:"), api.indexOf("removeFromReprintQueue:"));
  assert.match(regenerate, /signal\?: AbortSignal/);
  assert.match(regenerate, /method: "POST",\s*signal/);

  const blob = api.slice(api.indexOf("export async function fetchAuthorizedBlob"), api.indexOf("async function fetchQueuedCardPdf"));
  assert.match(blob, /return await response\.blob\(\);\s*\} catch \(error\) \{\s*throw buildNetworkError/);
});

test("viewer blob urls are revoked on close and unmount, not on download", () => {
  const source = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");
  const download = source.slice(source.indexOf("function downloadCardPreview"), source.indexOf("function printCardPreview"));
  const close = source.slice(source.indexOf("function closeCardPreview"), source.indexOf("function beginCardPreview"));
  const unmount = source.slice(source.indexOf("useEffect(() => {\n    return () => {\n      const current = cardPreviewRef.current;"), source.indexOf("const profileLoadGate"));
  assert.doesNotMatch(download, /revokeObjectURL/);
  assert.match(close, /URL\.revokeObjectURL\(current\.url\)/);
  assert.match(unmount, /URL\.revokeObjectURL\(current\.url\)/);
  assert.match(source, /cardPreviewRef\.current = cardPreview/);
});

async function syntheticCardPdf(label) {
  const doc = await PDFDocument.create();
  const page = doc.addPage([294, 656]);
  page.drawText(label, { x: 20, y: 600, size: 12 });
  return doc.save();
}

function syntheticBatch(total) {
  return Array.from({ length: total }, (_, index) => ({
    changeRequestId: 5000 + index,
    honoreeId: 9000 + index,
    honoreeName: `Synthetic Honoree ${String(index + 1).padStart(3, "0")}`
  }));
}

test("140 cards: missing PDFs are generated first, progress counts 1/140 to 140/140, and the merge has every card", async () => {
  const cards = syntheticBatch(140);
  const template = await syntheticCardPdf("card");
  const storage = new Map();
  cards.forEach((card, index) => {
    if (index % 7 < 2) storage.set(card.honoreeId, template);
  });
  const missingBefore = cards.filter((card) => !storage.has(card.honoreeId)).length;
  assert.equal(missingBefore, 100);

  const generatedIds = [];
  const progress = [];
  const prepared = await preparePrintBatch({
    cards,
    loadStoredPdf: async (card) => {
      await new Promise((resolve) => setImmediate(resolve));
      return storage.get(card.honoreeId) ?? null;
    },
    generatePdf: async (card) => {
      generatedIds.push(card.honoreeId);
      storage.set(card.honoreeId, template);
    },
    onProgress: (entry) => progress.push(entry)
  });

  assert.deepEqual(prepared.failures, []);
  assert.equal(prepared.generated, 100);
  assert.equal(generatedIds.length, 100);
  assert.equal(new Set(generatedIds).size, 100);
  assert.deepEqual(progress.map((entry) => entry.done), Array.from({ length: 141 }, (_, index) => index));
  assert.ok(progress.every((entry) => entry.total === 140 && entry.phase === "preparing"));
  assert.equal(printBatchProgressText(progress[37]), "Preparing card 37/140");

  const merging = [];
  const merged = await mergePrintBatch(cards, prepared.pdfs, { onProgress: (entry) => merging.push(entry), generated: prepared.generated });
  assert.deepEqual(merged.failures, []);
  assert.deepEqual(merging.map((entry) => entry.done), Array.from({ length: 140 }, (_, index) => index + 1));
  assert.equal(printBatchProgressText(merging.at(-1)), "Merging card 140/140");
  const output = await PDFDocument.load(merged.bytes);
  assert.equal(output.getPageCount(), 140);
});

test("a forced failure names the card and the step, does not skip it, and no merged PDF is built", async () => {
  const cards = syntheticBatch(140);
  const template = await syntheticCardPdf("card");
  const storage = new Map();
  const broken = cards[57];
  const prepared = await preparePrintBatch({
    cards,
    loadStoredPdf: async (card) => storage.get(card.honoreeId) ?? null,
    generatePdf: async (card) => {
      if (card.honoreeId === broken.honoreeId) throw new Error("HTTP 404: Honoree was not found. Reference: PFOH-TEST.");
      storage.set(card.honoreeId, template);
    }
  });

  assert.equal(prepared.failures.length, 1);
  const [failure] = prepared.failures;
  assert.equal(failure.card, broken);
  assert.equal(failure.step, "generate");
  assert.equal(printCardLabel(failure.card), "Synthetic Honoree 058 (honoree #9057)");
  assert.equal(printFailureStepLabel(failure), "Generating the missing card PDF failed");
  assert.match(failure.message, /Honoree was not found/);
  assert.equal(prepared.pdfs.filter(Boolean).length, 139);
  assert.equal(
    printBatchFailureSummary(prepared.failures, 140),
    "1 of 140 cards could not be prepared, so the merged PDF was not built. Fix or remove this card and try again."
  );
});

test("an unreadable card PDF fails the merge step by name instead of being dropped", async () => {
  const cards = syntheticBatch(3);
  const good = await syntheticCardPdf("card");
  const merged = await mergePrintBatch(cards, [good, new TextEncoder().encode("not a pdf"), good]);
  assert.equal(merged.bytes, undefined);
  assert.equal(merged.failures.length, 1);
  assert.equal(merged.failures[0].card, cards[1]);
  assert.equal(merged.failures[0].step, "merge");
});

test("Close cancels the batch without reporting card failures", async () => {
  const cards = syntheticBatch(20);
  const controller = new AbortController();
  let calls = 0;
  await assert.rejects(
    preparePrintBatch({
      cards,
      signal: controller.signal,
      loadStoredPdf: async () => {
        calls += 1;
        if (calls === 5) controller.abort();
        if (controller.signal.aborted) throw new Error("aborted");
        return new Uint8Array([1]);
      },
      generatePdf: async () => {}
    }),
    { name: "AbortError" }
  );
  assert.ok(calls < 20);
});
