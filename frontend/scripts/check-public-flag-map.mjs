import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import {
  FLAG_MAP_ZOOM,
  KIOSK_IDLE_MS,
  applyKioskIdleReset,
  attractView,
  createFlagMapLoader,
  createPinchBatch,
  createWheelBatch,
  cullRects,
  filterFlagMapSuggestions,
  fitView,
  fitWholeField,
  flagMapPageBands,
  initialOverviewPlacement,
  boxInsideViewport,
  flagDeepLink,
  countFlagsOnScreen,
  flagMapDetailGates,
  flagMapLayerOpacities,
  boundsOf,
  FIELD_MAP_ROWS,
  FIELD_MAP_Z_ROWS,
  layoutFlagField,
  MAP_FULLSCREEN_CLASS,
  PAVILION_COLUMNS_LEFT_TO_RIGHT,
  sealImagesAllowed,
  spotContents,
  SUBMITTER_SUGGESTION_DEBOUNCE_MS,
  SEAL_ON_SCREEN_LIMIT,
  pointerDragCapture,
  pointerIsTap,
  pinchView,
  pointerShouldOpen,
  lookupSubmittedFlags,
  lookupSubmitterSuggestions,
  resolveSubmitterFlags,
  publicFlagMapUrls,
  readFlagMapLocation,
  springEase,
  viewportWorldRect,
  wheelFrameShouldCull,
  wheelScaleFactor,
  zoomAt
} from "../src/publicFlagMap.js";
import { qrModules } from "../src/qrCode.js";
import {
  KIOSK_EXIT_HOLD_MS,
  KIOSK_GUARDED_ROUTES,
  clearKioskSession,
  kioskBackTarget,
  kioskBlocksNavigationKey,
  kioskChrome,
  kioskLinkLeavesMap,
  rememberKioskSession,
  requestKioskExit,
  routeWhileKiosk
} from "../src/kioskSession.js";

const app = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");
const program = readFileSync(new URL("../../backend/PFOH.Api/Program.cs", import.meta.url), "utf8");
const robots = readFileSync(new URL("../public/robots.txt", import.meta.url), "utf8");
const page = readFileSync(new URL("../src/PublicFlagMap.tsx", import.meta.url), "utf8");
const css = readFileSync(new URL("../src/style.css", import.meta.url), "utf8");

test("the hidden flag map route is registered and kept out of the nav", () => {
  assert.match(app, /"\/flag-map"/);
  assert.match(app, /route: "\/flag-map", label: "Flag Map", show: false/);
  assert.match(app, /siteNav\.filter\(\(item\) => item\.show\)/);
  assert.doesNotMatch(app, /footerExplore[\s\S]{0,400}flag-map/);
  assert.match(app, /name = "robots"/);
  assert.match(app, /noindex, nofollow/);
});

test("the public page does not start a sign-in redirect", () => {
  assert.doesNotMatch(page, /loginRedirect|signIn\(/);
  assert.doesNotMatch(page, /mapbox|leaflet|google\.maps/i);
  assert.match(page, /role="combobox"/);
  assert.match(page, /Preview card/);
  assert.match(page, /aria-label="Zoom in"/);
  assert.match(page, /aria-label="Reset view"/);
  assert.match(page, /gestureTransform\(/);
  assert.match(page, /onWheel|addEventListener\("wheel"/);
  assert.match(page, /touchmove/);
});

test("robots.txt and the API header keep the page out of search results", () => {
  assert.match(robots, /Disallow:\s*\/flag-map/);
  assert.match(program, /X-Robots-Tag/);
  assert.match(program, /\/api\/public\/flag-map/);
});

test("zoomed-out zones use real ranges and spots stay inside their box", () => {
  assert.equal(SUBMITTER_SUGGESTION_DEBOUNCE_MS, 150);
  assert.match(page, /SUBMITTER_SUGGESTION_DEBOUNCE_MS/);
  assert.equal(MAP_FULLSCREEN_CLASS, "isMapFullscreen");
  assert.match(page, /Exit full screen/);
  assert.match(page, /requestElementFullscreen/);
  assert.match(page, /cssFallbackRef/);
  assert.match(page, /event\.key !== "Escape" \|\| !mapFullRef\.current/);
  assert.match(css, /html\.isMapFullscreen \.hero/);
  assert.match(css, /html\.isMapFullscreen \.appFooter/);
  assert.match(css, /html\.isMapFullscreen \.publicFlagMap/);
  const chrome = css.slice(css.indexOf("html.flagMapPage .publicFlagChrome {"), css.indexOf("html.flagMapPage .publicFlagHeading"));
  assert.match(chrome, /overflow:\s*visible/);
  assert.match(chrome, /z-index:\s*30/);
  assert.match(css, /\.publicFlagChrome\s*\{[^}]*z-index:\s*30/);
  assert.match(css, /\.publicFlagSuggestions\s*\{[^}]*z-index:\s*5/);
  assert.match(css, /\.publicFlagStage\s*\{[^}]*z-index:\s*1/);
  assert.doesNotMatch(css, /\.publicFlagDot/);
  const open = spotContents({ width: 52, height: 28, status: "Open" }, { names: false, seals: false, ranks: false });
  assert.equal(open.name, "Open");
  assert.equal(open.seal, 0);
  const named = spotContents(
    { width: 52, height: 28, status: "Occupied", displayName: "Ean Howard Sullivan" },
    { names: true, seals: false, ranks: false }
  );
  assert.ok(named.name.length > 0);
  assert.ok(named.name.length * named.nameSize * 0.56 <= 52);
  assert.ok(named.nameSize + 4 <= 28);
  const sealed = spotContents(
    { width: 52, height: 28, status: "Occupied", displayName: "Ada", logoUrl: "/seal.png", rank: "Capt" },
    { names: true, seals: true, ranks: true }
  );
  assert.ok(sealed.seal >= 8 && sealed.seal <= 26);
  assert.ok(sealed.seal + (named.name ? 0 : 0) <= 28);
  assert.ok(sealed.name.length === 0 || sealed.name.length * sealed.nameSize * 0.56 <= 52);
  const hidden = spotContents(
    { width: 52, height: 28, status: "Occupied", displayName: "Ada", logoUrl: "/seal.png" },
    { names: true, seals: false, ranks: false }
  );
  assert.equal(hidden.seal, 0);
  assert.equal(hidden.name, "Ada");
});

test("search suggestions use the name and stay on the allowlist fields", () => {
  const hits = filterFlagMapSuggestions([
    { displayName: "Public Example", firstName: "Public", lastName: "Example", nickname: "", serviceBranchName: "Army", section: "A", flagGridName: "A-14" },
    { displayName: "Other Person", firstName: "Other", lastName: "Person" }
  ], "exam");
  assert.equal(hits.length, 1);
  assert.equal(hits[0].displayName, "Public Example");
  assert.equal(publicFlagMapUrls("").searchIndex, "/api/public/flag-map/search-index");
});

test("semantic zoom fades section, seal, name, and rank without a jump", () => {
  const fit = 1;
  const far = flagMapLayerOpacities(1, fit);
  assert.equal(far.section, 1);
  assert.equal(far.seal, 0);
  assert.equal(far.name, 0);
  assert.equal(far.rank, 0);

  const mid = flagMapLayerOpacities((FLAG_MAP_ZOOM.sealFadeStart + FLAG_MAP_ZOOM.sealFadeEnd) / 2, fit);
  assert.ok(Math.abs(mid.section - 0.5) < 0.001);
  assert.equal(mid.seal, 0);
  assert.equal(mid.name, 0);
  assert.equal(mid.rank, 0);

  const seals = flagMapLayerOpacities(2.05, fit);
  assert.equal(seals.section, 0);
  assert.equal(seals.seal, 0);
  assert.equal(seals.name, 0);
  assert.equal(seals.rank, 0);

  const names = flagMapLayerOpacities(2.9, fit);
  assert.equal(names.seal, 0);
  assert.equal(names.name, 1);
  assert.equal(names.rank, 0);

  const closest = flagMapLayerOpacities(FLAG_MAP_ZOOM.rankFadeEnd, fit);
  assert.equal(closest.section, 0);
  assert.equal(closest.seal, 0);
  assert.equal(closest.name, 1);
  assert.equal(closest.rank, 1);

  let previous = far;
  for (let relative = 1; relative <= 4.2; relative += 0.05) {
    const next = flagMapLayerOpacities(relative, fit);
    for (const key of ["section", "seal", "name", "rank"]) {
      assert.ok(Math.abs(next[key] - previous[key]) < 0.2, `${key} jumped at ${relative}`);
    }
    previous = next;
  }
});

test("wheel zoom keeps the point under the cursor and a drag is not a tap", () => {
  const view = { x: 20, y: 30, scale: 1 };
  const next = zoomAt(view, 120, 80, 2);
  const worldX = (120 - view.x) / view.scale;
  const worldY = (80 - view.y) / view.scale;
  assert.equal(next.x + worldX * next.scale, 120);
  assert.equal(next.y + worldY * next.scale, 80);
  assert.equal(pointerIsTap({ x: 0, y: 0 }, { x: 4, y: 4 }), true);
  assert.equal(pointerIsTap({ x: 0, y: 0 }, { x: 20, y: 0 }), false);
});

test("the field layout culls flags outside the viewport", () => {
  const field = layoutFlagField([
    {
      section: "A",
      flagCount: 2,
      occupiedCount: 1,
      seats: [
        { flagGridId: 1, flagGridName: "A-01", section: "A", status: "Open" },
        { flagGridId: 2, flagGridName: "A-02", section: "A", status: "Occupied", displayName: "Example" }
      ]
    }
  ]);
  assert.equal(field.flags.length, 2);
  assert.ok(field.flags.every((flag) => flag.x >= field.sections[0].x && flag.y >= field.sections[0].y));
  const fitted = fitView({ x: 0, y: 0, width: field.worldWidth, height: field.worldHeight }, { width: 800, height: 600 });
  const seen = viewportWorldRect(fitted, { width: 800, height: 600 });
  assert.equal(cullRects(field.flags, seen).length, 2);
  assert.equal(cullRects(field.flags, { x: field.worldWidth + 10, y: 0, width: 20, height: 20 }).length, 0);
});

test("the field is laid out as seen from the pavilion", () => {
  const field = layoutFlagField([
    {
      section: "AA",
      flagCount: 2,
      occupiedCount: 0,
      seats: [
        { flagGridId: 1, flagGridName: "AA-01", columnNumber: 1, section: "AA", status: "Open" },
        { flagGridId: 2, flagGridName: "AA-21", columnNumber: 21, section: "AA", status: "Open" }
      ]
    },
    {
      section: "Z",
      flagCount: 1,
      occupiedCount: 0,
      seats: [
        { flagGridId: 3, flagGridName: "Z-01", columnNumber: 1, section: "Z", status: "Reserved" },
        { flagGridId: 6, flagGridName: "Z-15", columnNumber: 15, section: "Z", status: "Occupied" }
      ]
    },
    {
      section: "YY",
      flagCount: 1,
      occupiedCount: 0,
      seats: [{ flagGridId: 4, flagGridName: "YY-40", columnNumber: 40, section: "YY", status: "Open" }]
    },
    {
      section: "QQ",
      flagCount: 1,
      occupiedCount: 0,
      seats: [{ flagGridId: 5, flagGridName: "QQ-01", columnNumber: 1, section: "QQ", status: "Open" }]
    }
  ]);
  assert.deepEqual(field.sections.map((section) => section.section), [...PAVILION_COLUMNS_LEFT_TO_RIGHT, "QQ"]);
  const row1 = field.flags.find((flag) => flag.flagGridId === 1);
  const row21 = field.flags.find((flag) => flag.flagGridId === 2);
  const row40 = field.flags.find((flag) => flag.flagGridId === 4);
  assert.ok(row1.y > row21.y);
  assert.ok(row40.y < row1.y);
  assert.ok(row1.y - row21.y > 20 * 31);
  const unknown = field.sections.find((section) => section.section === "QQ");
  const aa = field.sections.find((section) => section.section === "AA");
  assert.ok(unknown.x > aa.x);
  assert.equal(field.flags.length, 6);
  assert.equal(field.flags.some((flag) => flag.flagGridName === "Z-10"), false);
  assert.equal(field.cells.some((cell) => cell.flagGridName === "Z-10"), false);
  const zCells = field.cells.filter((cell) => cell.section === "Z");
  assert.equal(zCells.length, FIELD_MAP_Z_ROWS + 1);
  assert.ok(zCells.filter((cell) => cell.columnNumber <= FIELD_MAP_Z_ROWS).every((cell) => cell.status === "Reserved"));
  assert.equal(field.cells.filter((cell) => cell.section === "QQ").length, 1);
  assert.equal(field.cells.filter((cell) => cell.section === "AA").length, FIELD_MAP_ROWS);
  assert.ok(field.aisle);
  assert.ok(field.aisle.y > row21.y);
  assert.ok(field.aisle.y < row1.y);
  const z01 = field.cells.find((cell) => cell.flagGridName === "Z-01");
  const zSection = field.sections.find((section) => section.section === "Z");
  assert.equal(field.pavilion.label, "Pavilion");
  assert.equal(field.pavilion.x, zSection.x);
  assert.ok(field.pavilion.y >= z01.y + z01.height);
  assert.ok(field.rowLabels.some((label) => label.row === 1 && label.y === z01.y));
  assert.ok(field.columnLabels.some((label) => label.label === "AA" && label.x === aa.x));
  const empty = layoutFlagField([]);
  assert.equal(empty.flags.length, 0);
  assert.equal(empty.sections.length, PAVILION_COLUMNS_LEFT_TO_RIGHT.length);
  assert.equal(empty.cells.filter((cell) => cell.section === "Z" && cell.status === "Reserved").length, FIELD_MAP_Z_ROWS);
  assert.equal(empty.cells.some((cell) => cell.flagGridName === "Z-10"), false);
  assert.deepEqual(empty.zones.filter((zone) => zone.section === "C").map((zone) => zone.label), ["C 21\u201340", "C 1\u201320"]);
  assert.deepEqual(empty.zones.filter((zone) => zone.section === "Z").map((zone) => zone.label), ["Z 1\u20139"]);
  const partial = layoutFlagField([
    {
      section: "QQ",
      flagCount: 3,
      occupiedCount: 1,
      seats: [
        { flagGridId: 11, flagGridName: "QQ-03", columnNumber: 3, section: "QQ", status: "Occupied", displayName: "Ada Lovelace" },
        { flagGridId: 12, flagGridName: "QQ-08", columnNumber: 8, section: "QQ", status: "Open" },
        { flagGridId: 13, flagGridName: "QQ-22", columnNumber: 22, section: "QQ", status: "Open" },
        { flagGridId: 14, flagGridName: "QQ-30", columnNumber: 30, section: "QQ", status: "Occupied" }
      ]
    }
  ]);
  assert.deepEqual(partial.zones.filter((zone) => zone.section === "QQ").map((zone) => zone.label), ["QQ 22\u201330", "QQ 3\u20138"]);
});

test("the opening fit keeps row 1 and the pavilion inside the window", () => {
  const field = layoutFlagField([]);
  const sizes = [
    { width: 1440, height: 900 },
    { width: 1024, height: 768 },
    { width: 390, height: 844 },
    { width: 320, height: 568 },
    { width: 1920, height: 1080, kiosk: true }
  ];
  for (const size of sizes) {
    const placed = initialOverviewPlacement(field, size, { kiosk: Boolean(size.kiosk) });
    assert.equal(boxInsideViewport(placed.frame, size), true, `canvas ${size.width}`);
    assert.equal(boxInsideViewport(placed.row40, size), true, `row 40 ${size.width}`);
    assert.equal(boxInsideViewport(placed.row1, size), true, `row 1 ${size.width}`);
    assert.equal(boxInsideViewport(placed.pavilion, size), true, `pavilion ${size.width}`);
    assert.ok(placed.pavilion.top < size.height, `pavilion top ${size.width}`);
    assert.ok(placed.row1.bottom <= size.height + 0.5, `row 1 bottom ${size.width}`);
    assert.ok(placed.row40.top >= -0.5, `row 40 top ${size.width}`);
    assert.ok(placed.pavilion.bottom > placed.row1.top);
  }
  assert.match(page, /fitWholeField/);
  assert.match(page, /flagMapPageBands/);
  assert.match(page, /flagMapPage/);
  assert.match(css, /html\.flagMapPage \.publicFlagViewport/);
  assert.match(css, /--flag-map-header/);
  assert.match(css, /min-height:\s*0\s*!important/);
  const desktop = flagMapPageBands({ width: 1440, height: 900 });
  assert.ok(desktop.header > 0 && desktop.bottom > 0 && desktop.tools > 0);
  assert.equal(flagMapPageBands({ width: 1920, height: 1080 }, { kiosk: true }).header, 0);
  const fitted = fitWholeField(
    { x: 0, y: 0, width: field.worldWidth, height: field.worldHeight },
    { width: 280, height: 120 }
  );
  assert.ok(fitted.scale < FLAG_MAP_ZOOM.minScale);
  assert.ok(fitted.y >= 0);
  assert.ok(fitted.y + field.worldHeight * fitted.scale <= 120.5);
});

test("submitter suggestions share the honoree search and wave every match", () => {
  assert.equal(boundsOf([{ x: 2, y: 4, width: 10, height: 6 }, { x: 20, y: 1, width: 4, height: 3 }]).width, 22);
  assert.match(page, /lookupSubmitterSuggestions\(/);
  assert.match(page, /resolveSubmitterFlags\(/);
  assert.match(page, /void waveSubmitter\(/);
  assert.match(page, /boundsOf\(matched\)/);
  assert.match(page, /setWaveIds\(matched\.map/);
  assert.match(page, /trimmed\.length < 3/);
  assert.doesNotMatch(page, /Flags I submitted/);
  assert.doesNotMatch(page, /flag-map\/mine/);
  assert.doesNotMatch(page, /submittedFlagsEnabled/);
  assert.doesNotMatch(app, /loadMySubmittedFlags/);
  assert.doesNotMatch(app, /currentSubmitterReady/);
  assert.match(page, /publicFlagSeat/);
  assert.match(page, /isReserved/);
  assert.match(page, /publicFlagRowLabel/);
  assert.match(page, /publicFlagColumnLabel/);
  assert.match(page, /publicFlagAisle/);
  assert.match(page, /publicFlagPavilion/);
  assert.doesNotMatch(page, /submitter-first/);
  assert.match(css, /\.publicFlagSeat\.isReserved/);
  assert.match(css, /\.publicFlagPavilion/);
});

test("the flag detail photo shows the whole picture", () => {
  const frame = css.slice(css.indexOf(".publicFlagPhotoFrame {"), css.indexOf(".publicFlagQrBlock"));
  assert.match(frame, /object-fit:\s*contain/);
  assert.match(frame, /max-height:\s*min\(34vh, 280px\)/);
  assert.match(frame, /background:\s*#0b1f3a/);
  assert.doesNotMatch(frame, /object-fit:\s*cover/);
  assert.match(page, /publicFlagPhotoFrame/);
  assert.match(css, /\.publicFlagPhoto \{[^}]*object-fit:\s*contain/);
  assert.doesNotMatch(css, /\.publicFlagPhoto \{[^}]*object-fit:\s*cover/);
  assert.match(css, /\.publicFlagMap\.isKiosk \.publicFlagPhotoFrame/);
  assert.match(css, /max-height:\s*min\(32vh, 220px\)/);
});

test("branch seals render only when fewer than 10 flags are on screen", () => {
  assert.equal(SEAL_ON_SCREEN_LIMIT, 10);
  assert.equal(sealImagesAllowed(10), false);
  assert.equal(sealImagesAllowed(9), true);
  assert.equal(sealImagesAllowed(0), true);
  const wide = flagMapDetailGates(12, 1, 40);
  assert.equal(wide.seals, false);
  assert.equal(wide.names, true);
  assert.equal(wide.ranks, true);
  assert.equal(wide.flagsInteractive, true);
  const close = flagMapDetailGates(12, 1, 9);
  assert.equal(close.seals, true);
  assert.equal(close.names, true);
  assert.equal(close.flagsInteractive, true);
  const mid = flagMapDetailGates(1.4, 1, 400);
  assert.equal(mid.seals, false);
  assert.equal(mid.names, false);
  assert.equal(mid.sectionsInteractive, true);
  assert.equal(mid.flagsInteractive, false);
  assert.equal(flagMapDetailGates(12, 1, 10).seals, false);
  assert.equal(flagMapDetailGates(12, 1).seals, false);

  const flags = Array.from({ length: 12 }, (_, index) => ({
    x: index * 20,
    y: 0,
    width: 20,
    height: 20
  }));
  assert.equal(countFlagsOnScreen(flags, { x: 0, y: 0, scale: 1 }, { width: 100, height: 40 }), 5);
  assert.equal(countFlagsOnScreen(flags, { x: 0, y: 0, scale: 1 }, { width: 1000, height: 40 }), 12);

  const markerStart = page.indexOf("visibleCells.map");
  const marker = page.slice(markerStart, page.indexOf("selected ?", markerStart));
  assert.match(marker, /spot\.seal > 0 && cell\.logoUrl/);
  assert.match(marker, /spotContents/);
  assert.doesNotMatch(marker, /publicFlagDot/);
  assert.doesNotMatch(page, /branchDotColor/);
  assert.doesNotMatch(page, /publicFlagDot/);
  assert.match(marker, /publicFlagSealImg/);
  assert.match(marker, /loading="lazy"/);
  assert.match(marker, /decoding="async"/);
  assert.match(page, /countFlagsOnScreen\(layout\.flags, view, size\)/);
  assert.doesNotMatch(page, /publicFlagWave/);
  assert.match(page, /showSeal=\{gates\.seals\}/);

  const flush = page.slice(page.indexOf("const flushWheelFrame"), page.indexOf("const flushPinchFrame"));
  const beforeSettle = flush.slice(0, flush.indexOf("setTimeout"));
  assert.match(beforeSettle, /gestureTransform/);
  assert.doesNotMatch(beforeSettle, /publishCull|paintView|setGates|setVisibleFlagIds/);
  assert.match(flush.slice(flush.indexOf("setTimeout")), /publishCull\(true\)/);

  const css = readFileSync(new URL("../src/style.css", import.meta.url), "utf8");
  assert.match(css, /@keyframes publicFlagSealFade/);
  assert.match(css, /from \{ opacity: 0; \}/);
  assert.doesNotMatch(css, /publicFlagWave/);
  const load = page.slice(page.indexOf("setLoading(true);"), page.indexOf("useLayoutEffect(() => {"));
  assert.doesNotMatch(load, /summaries\.map/);
  assert.match(load, /loadField\(/);
  assert.match(page, /urls\.field/);
});

test("a click opens the target and a drag does not capture until the pointer leaves the slop", () => {
  assert.equal(pointerShouldOpen({ x: 0, y: 0 }, { x: 0, y: 0 }), true);
  assert.equal(pointerDragCapture({ x: 0, y: 0 }, { x: 3, y: 2 }, false), false);
  assert.equal(pointerShouldOpen({ x: 0, y: 0 }, { x: FLAG_MAP_ZOOM.tapSlop, y: 0 }), true);
  assert.equal(pointerDragCapture({ x: 0, y: 0 }, { x: FLAG_MAP_ZOOM.tapSlop + 1, y: 0 }, false), true);
  assert.equal(pointerShouldOpen({ x: 0, y: 0 }, { x: 24, y: 0 }), false);
  assert.equal(pointerDragCapture({ x: 0, y: 0 }, { x: 2, y: 2 }, true), true);

  const down = page.slice(page.indexOf("const onPointerDown"), page.indexOf("const onPointerMove"));
  const move = page.slice(page.indexOf("const onPointerMove"), page.indexOf("const onPointerUp"));
  assert.doesNotMatch(down, /setPointerCapture/);
  assert.match(move, /pointerDragCapture/);
  assert.match(move, /setPointerCapture/);
  assert.match(page, /releasePointerCapture/);
  assert.match(page, /pointerShouldOpen/);
});

test("wheel zoom coalesces each frame and stays under the 45fps bar on a 1,200-flag field", () => {
  const seats = Array.from({ length: 1200 }, (_, index) => ({
    flagGridId: index + 1,
    flagGridName: `A-${index + 1}`,
    section: "A",
    status: index % 5 === 0 ? "Occupied" : "Open",
    displayName: index % 5 === 0 ? `Honoree ${index}` : null
  }));
  const field = layoutFlagField([{ section: "A", flagCount: 1200, occupiedCount: 240, seats }]);
  assert.equal(field.flags.length, 1200);

  const startView = { x: 40, y: 50, scale: 1 };
  const point = { x: 400, y: 300 };
  const deltas = [-30, -12, 8, -4];
  let sequential = { ...startView };
  for (const delta of deltas) {
    sequential = zoomAt(sequential, point.x, point.y, sequential.scale * wheelScaleFactor(delta));
  }
  const batch = createWheelBatch();
  for (const delta of deltas) batch.push(delta, point);
  const flushed = batch.flush();
  const combined = zoomAt(startView, point.x, point.y, startView.scale * wheelScaleFactor(flushed.deltaY));
  assert.ok(Math.abs(combined.scale - sequential.scale) < 1e-9);
  assert.ok(Math.abs(combined.x - sequential.x) < 1e-6);
  assert.ok(Math.abs(combined.y - sequential.y) < 1e-6);
  assert.equal(batch.flush(), null);

  const frames = 40;
  const eventsPerFrame = 8;
  const viewport = { width: 1280, height: 800 };
  let view = { x: 0, y: 0, scale: 1 };
  let gates = flagMapDetailGates(view.scale, 1);
  let applies = 0;
  let culls = 0;
  const samples = [];
  const wheel = createWheelBatch();
  for (let frame = 0; frame < frames; frame += 1) {
    const started = performance.now();
    for (let event = 0; event < eventsPerFrame; event += 1) {
      wheel.push(-16, point);
    }
    const nextBatch = wheel.flush();
    assert.ok(nextBatch);
    view = zoomAt(view, nextBatch.point.x, nextBatch.point.y, view.scale * wheelScaleFactor(nextBatch.deltaY));
    applies += 1;
    const nextGates = flagMapDetailGates(view.scale, 1);
    const ended = frame === frames - 1;
    if (wheelFrameShouldCull(gates, nextGates, ended)) {
      cullRects(field.flags, viewportWorldRect(view, viewport, 180));
      culls += 1;
      gates = nextGates;
    }
    samples.push(performance.now() - started);
  }
  samples.sort((left, right) => left - right);
  const p95 = samples[Math.min(samples.length - 1, Math.ceil(samples.length * 0.95) - 1)];
  assert.equal(applies, frames);
  assert.equal(frames * eventsPerFrame, 320);
  assert.ok(culls < frames / 2, `culled on ${culls} of ${frames} frames`);
  assert.ok(culls <= 8, `expected culls only when the level changes, plus the last frame, got ${culls}`);
  assert.ok(view.scale > 1);
  assert.ok(p95 < 1000 / 45, `wheel frame p95 ${p95.toFixed(2)}ms is slower than 45fps`);

  const wheelHandler = page.slice(page.indexOf("const onWheel"), page.indexOf("const onPointerDown"));
  assert.match(wheelHandler, /requestAnimationFrame\(flushWheelFrame\)/);
  assert.match(wheelHandler, /wheelBatchRef/);
  assert.doesNotMatch(wheelHandler, /setVisibleFlagIds|setGates|setLevelText|publishCull/);
  assert.match(page, /wheelFrameShouldCull/);
});

test("pinch zoom coalesces each frame and stays under the 45fps bar on a 320-wide field", () => {
  const seats = Array.from({ length: 1200 }, (_, index) => ({
    flagGridId: index + 1,
    flagGridName: `A-${index + 1}`,
    section: "A",
    status: index % 5 === 0 ? "Occupied" : "Open",
    displayName: index % 5 === 0 ? `Honoree ${index}` : null
  }));
  const field = layoutFlagField([{ section: "A", flagCount: 1200, occupiedCount: 240, seats }]);
  const viewport = { width: 320, height: 640 };
  const origin = { x: 8, y: 12, scale: 1 };
  const startMid = { x: 160, y: 280 };
  let view = { ...origin };
  let gates = flagMapDetailGates(view.scale, 1);
  const frames = 40;
  const eventsPerFrame = 8;
  let applies = 0;
  let culls = 0;
  const samples = [];
  const batch = createPinchBatch();
  for (let frame = 0; frame < frames; frame += 1) {
    const started = performance.now();
    for (let event = 0; event < eventsPerFrame; event += 1) {
      const distance = 40 + frame * 8 + event;
      batch.push(pinchView(origin, startMid, distance, 40, { x: startMid.x + event, y: startMid.y }));
    }
    const next = batch.flush();
    assert.ok(next);
    view = next;
    applies += 1;
    const nextGates = flagMapDetailGates(view.scale, 1);
    const ended = frame === frames - 1;
    if (wheelFrameShouldCull(gates, nextGates, ended)) {
      cullRects(field.flags, viewportWorldRect(view, viewport, 180));
      culls += 1;
      gates = nextGates;
    }
    samples.push(performance.now() - started);
  }
  samples.sort((left, right) => left - right);
  const p95 = samples[Math.min(samples.length - 1, Math.ceil(samples.length * 0.95) - 1)];
  assert.equal(viewport.width, 320);
  assert.equal(field.flags.length, 1200);
  assert.equal(applies, frames);
  assert.equal(frames * eventsPerFrame, 320);
  assert.ok(culls < frames / 2, `culled on ${culls} of ${frames} frames`);
  assert.ok(culls <= 8, `expected culls only when the level changes, plus the last frame, got ${culls}`);
  assert.ok(view.scale > 1);
  assert.ok(p95 < 1000 / 45, `pinch frame p95 ${p95.toFixed(2)}ms is slower than 45fps`);
  assert.equal(batch.flush(), null);

  const move = page.slice(page.indexOf("const onTouchMove"), page.indexOf("const onTouchEnd"));
  assert.match(move, /requestAnimationFrame\(flushPinchFrame\)/);
  assert.match(move, /pinchBatchRef/);
  assert.match(move, /pinchView/);
  assert.doesNotMatch(move, /applyView|publishCull|setVisibleFlagIds|setGates|setLevelText/);
});

test("kiosk touch targets are at least 56px", () => {
  const css = readFileSync(new URL("../src/style.css", import.meta.url), "utf8");
  const exit = css.slice(css.indexOf(".kioskExitZone {"), css.indexOf(".kioskPinOverlay"));
  assert.match(exit, /width:\s*56px/);
  assert.match(exit, /height:\s*56px/);
  assert.match(exit, /min-width:\s*56px/);
  assert.match(exit, /min-height:\s*56px/);
  const targets = css.slice(css.indexOf(".publicFlagMap.isKiosk button:not(.publicFlagTile):not(.publicFlagMarker)"), css.indexOf(".publicFlagSearchTabs button.isActive"));
  assert.match(targets, /min-width:\s*56px/);
  assert.match(targets, /min-height:\s*56px/);
  assert.match(targets, /\.publicFlagSearchTabs button/);
  assert.match(targets, /\.publicFlagMap\.isKiosk input/);
  assert.match(targets, /\.publicFlagMap\.isKiosk a/);
  assert.doesNotMatch(css, /min-height:\s*44px|min-width:\s*44px|width:\s*44px|height:\s*44px/);

  const tags = page.match(/<(button|input|a|select|textarea)\b[^>]*>/g) || [];
  assert.ok(tags.length >= 8);
  for (const tag of tags) {
    const mapGlyph = /publicFlagTile|publicFlagMarker/.test(tag);
    const covered = mapGlyph || /button|input|a|select|textarea/.test(tag);
    assert.equal(covered, true);
  }
});

test("a kiosk attract loop does not request the flag map again", async () => {
  const attract = page.slice(page.indexOf("function beginAttract"), page.indexOf("function exitAttract"));
  const idle = page.slice(page.indexOf("function armIdle"), page.indexOf("const beginAttractRef"));
  assert.doesNotMatch(attract, /loadJson|fetch\(|urls\.|createFlagMapLoader/);
  assert.doesNotMatch(idle, /loadJson|fetch\(|urls\.|createFlagMapLoader/);
  const load = page.slice(page.indexOf("setLoading(true);"), page.indexOf("useLayoutEffect(() => {"));
  assert.equal((load.match(/searchIndex/g) || []).length, 1);
  assert.match(load, /\}, \[\]\);/);
  assert.doesNotMatch(load, /summaries\.map/);

  let calls = 0;
  const loader = createFlagMapLoader(async (url, init) => {
    calls += 1;
    const etag = init?.headers?.["If-None-Match"];
    if (etag) {
      return { status: 304, ok: false, headers: { get: () => etag }, json: async () => { throw new Error("304 has no body"); } };
    }
    return { status: 200, ok: true, headers: { get: () => "\"map\"" }, json: async () => ({ url }) };
  });
  const url = "/api/public/flag-map/search-index";
  const [first, parallel] = await Promise.all([loader(url), loader(url)]);
  assert.equal(calls, 1);
  assert.deepEqual(parallel, first);
  for (let frame = 0; frame < 120; frame += 1) {
    attractView({ x: 10, y: 20, scale: 0.4 }, frame * 16, { width: 1280, height: 800 }, false);
  }
  assert.equal(calls, 1);
  const again = await loader(url);
  assert.equal(calls, 2);
  assert.deepEqual(again, first);

  const cleared = applyKioskIdleReset({
    query: "Eleanor",
    open: true,
    active: 2,
    selected: { id: 1 },
    highlightId: 9,
    revealId: 9,
    history: [{}],
    searchTab: "submitted",
    submitterFirst: "Ada",
    submitterLast: "Lovelace",
    submitterNote: "No flags found",
    submitterHits: [{ displayName: "Eleanor" }],
    view: { x: 40, y: 12, scale: 3 },
    fitView: { x: 1, y: 2, scale: 0.4 }
  });
  assert.equal(cleared.query, "");
  assert.equal(cleared.open, false);
  assert.equal(cleared.selected, null);
  assert.equal(cleared.highlightId, null);
  assert.equal(cleared.revealId, null);
  assert.deepEqual(cleared.history, []);
  assert.equal(cleared.searchTab, "honoree");
  assert.equal(cleared.submitterFirst, "");
  assert.equal(cleared.submitterLast, "");
  assert.equal(cleared.submitterNote, "");
  assert.deepEqual(cleared.submitterHits, []);
  assert.equal(cleared.view.scale, 0.4);
  assert.match(attract, /setSubmitterSuggestions\(\[\]\)/);
  assert.match(attract, /setWaveIds\(\[\]\)/);
  assert.match(attract, /setQuery\(cleared\.query\)/);
  assert.match(attract, /setSelected\(cleared\.selected\)/);
  assert.ok(attract.indexOf("applyKioskIdleReset") < attract.indexOf("attractView"));
});

test("kiosk mode, deep links, and the client-side QR stay on the public map", () => {
  assert.equal(KIOSK_IDLE_MS, 60000);
  assert.equal(readFlagMapLocation("?kiosk=1").kiosk, true);
  assert.equal(readFlagMapLocation("?flag=14").flagId, 14);
  assert.equal(readFlagMapLocation("?flag=14").flagRejected, false);
  assert.equal(readFlagMapLocation("?kiosk=1&flag=14").kiosk, true);
  assert.equal(readFlagMapLocation("?flag=nope").flagId, null);
  assert.equal(readFlagMapLocation("?flag=nope").flagRejected, true);
  assert.equal(readFlagMapLocation("?flag=14.2").flagRejected, true);
  assert.equal(readFlagMapLocation("?flag=").flagRejected, false);
  assert.equal(readFlagMapLocation("?kiosk=1&flag=999999").kiosk, true);
  assert.equal(readFlagMapLocation("?kiosk=1&flag=999999").flagId, 999999);
  const link = flagDeepLink("https://flags.example/flag-map?kiosk=1", 14);
  assert.equal(link, "https://flags.example/flag-map?flag=14");
  assert.equal(flagDeepLink("https://flags.example/flag-map?kiosk=1&flag=9", 6504), "https://flags.example/flag-map?flag=6504");
  assert.doesNotMatch(link, /kiosk/);
  assert.match(page, /That flag is not on the map\./);
  assert.equal(springEase(0), 0);
  assert.equal(springEase(1), 1);
  const drifted = attractView({ x: 10, y: 20, scale: 0.4 }, 8000, { width: 1200, height: 800 }, false);
  const still = attractView({ x: 10, y: 20, scale: 0.4 }, 8000, { width: 1200, height: 800 }, true);
  assert.notEqual(drifted.x, 10);
  assert.equal(still.x, 10);
  assert.equal(still.scale, 0.4);

  assert.match(page, /aria-label="Home"/);
  assert.match(page, /aria-label="Back"/);
  assert.match(page, /requestFullscreen/);
  assert.match(page, /contextmenu/);
  assert.match(page, /user-scalable=no/);
  assert.match(page, /KIOSK_IDLE_MS/);
  assert.match(page, /prefers-reduced-motion/);
  assert.match(page, /isRevealed/);
  assert.match(css, /min-height:\s*56px/);
  assert.match(css, /prefers-reduced-motion:\s*reduce/);

  const keyframes = css.match(/@keyframes\s+publicFlag[\w]*\s*\{[\s\S]*?\n\}/g) || [];
  assert.ok(keyframes.length >= 4);
  for (const block of keyframes) {
    const declarations = block.match(/[a-z-]+\s*:/g) || [];
    for (const declaration of declarations) {
      const property = declaration.replace(":", "").trim();
      assert.ok(property === "transform" || property === "opacity", `${block.slice(0, 40)} animates ${property}`);
    }
  }

  const modules = qrModules(link);
  assert.equal(modules.length, modules[0].length);
  assert.ok(modules.length >= 21);
  assertFinder(modules, 0, 0);
  assertFinder(modules, modules.length - 7, 0);
  assertFinder(modules, 0, modules.length - 7);
  assert.deepEqual(modules, qrModules(link));
});

test("kiosk mode stays on the flag map and hides admin chrome", () => {
  const storage = memoryStorage();
  assert.equal(rememberKioskSession(storage, "?kiosk=1"), true);
  assert.equal(rememberKioskSession(storage, ""), true);
  assert.equal(rememberKioskSession(storage, "?flag=14"), true);
  for (const route of KIOSK_GUARDED_ROUTES) {
    assert.equal(routeWhileKiosk(route, true), "/flag-map");
    assert.equal(kioskBackTarget(route, true), "/flag-map");
  }
  assert.equal(routeWhileKiosk("/flag-map", true), "/flag-map");
  assert.equal(kioskBackTarget("/flag-map", true), "/flag-map");
  clearKioskSession(storage);
  assert.equal(rememberKioskSession(storage, ""), false);

  const chrome = kioskChrome(true, true);
  assert.equal(chrome.shell, "map");
  assert.equal(chrome.header, false);
  assert.equal(chrome.nav, false);
  assert.equal(chrome.auth, false);
  assert.equal(chrome.drawer, false);
  assert.equal(chrome.footer, false);
  assert.equal(chrome.adminBanner, false);
  assert.equal(chrome.adminLinks, false);
  assert.equal(chrome.loadSignedInData, false);
  assert.equal(kioskBlocksNavigationKey("ArrowLeft", true, true), true);
  assert.equal(kioskBlocksNavigationKey("Backspace", false, false), true);
  assert.equal(kioskBlocksNavigationKey("Backspace", false, true), false);
  assert.equal(kioskLinkLeavesMap("/admin/review", "https://flags.example/flag-map?kiosk=1"), true);
  assert.equal(kioskLinkLeavesMap("https://flags.example/flag-map?flag=14", "https://flags.example/flag-map"), false);
  assert.equal(KIOSK_EXIT_HOLD_MS, 3000);

  const shell = app.slice(app.indexOf('if (chrome.shell === "map")'), app.indexOf("\n  return (\n    <main>"));
  assert.match(shell, /data-kiosk-lock="1"/);
  assert.match(shell, /openPublicHonoreeCard/);
  assert.doesNotMatch(shell, /adminAlertBanner|authBox|heroNav|appFooter|\/admin\/review|loginRedirect|Sign out|Log in/);
  assert.match(app, /if \(kioskLockedRef\.current\) return/);
  assert.match(app, /Your name can be used to look up flags you submitted at the event kiosk\./);
  assert.match(page, /lookupSubmitterSuggestions/);
  assert.doesNotMatch(page, /Flags I submitted/);
  assert.doesNotMatch(page, /flag-map\/mine/);
  assert.doesNotMatch(page, /loginRedirect/);
  const setup = readFileSync(new URL("../../docs/kiosk-setup.md", import.meta.url), "utf8");
  assert.match(setup, /signed out/i);
  assert.match(setup, /PublicFlagMap:KioskExitPin/);
  assert.doesNotMatch(readFileSync(new URL("../../backend/PFOH.Api/appsettings.json", import.meta.url), "utf8"), /KioskExitPin"\s*:\s*"[^"]+"/);
});

test("the submitted-flag lookup does not send a sign-in token", async () => {
  let headers = null;
  const result = await lookupSubmittedFlags("Ada", "Lovelace", async (url, init) => {
    headers = init.headers;
    assert.equal(url, "/api/public/flag-map/by-submitter");
    assert.equal(init.method, "POST");
    assert.equal(init.body, JSON.stringify({ first: "Ada", last: "Lovelace" }));
    assert.doesNotMatch(url, /search-index|\?/);
    return { status: 200, json: async () => ({ flags: [{ honoreeId: 1, flagGridId: 2, displayName: "Visible Hero" }] }) };
  });
  assert.equal(headers.Authorization, undefined);
  assert.equal(result.body.flags[0].displayName, "Visible Hero");
  const exitStatus = await requestKioskExit("0000", async (_url, init) => {
    assert.equal(init.headers.Authorization, undefined);
    assert.equal(init.method, "POST");
    return { status: 401 };
  });
  assert.equal(exitStatus, 401);

  let suggestionHeaders = null;
  const suggestions = await lookupSubmitterSuggestions("Ada", async (url, init) => {
    suggestionHeaders = init.headers;
    assert.equal(url, "/api/public/flag-map/submitter-suggestions");
    assert.equal(init.method, "POST");
    assert.equal(init.body, JSON.stringify({ query: "Ada" }));
    return { status: 200, json: async () => ({ suggestions: [{ label: "All flags submitted by Ada Lovelace", token: "abc" }] }) };
  });
  assert.equal(suggestionHeaders.Authorization, undefined);
  assert.equal(suggestions.body.suggestions[0].label, "All flags submitted by Ada Lovelace");
  const resolved = await resolveSubmitterFlags("abc", async (url, init) => {
    assert.equal(init.headers.Authorization, undefined);
    assert.equal(url, "/api/public/flag-map/submitter-flags");
    assert.equal(init.body, JSON.stringify({ token: "abc" }));
    return { status: 200, json: async () => ({ flags: [{ flagGridId: 2 }] }) };
  });
  assert.equal(resolved.body.flags[0].flagGridId, 2);
});

function memoryStorage() {
  const data = new Map();
  return {
    getItem: (key) => (data.has(key) ? data.get(key) : null),
    setItem: (key, value) => data.set(key, String(value)),
    removeItem: (key) => data.delete(key)
  };
}

function assertFinder(modules, x0, y0) {
  for (let y = 0; y < 7; y += 1) {
    for (let x = 0; x < 7; x += 1) {
      const border = x === 0 || y === 0 || x === 6 || y === 6;
      const center = x >= 2 && x <= 4 && y >= 2 && y <= 4;
      assert.equal(modules[y0 + y][x0 + x], border || center ? 1 : 0);
    }
  }
}
