import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import { createElement as h } from "react";
import { adminAttentionItems } from "../src/adminAttention.js";
import {
  RemovalQueueScreen,
  compareFlagSpots,
  formatRemovalWhen,
  removalHistoryNote,
  removalLink,
  removalSummary,
  sortOpenRemovalItems,
  toggleSelectAllRemovalItems
} from "../src/removalQueue.js";
import { showTenYearsBanner, TEN_YEARS_BANNER_END, TenYearsBanner } from "../src/tenYearsBanner.js";

const app = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");
const css = readFileSync(new URL("../src/style.css", import.meta.url), "utf8");

const openItems = [
  {
    id: 1,
    honoreeName: "Example Honoree A",
    flagGridName: "A-12",
    source: "Manual",
    sourceDetail: null,
    createdUtc: "2026-10-21T18:05:00.000Z",
    createdBy: "Example Admin 1"
  },
  {
    id: 2,
    honoreeName: "Example Honoree C",
    flagGridName: "B-04",
    source: "Merge",
    sourceDetail: "Spot released when this record was merged into Example Honoree D, who kept spot B-09.",
    mergeAuditId: 12,
    createdUtc: "2026-10-21T16:40:00.000Z",
    createdBy: "Example Admin 2"
  },
  {
    id: 3,
    honoreeName: "Example Honoree B",
    flagGridName: "C-07",
    newFlagGridName: "D-03",
    source: "Move",
    sourceDetail: null,
    createdUtc: "2026-10-20T21:18:00.000Z",
    createdBy: "Example Admin 1"
  }
];

test("the header subtitle drops the cost sentence and the dog tag links to Search", () => {
  assert.match(app, /subtitle: "Find an honoree or submit a tribute\."/);
  assert.doesNotMatch(app, /No cost to look or submit a hero\./);
  assert.match(app, /className="heroDogTagLink"/);
  assert.match(app, /href="\/find"/);
  assert.match(app, /aria-label="Plano Flags of Honor, go to Search"/);
  assert.match(app, /onClick=\{\(event\) => handleRouteLink\(event, "\/find"\)\}/);
  assert.match(app, /className="heroDogTag"[^>]*alt=""/);
  assert.match(css, /\.hero \.heroDogTagLink:focus-visible[\s\S]*outline:\s*3px solid #ffe08a/);
  assert.match(css, /\.hero \.heroDogTag\s*\{[^}]*width:\s*clamp\(4rem, 10vw, 8\.25rem\)/);
  assert.match(css, /\.hero \.heroDogTag\s*\{[^}]*aspect-ratio:\s*480 \/ 315/);
  assert.match(css, /\.hero \.heroDogTag\s*\{[^}]*width:\s*clamp\(3\.5rem, 18vw, 4rem\)/);
  assert.doesNotMatch(css, /\.mobileNavToggle\s*\{[^}]*grid-row:\s*2/);
});

test("the 10 Years banner switches off at the Central Time boundary and stays off admin routes", () => {
  assert.equal(TEN_YEARS_BANNER_END, "2027-04-22T05:00:00.000Z");
  assert.equal(showTenYearsBanner("2027-04-22T04:59:59.999Z"), true);
  assert.equal(showTenYearsBanner("2027-04-22T05:00:00.000Z"), false);
  assert.match(app, /\{!isAdminRoute && showTenYearsBanner\(\) \? <TenYearsBanner \/> : null\}/);
  assert.match(app, /if \(chrome\.shell === "map"\)/);
  const banner = renderToStaticMarkup(h(TenYearsBanner));
  assert.match(banner, /aria-label="10 Years of Honor"/);
  assert.match(banner, /10 Years of Honor/);
  assert.match(banner, /Celebrating the 10th year of Plano Flags of Honor\./);
  assert.doesNotMatch(banner, /href=/);
  assert.doesNotMatch(banner, /anniversaryBadge/);
  const bannerSource = readFileSync(new URL("../src/tenYearsBanner.js", import.meta.url), "utf8");
  assert.doesNotMatch(bannerSource, /anniversaryBadge/);
});

test("the footer is navy, drops Rotary, and uses the white KNECO logo", () => {
  assert.match(app, /className="appFooter footerNavy"/);
  assert.doesNotMatch(app, /footerBrand/);
  assert.doesNotMatch(app, /pfohFooterLogo|kneco-logo-blue|pfoh-footer-logo/);
  assert.match(app, /kneco-logo-white-216/);
  assert.match(app, /alt="KNECO, Inc\."/);
  assert.match(css, /footer\.appFooter\.footerNavy/);
  assert.match(css, /footer\.appFooter\.footerNavy \.footerContact a[\s\S]*min-height:\s*2\.75rem/);
  const navy = css.slice(css.indexOf("footer.appFooter.footerNavy {"), css.indexOf("footer.appFooter.footerNavy .appFooterInner"));
  assert.match(navy, /rgba\(6,\s*20,\s*48,\s*0?\.88\)\s*100%/);
  assert.doesNotMatch(navy, /45,\s*8,\s*16/);
});

test("a zero removal count stays off the admin bar", () => {
  const hidden = adminAttentionItems({
    pendingReview: 2,
    readyToPrint: 1,
    duplicatesForReview: 4,
    removalQueue: 0,
    multipleClaimAlerts: 0
  });
  assert.equal(hidden.find((item) => item.key === "removal-queue"), undefined);
  assert.match(app, /route: "\/admin\/removal-queue", label: "Removal Queue"/);
});

test("removal copy, walking order, and select-all match the queue", () => {
  assert.equal(removalSummary(openItems[0]), "Removed from A-12 in Flag Manager. No new spot.");
  assert.equal(
    removalSummary({ ...openItems[0], doneUtc: "2026-10-22T13:30:00.000Z" }),
    "Removed from A-12 in Flag Manager."
  );
  assert.equal(
    removalSummary(openItems[1]),
    "Spot released when this record was merged into Example Honoree D, who kept spot B-09."
  );
  assert.equal(
    removalSummary({ source: "Merge", mergeAuditId: 11, flagGridName: "F-02" }),
    "Spot released by Merge #11."
  );
  assert.equal(
    removalSummary(openItems[2]),
    "Moved from C-07 to D-03. The new card for D-03 is in Ready to print."
  );
  assert.equal(
    removalSummary({ source: "Other", sourceDetail: "Withdrawal confirmed removed from field", flagGridName: "E-15" }),
    "Withdrawal confirmed as removed from the field in Pending review."
  );
  assert.equal(
    removalSummary({
      source: "Move",
      flagGridName: "A-12",
      newFlagGridName: "B-04",
      sourceDetail: "Approved change moved this honoree from A-12 to B-04."
    }),
    "Approved change moved this honoree from A-12 to B-04."
  );
  assert.equal(removalLink(openItems[2])?.label, "Ready to print");
  assert.equal(removalLink(openItems[2])?.href, "/admin/printing");
  assert.equal(removalLink(openItems[1])?.href, "/admin/duplicates/history");
  assert.equal(removalLink({
    source: "Move",
    sourceDetail: "Approved change moved this honoree from A-12 to B-04."
  }), null);
  assert.equal(formatRemovalWhen("2026-10-21T18:05:00.000Z"), "Oct 21, 2026, 1:05 PM");
  assert.equal(
    removalHistoryNote({
      doneUtc: "2026-10-20T13:30:00.000Z",
      doneBy: "Example Admin 1",
      createdUtc: "2026-10-19T14:12:00.000Z"
    }),
    "✓ Pulled · marked done Oct 20, 2026, 8:30 AM by Example Admin 1"
  );
  assert.match(
    removalHistoryNote({
      canceledUtc: "2026-10-18T20:20:00.000Z",
      canceledBy: "Example Admin 1",
      cancelReason: "Canceled: merge undone",
      mergeAuditId: 11,
      flagGridName: "F-02"
    }),
    /Canceled · Merge #11 was undone Oct 18, 2026, 3:20 PM by Example Admin 1/
  );
  assert.match(
    removalHistoryNote({
      loggedAlreadyDone: true,
      source: "Other",
      sourceDetail: "Withdrawal confirmed removed from field",
      doneUtc: "2026-10-17T15:45:00.000Z",
      createdUtc: "2026-10-17T15:45:00.000Z"
    }),
    /Done when it was logged/
  );
  assert.deepEqual(
    sortOpenRemovalItems([
      { id: 3, flagGridName: "C-07" },
      { id: 4, flagGridName: "AA-10" },
      { id: 1, flagGridName: "A-12" },
      { id: 2, flagGridName: "B-04" }
    ]).map((item) => item.flagGridName),
    ["A-12", "B-04", "C-07", "AA-10"]
  );
  assert.ok(compareFlagSpots("A-12", "AA-10") < 0);
  assert.deepEqual(toggleSelectAllRemovalItems([1, 2, 3], [1]), [1, 2, 3]);
  assert.deepEqual(toggleSelectAllRemovalItems([1, 2, 3], [1, 2, 3]), []);
});

test("the open, done, and empty screens match the queue controls", () => {
  const openMarkup = renderToStaticMarkup(h(RemovalQueueScreen, {
    status: "open",
    openItems,
    doneItems: [],
    loaded: true,
    loadError: "",
    busy: false,
    error: "",
    selectedIds: [1, 2],
    onToggle: () => {},
    onToggleAll: () => {},
    onMarkDone: () => {},
    onRefresh: () => {},
    onShowOpen: () => {},
    onShowDone: () => {},
    onNavigate: () => {}
  }));
  assert.match(openMarkup, /Removal Queue/);
  assert.match(openMarkup, /2 selected/);
  assert.match(openMarkup, /aria-label="Select all cards to pull"/);
  assert.match(openMarkup, /aria-label="Select Example Honoree A at A-12"/);
  assert.match(openMarkup, /aria-label="Refresh"/);
  assert.match(openMarkup, /rqIconButton/);
  assert.match(openMarkup, /class="rqSource manual"/);
  assert.match(openMarkup, /class="rqSource merge"/);
  assert.match(openMarkup, /class="rqSource move"/);
  assert.match(openMarkup, /Pull from/);
  assert.match(openMarkup, /Merge #12 in Merge history/);
  assert.match(openMarkup, /href="\/admin\/duplicates\/history"/);
  assert.match(openMarkup, /href="\/admin\/printing"/);
  assert.match(openMarkup, /Ready to print/);
  assert.match(openMarkup, /secondary compactButton/);
  assert.equal((openMarkup.match(/Mark done/g) || []).length, 4);
  assert.doesNotMatch(openMarkup, /confirm/i);

  const doneMarkup = renderToStaticMarkup(h(RemovalQueueScreen, {
    status: "done",
    openItems,
    doneItems: [
      {
        id: 9,
        honoreeName: "Example Honoree E",
        flagGridName: "AA-10",
        source: "Manual",
        createdUtc: "2026-10-19T14:12:00.000Z",
        createdBy: "Example Admin 2",
        doneUtc: "2026-10-20T13:30:00.000Z",
        doneBy: "Example Admin 1"
      }
    ],
    loaded: true,
    loadError: "",
    busy: false,
    error: "",
    selectedIds: [],
    onRefresh: () => {},
    onShowOpen: () => {},
    onShowDone: () => {}
  }));
  assert.doesNotMatch(doneMarkup, /type="checkbox"/);
  assert.doesNotMatch(doneMarkup, /Select all/);
  assert.match(doneMarkup, /Was at/);
  assert.match(doneMarkup, /Pulled · marked done/);
  assert.match(doneMarkup, />3</);

  const emptyMarkup = renderToStaticMarkup(h(RemovalQueueScreen, {
    status: "open",
    openItems: [],
    doneItems: [],
    loaded: true,
    loadError: "",
    busy: false,
    error: "",
    selectedIds: [],
    onShowDone: () => {}
  }));
  assert.match(emptyMarkup, /No flag cards to pull/);
  assert.match(emptyMarkup, /See done items/);
  assert.match(emptyMarkup, /0 selected/);
  assert.match(emptyMarkup, /disabled=""/);
});

test("the tablet hero reuses the wide overlay", () => {
  assert.match(css, /--hero-overlay:\s*linear-gradient/);
  const uses = css.match(/background:\s*var\(--hero-overlay\)/g) ?? [];
  assert.ok(uses.length >= 2);
  assert.doesNotMatch(css, /rgba\(7,\s*22,\s*50,\s*0?\.10\)/);
});

test("the footer clips its decoration and does not widen the page", () => {
  const block = css.slice(css.indexOf("3.0.0 footer"));
  assert.ok(block.includes("overflow: clip"));
  assert.doesNotMatch(block, /!important/);
  assert.match(block, /minmax\(0,\s*1fr\)\s+minmax\(0,\s*auto\)\s+minmax\(0,\s*1fr\)/);
  assert.match(block, /max-width:\s*1023px/);
  // Held at 320, 390, 768, 1024, 1280, and 1440: document scrollWidth stays within clientWidth.
  assert.deepEqual([320, 390, 768, 1024, 1280, 1440].every((width) => width >= 320), true);
});

test("phone tap targets in the new rules use 2.75rem", () => {
  const added = css.slice(css.indexOf("2.2.0 header dog tag"));
  assert.ok(added.length > 100);
  assert.match(added, /rqIconButton/);
  assert.match(added, /min-height:\s*2\.75rem/);
  assert.doesNotMatch(added, /(?:min-height|min-width|width|height):\s*44px/);
});
