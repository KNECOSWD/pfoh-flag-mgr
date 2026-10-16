import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
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

  const merged = source.slice(source.indexOf("function openSelectedPrintPdf"), source.indexOf("async function markSelectedPrinted"));
  assert.match(merged, /const count = selectedPrintIds\.length/);
  assert.match(merged, /printSelectionSubtitle\(count\)/);
  assert.match(merged, /heading: printCenterHeading/);
  assert.match(merged, /openCardBlob\(/);
  assert.match(merged, /fetchMergedPrintPdf/);
  assert.match(merged, /failCardPreview/);
  assert.ok(merged.indexOf("openCardBlob") < merged.indexOf("fetchMergedPrintPdf"));
  assert.doesNotMatch(merged, /revokeObjectURL/);
  assert.doesNotMatch(merged, /setError\(/);
  assert.doesNotMatch(source, /downloadMergedPrintPdf/);
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
