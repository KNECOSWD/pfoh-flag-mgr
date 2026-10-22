import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const app = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");
const css = readFileSync(new URL("../src/style.css", import.meta.url), "utf8");

const ROOT_PX = 16;

function rem(value) {
  const match = String(value).match(/^([0-9.]+)rem$/);
  assert.ok(match, `${value} is a rem length`);
  return Number(match[1]) * ROOT_PX;
}

function pageBodyBlock(source, from = 0) {
  const start = source.indexOf(".pageBody {", from);
  assert.ok(start >= 0, ".pageBody rule exists");
  const end = source.indexOf("}", start);
  return { start, text: source.slice(start, end + 1) };
}

// The footer follows the content container, so this padding is the gap above
// the footer once a full page uses up the footer's auto top margin.
function contentFooterGapPx(width) {
  const base = pageBodyBlock(css);
  const basePadding = base.text.match(/padding-bottom:\s*([^;]+);/);
  assert.ok(basePadding, "phone padding-bottom is set");
  let padding = basePadding[1].trim();

  const tabletQuery = css.indexOf("@media (min-width: 761px)", base.start);
  assert.ok(tabletQuery > base.start);
  const tablet = pageBodyBlock(css, tabletQuery);
  const tabletPadding = tablet.text.match(/padding-bottom:\s*([^;]+);/);
  assert.ok(tabletPadding, "tablet padding-bottom is set");
  if (width >= 761) padding = tabletPadding[1].trim();

  return rem(padding);
}

test("the last content element keeps a non-zero gap above the footer", () => {
  assert.match(app, /<main>\s*<div className="pageBody">/);
  assert.match(app, /<\/div>\s*<footer className="appFooter footerNavy"/);
  assert.equal(app.split('className="pageBody"').length - 1, 1);

  const base = pageBodyBlock(css);
  assert.doesNotMatch(base.text, /!important/);
  const tablet = pageBodyBlock(css, css.indexOf("@media (min-width: 761px)", base.start));
  assert.doesNotMatch(tablet.text, /!important/);

  const phone = contentFooterGapPx(390);
  const tabletGap = contentFooterGapPx(768);
  const desktop = contentFooterGapPx(1024);
  const wide = contentFooterGapPx(1440);

  for (const [width, gap] of [[390, phone], [768, tabletGap], [1024, desktop], [1440, wide]]) {
    assert.ok(gap > 0, `gap at ${width}px is non-zero`);
  }
  assert.ok(phone < tabletGap, "the phone gap is smaller than the tablet gap");
  assert.equal(tabletGap, desktop);
  assert.equal(desktop, wide);

  // Flag Manager used its own bottom margin and footer top margin. Those would
  // stack on the shared gap, so they are gone.
  assert.doesNotMatch(css, /margin-bottom:\s*3\.25rem\s*!important/);
  assert.doesNotMatch(css, /margin-bottom:\s*1\.75rem\s*!important/);
  assert.doesNotMatch(css, /margin-bottom:\s*1\.5rem\s*!important/);
  assert.doesNotMatch(css, /margin-top:\s*3\.5rem\s*!important/);
  assert.doesNotMatch(css, /main:has\(\.flagPositionManager\.adminStandalonePanel\) \.appFooter\s*\{[^}]*margin-top/);
});
