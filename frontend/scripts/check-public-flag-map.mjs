import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import {
  FLAG_MAP_ZOOM,
  FIT_PADDING,
  KIOSK_IDLE_MS,
  SPOT_SIZE,
  blockEdgePx,
  SPOT_TEXT_PX,
  clampView,
  closestViewScale,
  labelFontSize,
  spotLinesThatFit,
  spotsViewScale,
  zoneLabelSizes,
  initialMapFullScreen,
  flagMapEscapeTarget,
  pinnedAxisLabels,
  axisStripFrame,
  AXIS_MIN_FONT_PX,
  gutterInsets,
  fieldFitBounds,
  AXIS_STRIP,
  AXIS_LABEL_PAD,
  sectionLabelSizes,
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
  flagMapItemsInView,
  flagMapItemsSummary,
  flagMapLayerOpacities,
  PAVILION_SIZE,
  boundsOf,
  FIELD_MAP_ROWS,
  FIELD_MAP_Z_ROWS,
  SERVICE_FLAGS_LABEL,
  isServiceFlagSeat,
  serviceFlagsLabelBox,
  serviceFlagsLabelSize,
  SERVICE_FLAGS_SHORT_LABEL,
  NARROW_BLOCK_PX,
  snapEdgeSpots,
  spotFitMaxScale,
  spotScreenWidth,
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
  zoomAt,
  compareInViewItems
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

test("the public nav links Flag Map right after Search, and the admin nav item reads Flag Manager", () => {
  assert.match(app, /"\/flag-map"/);
  assert.match(
    app,
    /\{ route: "\/find", label: "Search", show: true \},\s*\{ route: "\/flag-map", label: "Flag Map", show: true \},\s*\{ route: "\/my-flags"/
  );
  assert.equal(app.split("siteNav.filter((item) => item.show)").length - 1, 2);
  assert.match(
    app,
    /const adminNav: \{ route: AppRoute; label: string \}\[\] = \[\s*\{ route: "\/admin\/review", label: "Review" \},\s*\{ route: "\/admin\/printing", label: "Print Center" \},\s*\{ route: "\/admin\/flag-map", label: "Flag Manager" \}\s*\];/
  );
  assert.equal(app.split("<AdminNavMenu ").length - 1, 2);
  assert.match(app, /menuId="admin-nav-desktop" layout="bar"/);
  assert.match(app, /menuId="admin-nav-drawer" layout="drawer"/);
  for (const navLabel of ['aria-label="Main navigation"', 'aria-label="Mobile navigation"']) {
    const start = app.indexOf(navLabel);
    const nav = app.slice(start, app.indexOf("</nav>", start));
    assert.doesNotMatch(nav, /href="\/admin\//);
    assert.match(nav, /<AdminNavMenu /);
    assert.ok(nav.indexOf('href="/honor-a-hero"') < nav.indexOf("PlanoFlagsOfHonor.com"));
    assert.ok(nav.lastIndexOf("PlanoFlagsOfHonor.com") < nav.indexOf("<AdminNavMenu "));
  }
  assert.equal(app.split('className="drawerExternalLink"').length - 1, 1);
  const drawerStart = app.indexOf('aria-label="Mobile navigation"');
  const drawerNav = app.slice(drawerStart, app.indexOf("</nav>", drawerStart));
  assert.match(drawerNav, /className="drawerExternalLink"/);
  assert.match(css, /\.mobileDrawerNav a\.drawerExternalLink[\s\S]{0,280}overflow-wrap:\s*anywhere/);
  assert.match(css, /\.mobileDrawerNav a\.drawerExternalLink[\s\S]{0,280}min-width:\s*0/);
  const menu = readFileSync(new URL("../src/AdminNavMenu.tsx", import.meta.url), "utf8");
  assert.match(menu, /aria-haspopup="menu"/);
  assert.match(menu, /role="menu"/);
  assert.match(menu, /role="menuitem"/);
  assert.match(menu, /ArrowDown/);
  assert.match(menu, /ArrowUp/);
  assert.match(menu, /Escape/);
  assert.match(menu, /href=\{item\.route\}/);
  assert.doesNotMatch(app, />Flag Map<\/a>/);
  assert.doesNotMatch(app, /footerExplore[\s\S]{0,400}flag-map/);
  assert.match(app, /aria-label="Flag Manager">\s*<div className="sectionHeader">\s*<div>\s*<p className="eyebrow">Flag grids<\/p>\s*<h3>Flag Manager<\/h3>/);
  assert.doesNotMatch(app, /<h3>Flag Map<\/h3>/);
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

test("the /flag-map page is indexable, and only its API keeps the noindex header", () => {
  assert.doesNotMatch(robots, /flag-map/);
  assert.match(robots, /^User-agent: \*\nDisallow:\n$/);
  assert.doesNotMatch(app, /name = "robots"|noindex/);
  const header = program.slice(program.indexOf("X-Robots-Tag") - 200, program.indexOf("X-Robots-Tag"));
  assert.match(header, /path\.StartsWith\("\/api\/public\/flag-map", StringComparison\.OrdinalIgnoreCase\)\)/);
  assert.doesNotMatch(header, /path\.Equals\("\/flag-map"/);
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
  const spot = { width: SPOT_SIZE.width, height: SPOT_SIZE.height, flagGridName: "AA-11" };
  const far = spotContents({ ...spot, status: "Occupied", displayName: "Ada" }, flagMapDetailGates(0.1), 0.1);
  assert.equal(far.number, "");
  assert.deepEqual(far.lines, []);
  const spotsScale = 64 / SPOT_SIZE.width;
  const numbered = spotContents({ ...spot, status: "Occupied", displayName: "Ada" }, flagMapDetailGates(spotsScale), spotsScale);
  assert.equal(numbered.number, "AA-11");
  assert.deepEqual(numbered.lines, []);
  assert.equal(numbered.seal, 0);
  const openSpot = spotContents({ ...spot, status: "Open" }, flagMapDetailGates(spotsScale), spotsScale);
  assert.equal(openSpot.number, "AA-11");
  const namesScale = 130 / SPOT_SIZE.width;
  const named = spotContents(
    { ...spot, status: "Occupied", displayName: "Ean Howard Sullivan", logoUrl: "/seal.png" },
    flagMapDetailGates(namesScale, 40),
    namesScale
  );
  assert.deepEqual(named.lines, ["Ean Howard", "Sullivan"]);
  assert.equal(named.seal, 0);
  const closeScale = 300 / SPOT_SIZE.width;
  const sealed = spotContents(
    { ...spot, status: "Occupied", displayName: "Ada", logoUrl: "/seal.png", rank: "Capt" },
    flagMapDetailGates(closeScale, 6),
    closeScale
  );
  assert.ok(sealed.seal * closeScale >= SPOT_TEXT_PX.seal.min && sealed.seal * closeScale <= SPOT_TEXT_PX.seal.max);
  assert.deepEqual(sealed.lines, ["Ada"]);
  assert.equal(sealed.rank, "Capt");
  const hidden = spotContents(
    { ...spot, status: "Occupied", displayName: "Ada", logoUrl: "/seal.png" },
    flagMapDetailGates(closeScale, 10),
    closeScale
  );
  assert.equal(hidden.seal, 0);
  assert.deepEqual(hidden.lines, ["Ada"]);
});

test("spot text keeps a readable screen size and stays inside the spot at every zoom", () => {
  const names = ["Ada", "Ean Howard Sullivan", "Margaret Anne Okonkwo-Brightwater", "Guadalupe Washington-Montgomery Jr.", "Li"];
  for (let spotPx = 8; spotPx <= SPOT_SIZE.width * FLAG_MAP_ZOOM.maxScale; spotPx *= 1.07) {
    const scale = spotPx / SPOT_SIZE.width;
    for (const flagsOnScreen of [4, 40]) {
      const gates = flagMapDetailGates(scale, flagsOnScreen);
      for (const [index, displayName] of names.entries()) {
        const cell = { width: SPOT_SIZE.width, height: SPOT_SIZE.height, flagGridName: index % 2 ? "AA-11" : "C-40", status: "Occupied", displayName, rank: "Lieutenant Commander", logoUrl: "/seal.png" };
        const spot = spotContents(cell, gates, scale);
        const width = SPOT_SIZE.width * scale;
        const height = SPOT_SIZE.height * scale;
        const inner = width - spot.pad * scale * 2;
        let used = spot.pad * scale * 2 + spot.seal * scale * 1.08;
        if (spot.number) {
          const px = spot.numberSize * scale;
          assert.ok(px >= SPOT_TEXT_PX.number.min - 0.01 && px <= SPOT_TEXT_PX.number.max + 0.01, `number ${px}px at ${spotPx}`);
          assert.ok(spot.number.length * px * 0.62 <= inner + 0.01);
          used += px * 1.15;
        }
        if (spot.lines.length) {
          const px = spot.nameSize * scale;
          assert.ok(px >= SPOT_TEXT_PX.name.min - 0.01 && px <= SPOT_TEXT_PX.closestName.max + 0.01, `name ${px}px at ${spotPx}`);
          assert.ok(spot.lines.length <= 2);
          for (const line of spot.lines) assert.ok(line.length * px * 0.62 <= inner + 0.01, `${line} at ${spotPx}`);
          used += spot.lines.length * px * 1.15;
        }
        if (spot.rank) used += spot.rankSize * scale * 1.15;
        assert.ok(used <= height + 0.01, `${displayName} uses ${used} of ${height} at ${spotPx}`);
        if (!gates.names) assert.deepEqual(spot.lines, []);
        if (!gates.seals) assert.equal(spot.seal, 0);
      }
    }
  }
  assert.deepEqual(spotLinesThatFit("Sam Okonkwo-Brightwater", 120, 10, 2), ["Sam Okonkwo-", "Brightwater"]);
  assert.equal(labelFontSize("40", 160, 108, 0.01), 0);
  assert.ok(labelFontSize("40", 160, 108, 0.12) * 0.12 <= 13);
  const zone = { section: "C", min: 21, max: 40, width: SPOT_SIZE.width, height: 2388 };
  const sizes = zoneLabelSizes(zone, 0.12);
  assert.equal(sizes.range, "21\u201340");
  assert.ok(sizes.letter > 0 && sizes.rangeSize > 0);
  assert.deepEqual(sizes.rangeParts, ["21", "\u2013", "40"]);
});

test("spots never overlap and the global button size cannot stretch them", () => {
  const field = layoutFlagField([]);
  const cells = [...field.cells].sort((left, right) => left.x - right.x || left.y - right.y);
  for (let index = 0; index < cells.length; index += 1) {
    const a = cells[index];
    assert.equal(a.width, SPOT_SIZE.width);
    assert.equal(a.height, SPOT_SIZE.height);
    for (let next = index + 1; next < cells.length && cells[next].x < a.x + a.width + SPOT_SIZE.gap; next += 1) {
      const b = cells[next];
      const apartX = b.x >= a.x + a.width + SPOT_SIZE.gap - 0.001 || a.x >= b.x + b.width + SPOT_SIZE.gap - 0.001;
      const apartY = b.y >= a.y + a.height + SPOT_SIZE.gap - 0.001 || a.y >= b.y + b.height + SPOT_SIZE.gap - 0.001;
      assert.ok(apartX || apartY, `${a.flagGridName} touches ${b.flagGridName}`);
    }
  }
  const glyph = css.slice(css.indexOf(".publicFlagTile,\n.publicFlagMarker {"), css.indexOf(".publicFlagTile,\n.publicFlagZone {"));
  assert.match(glyph, /min-height:\s*0/);
  assert.match(glyph, /min-width:\s*0/);
  assert.match(glyph, /padding:\s*0/);
  assert.match(glyph, /box-sizing:\s*border-box/);
  assert.doesNotMatch(css, /\.publicFlagMarker\.isHighlighted\s*\{[^}]*box-shadow:\s*0 0 0/);
  const reveal = css.slice(css.indexOf("@keyframes publicFlagReveal"), css.indexOf("@keyframes publicFlagGlow"));
  for (const amount of reveal.match(/scale\(([\d.]+)\)/g) || []) {
    assert.ok(Number(amount.slice(6, -1)) <= 1, `reveal grows a spot to ${amount}`);
  }
});

test("the fit shows the whole grid inside the axis gutters and a pan reaches every edge", () => {
  const field = layoutFlagField([]);
  const world = fieldFitBounds(field);
  const everything = boundsOf([...field.cells, field.pavilion]);
  const inset = gutterInsets(FIT_PADDING);
  assert.deepEqual(world, everything, "the fit leaves out the row-label columns and letter band");
  for (const viewport of [{ width: 1408, height: 640 }, { width: 366, height: 560 }, { width: 1876, height: 860 }]) {
    const fitted = fitWholeField(world, viewport, FIT_PADDING);
    const left = fitted.x + everything.x * fitted.scale;
    const right = fitted.x + (everything.x + everything.width) * fitted.scale;
    const top = fitted.y + everything.y * fitted.scale;
    const bottom = fitted.y + (everything.y + everything.height) * fitted.scale;
    assert.ok(left >= inset.left - 0.01 && right <= viewport.width - inset.right + 0.01, `fit width ${viewport.width}`);
    assert.ok(top >= inset.top - 0.01 && bottom <= viewport.height - inset.bottom + 0.01, `fit height ${viewport.width}`);
    assert.deepEqual(clampView(fitted, viewport, world, { minScale: fitted.scale }), fitted);
    const under = clampView({ ...fitted, scale: fitted.scale / 2 }, viewport, world, { minScale: fitted.scale });
    assert.equal(under.scale, fitted.scale);
    for (const scale of [fitted.scale * 3, spotsViewScale(), closestViewScale(viewport), FLAG_MAP_ZOOM.maxScale]) {
      const farLeft = clampView({ x: 1e7, y: 1e7, scale }, viewport, world, { minScale: fitted.scale });
      assert.ok(farLeft.x + everything.x * scale >= inset.left - 0.01, `left edge at ${scale}`);
      assert.ok(farLeft.y + everything.y * scale >= inset.top - 0.01, `top edge at ${scale}`);
      if (world.width * scale > viewport.width - inset.left - inset.right) assert.ok(Math.abs(farLeft.x + world.x * scale - inset.left) < 0.01);
      if (world.height * scale > viewport.height - inset.top - inset.bottom) assert.ok(Math.abs(farLeft.y + world.y * scale - inset.top) < 0.01);
      const farRight = clampView({ x: -1e7, y: -1e7, scale }, viewport, world, { minScale: fitted.scale });
      assert.ok(farRight.x + (everything.x + everything.width) * scale <= viewport.width - inset.right + 0.01, `right edge at ${scale}`);
      assert.ok(farRight.y + (everything.y + everything.height) * scale <= viewport.height - inset.bottom + 0.01, `bottom edge at ${scale}`);
    }
  }
  assert.match(page, /limitView\(zoomAt\(/);
  assert.match(page, /paintView\(limitView\(next\)\)/);
  assert.match(page, /const target = limitView\(requested\)/);
});

test("section blocks are flat, Z1–Z10 is one flat Service Flags block, and Z sits against N with the aisle before M", () => {
  const zoneBlock = css.slice(css.indexOf(".publicFlagTile,\n.publicFlagZone {"), css.indexOf(".publicFlagZone strong"));
  assert.match(zoneBlock, /background:\s*#[0-9a-f]{6};/i);
  assert.doesNotMatch(zoneBlock, /gradient/);
  const serviceBlock = css.slice(css.indexOf(".publicFlagServiceFlags {"), css.indexOf(".publicFlagServiceFlags > span {"));
  assert.match(serviceBlock, /background:\s*#[0-9a-f]{6};/i);
  assert.doesNotMatch(serviceBlock, /gradient/);
  assert.match(css, /\.publicFlagSeat\.isReserved \{\s*background: transparent;\s*border-color: transparent;/);
  assert.match(css, /\.publicFlagMarker\.isReserved \{\s*background: transparent;\s*color: transparent;\s*border-color: transparent;/);
  assert.doesNotMatch(page, /<li><i className="isReserved"/);
  assert.match(page, /cell\.status !== "Reserved"/);
  assert.match(page, /"publicFlagServiceFlags isNarrow" : "publicFlagServiceFlags"/);
  assert.match(page, /!cell\.serviceFlag && ids\.has/);

  assert.equal(FIELD_MAP_Z_ROWS, 10);
  assert.equal(SERVICE_FLAGS_LABEL, "Service Flags");
  assert.equal(isServiceFlagSeat({ section: "Z", columnNumber: 1 }), true);
  assert.equal(isServiceFlagSeat({ section: "Z", columnNumber: 10 }), true);
  assert.equal(isServiceFlagSeat({ section: "Z", columnNumber: 11 }), false);
  assert.equal(isServiceFlagSeat({ section: "M", columnNumber: 5 }), false);

  const field = layoutFlagField([
    {
      section: "Z",
      flagCount: 3,
      occupiedCount: 1,
      seats: [
        { flagGridId: 901, flagGridName: "Z-10", columnNumber: 10, section: "Z", status: "Reserved" },
        { flagGridId: 902, flagGridName: "Z-11", columnNumber: 11, section: "Z", status: "Occupied", displayName: "Ada Lovelace" },
        { flagGridId: 903, flagGridName: "Z-12", columnNumber: 12, section: "Z", status: "Open" }
      ]
    }
  ]);
  assert.deepEqual(field.zones.filter((zone) => zone.section === "Z").map((zone) => zone.label), ["Z 11\u201312"]);
  assert.equal(field.flags.some((flag) => flag.flagGridName === "Z-10"), false);
  assert.ok(field.flags.some((flag) => flag.flagGridName === "Z-11"));
  const service = field.cells.filter((cell) => cell.serviceFlag);
  assert.equal(service.length, FIELD_MAP_Z_ROWS);
  assert.deepEqual(service.map((cell) => cell.columnNumber).sort((a, b) => a - b), [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
  assert.ok(service.every((cell) => cell.section === "Z"));
  assert.equal(field.cells.filter((cell) => cell.section !== "Z" && cell.serviceFlag).length, 0);
  assert.equal(field.cells.find((cell) => cell.flagGridName === "Z-11").serviceFlag, false);

  const box = field.serviceFlags;
  assert.equal(box.label, "Service Flags");
  assert.deepEqual({ x: box.x, y: box.y, width: box.width, height: box.height }, boundsOf(service));
  const z11 = field.cells.find((cell) => cell.flagGridName === "Z-11");
  assert.ok(box.y >= z11.y + z11.height, "Service Flags sits below Z11");
  const zZone = field.zones.find((zone) => zone.section === "Z");
  assert.ok(zZone.y + zZone.height <= box.y, "Z section block does not overlap Service Flags");

  for (const scale of [0.03, 0.08, 0.2, 0.6, 1.5, 4]) {
    const label = serviceFlagsLabelSize(box, scale);
    if (!label.size) continue;
    const px = label.size * scale;
    assert.ok(px > 0 && px <= 20 && (label.narrow || px >= 6), `service flags label ${px}px at ${scale}`);
    const words = box.label.split(" ");
    if (label.vertical) {
      assert.ok(box.label.length * px * 0.62 <= box.height * scale);
      assert.ok(px <= box.width * scale);
    } else {
      assert.ok(Math.max(...words.map((word) => word.length)) * px * 0.62 <= box.width * scale);
      assert.ok(words.length * px * 1.15 <= box.height * scale);
    }
  }
  assert.ok(serviceFlagsLabelSize(box, 0.08).size > 0, "label shows when fully zoomed out");
  assert.equal(serviceFlagsLabelSize(box, 1.5).vertical, false);

  const whole = serviceFlagsLabelBox(box, null);
  assert.deepEqual(whole, { x: 0, y: 0, width: box.width, height: box.height, label: "Service Flags" });
  const seen = { x: box.x - 500, y: box.y + box.height - 300, width: 2000, height: 1000 };
  const part = serviceFlagsLabelBox(box, seen);
  assert.deepEqual(part, { x: 0, y: box.height - 300, width: box.width, height: 300, label: "Service Flags" });
  assert.ok(serviceFlagsLabelSize(part, 1.5).size > 0, "label follows the part of the block on screen");
  assert.equal(serviceFlagsLabelBox(box, { x: box.x, y: box.y + box.height + 10, width: 100, height: 100 }), null);
  assert.match(page, /serviceFlagsLabelBox\(layout\.serviceFlags, viewportWorldRect\(view, size, 0\)\)/);

  const x = Object.fromEntries(field.sections.map((section) => [section.section, section]));
  const gapBefore = x.Z.x - (x.N.x + x.N.width);
  const gapAfter = x.M.x - (x.Z.x + x.Z.width);
  const usual = x.L.x - (x.M.x + x.M.width);
  assert.equal(gapBefore, usual, "no aisle between N and Z");
  assert.equal(x.N.x - (x.O.x + x.O.width), usual);
  assert.ok(gapAfter > usual * 3, `Z to M gap ${gapAfter} vs ${usual}`);
  assert.equal(usual, SPOT_SIZE.gap);
  const aisles = PAVILION_COLUMNS_LEFT_TO_RIGHT.slice(1).filter((section, index) => x[section].x - (x[PAVILION_COLUMNS_LEFT_TO_RIGHT[index]].x + SPOT_SIZE.width) !== usual);
  assert.deepEqual(aisles, ["M"], "the only column aisle is Z to M");
});

test("row numbers run down both sides, the pavilion is four times wider and twice as tall, and the key sits under the field", () => {
  const field = layoutFlagField([]);
  const left = field.rowLabels.filter((label) => label.side === "left");
  const right = field.rowLabels.filter((label) => label.side === "right");
  assert.equal(left.length, FIELD_MAP_ROWS);
  assert.equal(right.length, FIELD_MAP_ROWS);
  const contentRight = Math.max(...field.sections.map((section) => section.x + section.width));
  const gridLeft = Math.min(...field.sections.map((section) => section.x));
  for (const label of right) {
    const twin = left.find((other) => other.row === label.row);
    assert.equal(label.y, twin.y, `row ${label.row} lines up`);
    assert.equal(label.height, twin.height);
    assert.equal(label.width, twin.width);
    assert.equal(label.x - contentRight, gridLeft - (twin.x + twin.width), "same gap from the grid on both sides");
    assert.ok(label.x + label.width < field.worldWidth);
  }
  assert.equal(PAVILION_SIZE.width, (SPOT_SIZE.width * 3 + SPOT_SIZE.gap * 2) * 4);
  assert.equal(PAVILION_SIZE.height, 84 * 2);
  assert.equal(field.pavilion.width, PAVILION_SIZE.width);
  assert.equal(field.pavilion.height, PAVILION_SIZE.height);
  assert.ok(field.pavilion.x > 0 && field.pavilion.x + field.pavilion.width < field.worldWidth);
  assert.ok(field.pavilion.y + field.pavilion.height < field.worldHeight);
  assert.match(page, /label\.side === "right" \? "publicFlagRowLabel isRight"/);
  assert.match(css, /\.publicFlagRowLabel\.isRight \{\s*justify-content: flex-start;/);

  const stage = page.slice(page.indexOf('<div className="publicFlagStage">'), page.indexOf("{selected ? createPortal("));
  assert.match(stage, /<ul className="publicFlagLegend" aria-label="Spot key">/);
  const toolbar = page.slice(page.indexOf('<div className="publicFlagToolbar">'), page.indexOf('<div className="publicFlagStage">'));
  assert.doesNotMatch(toolbar, /publicFlagLegend/);
  assert.ok(stage.indexOf("publicFlagLegend") > stage.indexOf('className="publicFlagViewport"'), "the key follows the field");
  assert.match(css, /\.publicFlagLegend \{[^}]*align-self: flex-end;[^}]*height: var\(--flag-map-legend, 20px\);/);
  for (const size of [{ width: 1440, height: 900 }, { width: 390, height: 844 }, { width: 320, height: 568 }]) {
    assert.ok(flagMapPageBands(size).legend > 0);
  }
  assert.ok(flagMapPageBands({ width: 1920, height: 1080 }, { kiosk: true }).legend >= 30);
});

test("the map controls are one icon toolbar and the in-view list follows the zoom", () => {
  const toolbar = page.slice(page.indexOf('className="publicFlagControls" role="toolbar"'), page.indexOf('<div className="publicFlagJump">'));
  assert.match(toolbar, /aria-label="Map controls"/);
  for (const name of ["Zoom out", "Zoom in", "Reset view"]) {
    assert.match(toolbar, new RegExp(`aria-label="${name}" data-tooltip="${name}"`));
  }
  assert.match(toolbar, /aria-label=\{mapFull \? "Exit full screen" : "Full screen"\}/);
  assert.match(toolbar, /data-tooltip=\{mapFull \? "Exit full screen" : "Full screen"\}/);
  assert.equal((toolbar.match(/<ToolIcon /g) || []).length, 4);
  assert.doesNotMatch(toolbar, />\s*(−|\+|Full screen|Reset view)\s*</, "no text labels inside the icon buttons");
  assert.match(page, /<svg viewBox="0 0 24 24"[^>]*aria-hidden="true" focusable="false"/);
  assert.match(css, /\.publicFlagIconButton::after \{[^}]*content: attr\(data-tooltip\);/);
  assert.match(css, /\.publicFlagIconButton:focus-visible::after/);
  assert.doesNotMatch(page, /className="publicFlagLevel" aria-live/);
  assert.match(page, /<label htmlFor=\{jumpId\} className="srOnly">/);
  assert.match(page, /event\.target instanceof HTMLSelectElement\) return;/);

  const field = pavilionField(40);
  const viewport = { width: 1406, height: 618 };
  const fitted = fitWholeField({ x: 0, y: 0, width: field.worldWidth, height: field.worldHeight }, viewport, FIT_PADDING);
  const out = flagMapItemsInView(field, fitted, viewport, flagMapDetailGates(fitted.scale, 1000));
  assert.equal(out.kind, "sections");
  assert.equal(out.items.filter((item) => item.kind === "zone").length, 56);
  assert.equal(out.items.filter((item) => item.kind === "service").length, 1);
  assert.equal(flagMapItemsSummary(out), "Sections (57)");
  const zone = field.zones.find((item) => item.label === "C 21\u201340");
  const scale = 64 / SPOT_SIZE.width;
  const near = { scale, x: viewport.width / 2 - (zone.x + zone.width / 2) * scale, y: viewport.height / 2 - (zone.y + zone.height / 2) * scale };
  const spots = flagMapItemsInView(field, near, viewport, flagMapDetailGates(scale, 300));
  assert.equal(spots.kind, "spots");
  assert.ok(spots.items.length > 0 && spots.items.every((item) => item.kind === "spot" || item.kind === "service"));
  assert.ok(spots.items.some((item) => item.label.startsWith("C-")));
  const seen = viewportWorldRect(near, viewport, 0);
  for (const item of spots.items) {
    const box = item.box;
    assert.ok(box.x < seen.x + seen.width && box.x + box.width > seen.x && box.y < seen.y + seen.height && box.y + box.height > seen.y, `${item.label} is in view`);
  }
  assert.equal(spots.items.some((item) => /Z-0?[1-9]\b|Z-10\b/.test(item.label)), false, "Service Flags spots are not listed one by one");
  assert.match(flagMapItemsSummary(spots), /^Spots \(\d+\)$/);
});

function fieldWithZ(rows) {
  const seats = [];
  for (let row = 1; row <= rows; row += 1) {
    seats.push({ flagGridId: 9000 + row, flagGridName: `Z-${row}`, columnNumber: row, section: "Z", status: row <= FIELD_MAP_Z_ROWS ? "Reserved" : row % 2 ? "Occupied" : "Open", displayName: "Ada Sample" });
  }
  return layoutFlagField([{ section: "Z", flagCount: seats.length, occupiedCount: 3, seats }]);
}

test("at a 320 fit every section shows its letter and Service Flags shows a label inside the block", () => {
  // Live: 40 rows, Z1–Z10 is Service Flags, and Z splits only at the aisle.
  const field = fieldWithZ(40);
  assert.equal(field.zones.length, 56);
  assert.deepEqual(field.zones.filter((zone) => zone.section === "Z").map((zone) => zone.label).sort(), ["Z 11–20", "Z 21–40"]);
  const em = { W: 1.04, M: 0.94 };
  // 6.6–9.9px columns cover a 320 window with page chrome (the live fit is
  // 7.1–7.2px); 2.0.3 drew no range, no YY/AA letter, and no Service Flags
  // label there.
  for (const columnPx of [6.6, 7, 7.09, 7.17, 7.2, 8, 8.7, 9.9]) {
    const scale = columnPx / SPOT_SIZE.width;
    let ranges = 0;
    const uniform = sectionLabelSizes(field.zones, scale);
    for (const zone of field.zones) {
      const sizes = zoneLabelSizes(zone, scale, uniform);
      const width = zone.width * scale;
      const height = zone.height * scale;
      assert.equal(sizes.narrow, width < NARROW_BLOCK_PX);
      assert.ok(sizes.letter > 0, `${zone.label} has no letter at ${columnPx}px`);
      const letterPx = sizes.letter * scale;
      assert.ok(letterPx >= 5, `${zone.label} letter ${letterPx}px`);
      const chars = [...zone.section];
      const glyph = (char) => Math.max(em[char] ?? 0.78, 0.78);
      const inner = width - 1;
      const letterWide = sizes.letterStacked ? Math.max(0.8, ...chars.map(glyph)) * letterPx : chars.reduce((sum, char) => sum + glyph(char), 0) * letterPx;
      assert.ok(letterWide <= inner + 0.01, `${zone.label} letter ${letterWide} wide in ${inner}`);
      assert.equal(sizes.letterStacked, chars.length > 1, `${zone.label} stacked at ${columnPx}px`);
      let tall = letterPx * (sizes.letterStacked ? chars.length * 1.25 : 1.25);
      if (sizes.rangeSize) {
        ranges += 1;
        const rangePx = sizes.rangeSize * scale;
        if (sizes.vertical) {
          assert.ok(rangePx >= 7 && rangePx <= width * 0.8 + 0.01, `${zone.label} rotated range ${rangePx}px`);
          tall += sizes.range.length * rangePx * 0.62 + rangePx * 0.4;
        } else {
          assert.equal(sizes.stacked, true);
          assert.ok(rangePx >= 6 && rangePx <= 9, `${zone.label} range ${rangePx}px`);
          assert.ok(rangePx * 0.8 <= inner + 0.01);
          tall += rangePx * sizes.range.length * 1.25 + letterPx * 0.3;
        }
      }
      assert.ok(tall <= height, `${zone.label} label ${tall} tall in ${height}`);
    }
    // Below ~7px columns every block falls back to its letter alone, together.
    assert.equal(ranges, columnPx >= 7 ? 56 : 0, `${ranges} ranges at ${columnPx}px`);

    const label = serviceFlagsLabelSize(serviceFlagsLabelBox(field.serviceFlags, null), scale, uniform);
    const px = label.size * scale;
    assert.ok(px >= 5, `Service Flags label ${px}px at ${columnPx}px`);
    assert.ok([SERVICE_FLAGS_LABEL, SERVICE_FLAGS_SHORT_LABEL].includes(label.text));
    assert.equal(label.vertical, true);
    assert.ok(px <= field.serviceFlags.width * scale - 1 + 0.01, "rotated line fits inside the stroke");
    assert.ok(label.text.length * px * 0.62 <= field.serviceFlags.height * scale * 0.84 + 0.01);
  }
  // A taller field fits at under 5px columns; the letter still draws edge to
  // edge and Service Flags still has a label.
  for (const rows of [40, 60]) {
    const tall = pavilionField(rows);
    const fits = [fitWholeField({ x: 0, y: 0, width: tall.worldWidth, height: tall.worldHeight }, { width: 284, height: 268 }, FIT_PADDING).scale];
    if (rows === 60) assert.ok(fits[0] * SPOT_SIZE.width < 5, "the 60-row fit is under 5px a column");
    for (const scale of [...fits, 3 / SPOT_SIZE.width, 4 / SPOT_SIZE.width, 4.87 / SPOT_SIZE.width, 5.5 / SPOT_SIZE.width, 7.2 / SPOT_SIZE.width]) {
      assert.equal(tall.zones.length, 56);
      const uniform = sectionLabelSizes(tall.zones, scale);
      for (const zone of tall.zones) {
        const sizes = zoneLabelSizes(zone, scale, uniform);
        const width = zone.width * scale;
        const letterPx = sizes.letter * scale;
        const where = `${rows} rows ${zone.label} at ${width.toFixed(2)}px`;
        assert.ok(letterPx > 0, `${where} paints nothing`);
        const chars = [...zone.section];
        const glyph = (char) => Math.max(em[char] ?? 0.78, 0.78);
        const wide = sizes.letterStacked ? Math.max(0.8, ...chars.map(glyph)) * letterPx : chars.reduce((sum, char) => sum + glyph(char), 0) * letterPx;
        assert.ok(wide <= width + 0.01, `${where} letter ${wide} wide`);
        if (letterPx < 5) assert.equal(sizes.tiny, true, where);
        let used = letterPx * 1.25 * (sizes.letterStacked ? chars.length : 1);
        if (sizes.rangeSize) used += sizes.rangeSize * scale * sizes.range.length * 1.25;
        assert.ok(used <= zone.height * scale, `${where} label too tall`);
      }
      const label = serviceFlagsLabelSize(tall.serviceFlags, scale, uniform);
      assert.ok(label.size > 0 && [SERVICE_FLAGS_LABEL, SERVICE_FLAGS_SHORT_LABEL].includes(label.text), `${rows} rows Service Flags at ${scale}`);
      assert.ok(label.size * scale <= tall.serviceFlags.width * scale + 0.01);
      assert.ok(label.text.length * label.size * scale * 0.62 <= tall.serviceFlags.height * scale * 0.84 + 0.01);
    }
  }
  assert.match(css, /\.publicFlagZone\.isTiny \{\s*border-width: 0;\s*box-shadow: inset/);
  const fit320 = initialOverviewPlacement(field, { width: 320, height: 568 });
  const padded320 = fitWholeField({ x: 0, y: 0, width: field.worldWidth, height: field.worldHeight }, { width: 284, height: 268 }, FIT_PADDING).scale;
  for (const scale of [fit320.view.scale, padded320]) {
    const zones320 = field.zones.map((zone) => [zone.label, zoneLabelSizes(zone, scale, sectionLabelSizes(field.zones, scale))]);
    assert.ok(zones320.every(([, sizes]) => sizes.letter > 0), `every zone shows its letter at ${scale}`);
    const withRange = zones320.filter(([, sizes]) => sizes.rangeSize > 0).length;
    assert.equal(withRange, SPOT_SIZE.width * scale >= 7 ? zones320.length : 0, `${withRange} ranges at ${(SPOT_SIZE.width * scale).toFixed(2)}px columns`);
  }
  assert.ok(serviceFlagsLabelSize(field.serviceFlags, fit320.view.scale).size > 0);

  for (const scale of [0.12, 0.29, 0.39]) {
    const uniform = sectionLabelSizes(field.zones, scale);
    for (const zone of field.zones) {
      const sizes = zoneLabelSizes(zone, scale, uniform);
      assert.equal(sizes.narrow, false);
      assert.equal(sizes.stacked, false);
      assert.equal(sizes.letterStacked, false, `${zone.label} letter on one line at ${scale}`);
      assert.ok(sizes.rangeSize > 0, `${zone.label} range at ${scale}`);
      assert.ok(sizes.letter * scale * 0.78 * zone.section.length <= zone.width * scale - 3.2 + 0.01, `${zone.label} letter wider than its tile`);
    }
    assert.equal(serviceFlagsLabelSize(field.serviceFlags, scale, uniform).text, SERVICE_FLAGS_LABEL);
  }
  assert.match(page, /sizes\.narrow \? "isNarrow" : ""/);
  assert.match(page, /sizes\.tiny \? "isTiny" : ""/);
  assert.match(page, /sizes\.letterStacked \? "isUpright"/);
  assert.doesNotMatch(page, /sizes\.stacked \? "isUpright"/);
  assert.match(page, /label\.narrow \? "publicFlagServiceFlags isNarrow"/);
  assert.match(page, /\{label\.text\}/);
  assert.match(css, /\.publicFlagZone\.isNarrow \{\s*border-width: calc\(var\(--px, 1px\) \* 0\.5\);/);
  assert.match(css, /\.publicFlagZone \.isUpright \{[^}]*text-orientation: upright;[^}]*line-height: 0\.8;/);
});

function pavilionField(rows) {
  return layoutFlagField(PAVILION_COLUMNS_LEFT_TO_RIGHT.map((section) => ({
    section,
    seats: Array.from({ length: rows }, (_, index) => ({
      flagGridId: pavilionSeatId(section, index + 1),
      flagGridName: `${section}-${index + 1}`,
      columnNumber: index + 1,
      section,
      status: section === "Z" && index < FIELD_MAP_Z_ROWS ? "Reserved" : index % 3 ? "Open" : "Occupied"
    }))
  })));
}

test("every section label shares one size, weight, and top placement at a zoom, sized for the widest name", () => {
  const em = { W: 1.04, M: 0.94 };
  const glyph = (char) => Math.max(em[char] ?? 0.78, 0.78);
  // 40 rows is live; 60 rows fits the kiosk at about 14.6px columns.
  for (const field of [fieldWithZ(40), fieldWithZ(60)]) for (const columnPx of [3, 4.87, 5.5, 6.6, 7.2, 8.8, 9.8, 12, 14.1, 14.59, 15, 17.8, 21.4, 30, 42, 60, 120]) {
    const scale = columnPx / SPOT_SIZE.width;
    const uniform = sectionLabelSizes(field.zones, scale);
    const all = field.zones.map((zone) => zoneLabelSizes(zone, scale, uniform));
    const one = (key) => new Set(all.map((sizes) => sizes[key].toFixed(6))).size === 1;
    assert.ok(one("letter") && one("rangeSize") && one("top"), `one letter size, range size, and top offset at ${columnPx}px`);
    assert.equal(new Set(all.map((sizes) => `${sizes.stacked}|${Boolean(sizes.rangeParts)}`)).size, 1, `one range layout at ${columnPx}px`);
    const letterPx = uniform.letterPx;
    const inner = columnPx - (columnPx < NARROW_BLOCK_PX ? 1 : 3) - 0.2;
    if (uniform.letters === "across") {
      assert.ok(Math.max(...field.zones.map((zone) => [...zone.section].reduce((sum, char) => sum + glyph(char), 0))) * letterPx <= inner + 0.01, `widest name fits at ${columnPx}px`);
      assert.ok(all.every((sizes) => !sizes.letterStacked));
    } else {
      assert.ok(Math.max(...field.zones.flatMap((zone) => [...zone.section].map(glyph))) * letterPx <= (uniform.tiny ? columnPx : inner) + 0.01, `widest glyph fits at ${columnPx}px`);
    }
    if (columnPx >= 7) assert.ok(uniform.rangePx >= 6, `range drawn at ${columnPx}px`);
    if (columnPx < 5.5) assert.equal(uniform.rangePx, 0, `letter only at ${columnPx}px`);
    const service = serviceFlagsLabelSize(field.serviceFlags, scale, uniform);
    assert.ok(service.size > 0, `Service Flags label at ${columnPx}px`);
    const sharedPx = uniform.rangePx > 0 ? uniform.rangePx : Math.min(uniform.letterPx, 20);
    assert.ok(Math.abs(service.size * scale - sharedPx) < 1e-6, `Service Flags ${service.size * scale}px matches the ${sharedPx}px section label at ${columnPx}px`);
    assert.ok(Math.abs(service.top * scale - (uniform.topPx + (uniform.tiny ? 0 : blockEdgePx(columnPx)))) < 1e-6, `Service Flags top lines up with the letters at ${columnPx}px`);
  }
  assert.match(css, /\.publicFlagZone \{\s*justify-content: flex-start;/);
  assert.match(css, /\.publicFlagZone strong \{[^}]*font-weight: 800;/);
  assert.match(css, /\.publicFlagServiceFlags \{[^}]*font-weight: 650;/);
  assert.match(css, /\.publicFlagServiceFlags > span \{[^}]*justify-content: flex-start;/);
  assert.match(page, /sectionLabelSizes\(field\.zones, renderScale\)/);
  assert.match(page, /zoneLabelSizes\(zone, renderScale, sectionLabels\)/);
  assert.match(page, /serviceFlagsLabelSize\(area, renderScale, sectionLabels\)/);
});

test("a view that settles from the names zoom to the closest keeps every mostly visible spot inside the gutters with padding", () => {
  const level = (scale) => (spotScreenWidth(scale) >= FLAG_MAP_ZOOM.closestAt ? "Closest" : "Names");
  const cut = (field, view, viewport) => field.cells.filter((cell) => cell.serviceFlag || cell.status !== "Reserved").filter((cell) => {
    const left = view.x + cell.x * view.scale;
    const top = view.y + cell.y * view.scale;
    const right = left + cell.width * view.scale;
    const bottom = top + cell.height * view.scale;
    const inset = gutterInsets(FIT_PADDING);
    const seen = Math.max(0, Math.min(right, viewport.width - AXIS_STRIP.row) - Math.max(left, AXIS_STRIP.row)) * Math.max(0, Math.min(bottom, viewport.height - AXIS_STRIP.column) - Math.max(top, 0));
    if (seen <= 0.5 * (right - left) * (bottom - top) + 1e-6) return false;
    return left < inset.left - 0.01 || top < inset.top - 0.01 || right > viewport.width - inset.right + 0.01 || bottom > viewport.height - inset.bottom + 0.01;
  });
  // Padding boxes of the 390 and 320 phone maps, a short phone, 1440, and the kiosk.
  const viewports = [{ width: 346, height: 567 }, { width: 284, height: 268 }, { width: 296, height: 300 }, { width: 1406, height: 638 }, { width: 1874, height: 858 }];
  let views = 0;
  for (const rows of [40, 60]) {
    const field = pavilionField(rows);
    const world = fieldFitBounds(field);
    const anchors = ["Z-13", "N-31", "YY-1", `AA-${rows}`, "C-21"].map((name) => field.cells.find((cell) => cell.flagGridName === name));
    for (const viewport of viewports) {
      const fitted = fitWholeField(world, viewport, FIT_PADDING);
      const maxScale = Math.max(fitted.scale, spotFitMaxScale(viewport, FIT_PADDING));
      if (viewport.width < 760) {
        assert.ok(SPOT_SIZE.width * maxScale + gutterInsets(FIT_PADDING).left * 2 <= viewport.width + 0.01 || spotScreenWidth(maxScale) <= FLAG_MAP_ZOOM.closestAt + 1.01);
        assert.ok(spotScreenWidth(maxScale) > FLAG_MAP_ZOOM.closestAt, "the closest level stays reachable on a phone");
      } else {
        assert.equal(maxScale, FLAG_MAP_ZOOM.maxScale, "1440 and the kiosk keep the full zoom");
      }
      const options = { minScale: fitted.scale, maxScale, world };
      for (let spotPx = FLAG_MAP_ZOOM.namesAt; spotPx <= SPOT_SIZE.width * maxScale + 0.01; spotPx *= 1.09) {
        const scale = spotPx / SPOT_SIZE.width;
        for (const anchor of anchors) {
          for (const [nx, ny] of [[0, 0], [0.37, -0.29], [-0.45, 0.45], [0.51, 0.13], [-0.18, -0.52]]) {
            const before = clampView({
              scale,
              x: viewport.width / 2 - (anchor.x + anchor.width * (0.5 + nx)) * scale,
              y: viewport.height / 2 - (anchor.y + anchor.height * (0.5 + ny)) * scale
            }, viewport, world, options);
            const settled = clampView(snapEdgeSpots(before, viewport, field.cells, FIT_PADDING, options), viewport, world, options);
            views += 1;
            const where = `${rows} rows ${viewport.width}x${viewport.height} ${spotPx.toFixed(1)}px ${anchor.flagGridName} ${nx},${ny}`;
            assert.deepEqual(cut(field, settled, viewport).map((cell) => cell.flagGridName), [], where);
            assert.ok(Math.abs(settled.scale / before.scale - 1) <= 0.0601, `scale ${where}`);
            assert.equal(level(settled.scale), level(before.scale), `level ${where}`);
            const again = snapEdgeSpots(settled, viewport, field.cells, FIT_PADDING, options);
            assert.ok(Math.abs(again.x - settled.x) < 0.01 && Math.abs(again.y - settled.y) < 0.01 && again.scale === settled.scale, `stable ${where}`);
          }
        }
      }
    }
  }
  assert.ok(views > 1000, `${views} views`);
  const field = pavilionField(40);
  const phone = { width: 346, height: 567 };
  const spots = { scale: (FLAG_MAP_ZOOM.namesAt - 1) / SPOT_SIZE.width, x: -1400, y: -600 };
  assert.deepEqual(snapEdgeSpots(spots, phone, field.cells, FIT_PADDING), spots, "below the names zoom nothing moves");

  assert.match(page, /animateTo\(settledView\(zoomAt\(viewRef\.current, point\.x, point\.y/);
  assert.match(page, /wheelSettleRef\.current = window\.setTimeout\(settleGesture, WHEEL_SETTLE_MS\)/);
  assert.match(page, /pinchSettleRef\.current = window\.setTimeout\(settleGesture, WHEEL_SETTLE_MS\)/);
  assert.match(page, /clearTimeout\(pinchSettleRef\.current\);\s*settleGesture\(\);/);
  assert.match(page, /animateTo\(settledView\(zoneView\.scale >= spotsViewScale\(\)/);
  assert.match(page, /node\.clientWidth, height: node\.clientHeight/);
  assert.match(page, /maxScale: maxViewScale\(\)/);
  const open = page.slice(page.indexOf("function openFlag("), page.indexOf("function drawerCover("));
  assert.doesNotMatch(open, /settledView/);
  const drag = page.slice(page.indexOf("const onPointerMove = "), page.indexOf("const onPointerUp = "));
  assert.doesNotMatch(drag, /settle/);
  const keys = page.slice(page.indexOf("panView(viewRef.current, FLAG_MAP_ZOOM.panStep, 0)") - 80, page.indexOf("panView(viewRef.current, 0, -FLAG_MAP_ZOOM.panStep)") + 80);
  assert.doesNotMatch(keys, /settle/);
});

function pavilionSeatId(section, row) {
  return (PAVILION_COLUMNS_LEFT_TO_RIGHT.indexOf(section) + 1) * 100 + row;
}

test("the detail drawer is portaled to body and layers above every page layer", () => {
  const stage = page.slice(page.indexOf('<div className="publicFlagStage">'), page.indexOf("{selected ? createPortal("));
  assert.doesNotMatch(stage, /<FlagDetail/);
  assert.match(page, /import \{ createPortal \} from "react-dom";/);
  const portal = page.slice(page.indexOf("{selected ? createPortal("), page.indexOf("{kiosk ? createPortal("));
  assert.match(portal, /<div className=\{kiosk \? "publicFlagDetailLayer isKiosk" : "publicFlagDetailLayer"\}>/);
  assert.match(portal, /<div className="publicFlagDetailBackdrop" aria-hidden="true" \/>/);
  assert.match(portal, /<FlagDetail/);
  assert.match(portal, /document\.body\s*\)/);
  assert.doesNotMatch(portal, /publicFlagDetailBackdrop"[^>]*onClick/);
  const kioskPortal = page.slice(page.indexOf("{kiosk ? createPortal("), page.indexOf("</section>", page.indexOf("{kiosk ? createPortal(")));
  assert.match(kioskPortal, /kioskExitZone/);
  assert.match(kioskPortal, /kioskPinOverlay/);
  assert.match(kioskPortal, /document\.body\) : null\}/);
  assert.match(page, /selected \? "hasDetail" : ""/);

  const zOf = (selector) => {
    const block = css.slice(css.indexOf(`${selector} {`), css.indexOf("}", css.indexOf(`${selector} {`)));
    return Number(block.match(/z-index:\s*(\d+)/)[1]);
  };
  const layer = css.slice(css.indexOf(".publicFlagDetailLayer {"), css.indexOf("}", css.indexOf(".publicFlagDetailLayer {")));
  assert.match(layer, /position: fixed;/);
  assert.match(layer, /inset: 0;/);
  assert.match(layer, /pointer-events: none;/);
  const drawerZ = zOf(".publicFlagDetailLayer");
  const pageLayers = [...css.matchAll(/([^{}]+)\{[^{}]*?z-index:\s*(\d+)/g)]
    .map((match) => ({ selector: match[1].trim(), z: Number(match[2]) }))
    .filter((rule) => !/modal|Modal|Overlay|overlay|mobileDrawer|kioskPin|kioskExit|publicFlagDetailLayer/.test(rule.selector));
  for (const rule of pageLayers) assert.ok(rule.z < drawerZ, `${rule.selector} z-index ${rule.z} is not below the drawer ${drawerZ}`);
  assert.ok(drawerZ > 9200, "above the phone menu button");
  assert.ok(drawerZ < zOf(".appConfirmOverlay,\n.modalOverlay.appConfirmOverlay"), "the card preview opened from the drawer still sits above it");
  assert.ok(zOf(".kioskExitZone") > drawerZ);
  assert.ok(zOf(".kioskPinOverlay") > zOf(".kioskExitZone"));
  assert.doesNotMatch(css, /\.publicFlagMap[^,{]*\.publicFlagDetail/);
  assert.doesNotMatch(css, /--flag-map-header, 92px\) \+ 8px\);\s*\}\s*\n\s*\.publicFlagMap\.hasDetail/);

  const phone = css.slice(css.indexOf("@media (max-width: 760px) {\n  .publicFlagMap {"));
  assert.match(phone, /\.publicFlagDetailLayer:not\(\.isKiosk\) \.publicFlagDetailBackdrop \{\s*display: block;/);
  assert.match(css, /\.publicFlagDetailBackdrop \{\s*display: none;/);
  assert.match(css, /\.publicFlagMap\.hasDetail:not\(\.isKiosk\) \.publicFlagChrome \{\s*margin-right:/);
  assert.match(css, /\.publicFlagMap\.isKiosk\.hasDetail \.publicFlagChrome \{\s*margin-right:/);
  assert.match(css, /html\.isMapFullscreen \.publicFlagMap\.hasDetail:not\(\.isKiosk\) \.publicFlagChrome \{\s*margin-right:/);
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

test("the zoom ladder goes sections, flag numbers, names, then seals", () => {
  const at = (spotPx, flagsOnScreen) => flagMapDetailGates(spotPx / SPOT_SIZE.width, flagsOnScreen);
  assert.equal(at(18, 1000).level, "Sections");
  assert.equal(at(18, 1000).sectionsInteractive, true);
  assert.equal(at(18, 1000).numbers, false);
  assert.equal(at(64, 300).level, "Spots");
  assert.equal(at(64, 300).numbers, true);
  assert.equal(at(64, 300).names, false);
  assert.equal(at(64, 300).flagsInteractive, true);
  assert.equal(at(130, 60).level, "Names");
  assert.equal(at(130, 60).seals, false);
  assert.equal(at(130, 4).seals, false);
  assert.equal(at(300, 20).level, "Names");
  assert.equal(at(300, 9).level, "Closest");
  assert.equal(at(300, 9).seals, true);

  assert.equal(flagMapLayerOpacities(0.05).section, 1);
  assert.equal(flagMapLayerOpacities(FLAG_MAP_ZOOM.spotsFadeEnd / SPOT_SIZE.width).section, 0);
  let previous = flagMapLayerOpacities(0.05);
  for (let spotPx = 8; spotPx <= 600; spotPx += 1) {
    const next = flagMapLayerOpacities(spotPx / SPOT_SIZE.width);
    assert.ok(Math.abs(next.section - previous.section) < 0.2, `section jumped at ${spotPx}`);
    previous = next;
  }
  for (const viewport of [{ width: 1408, height: 640 }, { width: 366, height: 560 }]) {
    const scale = closestViewScale(viewport);
    const columns = viewport.width / ((SPOT_SIZE.width + SPOT_SIZE.gap) * scale);
    const rows = viewport.height / ((SPOT_SIZE.height + SPOT_SIZE.gap) * scale);
    assert.ok((Math.ceil(columns) + 1) * (Math.ceil(rows) + 1) >= 1);
    assert.ok(columns <= 2.61 && rows <= 2.61);
    assert.ok(scale * SPOT_SIZE.width >= FLAG_MAP_ZOOM.closestAt);
  }
  assert.ok(spotsViewScale() * SPOT_SIZE.width > FLAG_MAP_ZOOM.spotsFadeEnd);
  assert.match(page, /closestViewScale\(size\)/);
  assert.match(page, /spotsViewScale\(\)/);
  assert.match(page, /className="publicFlagNumber"/);
  assert.match(page, /className="publicFlagLegend"/);
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
  assert.equal(field.flags.length, 5);
  assert.equal(field.flags.some((flag) => flag.flagGridName === "Z-01"), false);
  assert.equal(field.cells.some((cell) => cell.flagGridName === "Z-11"), false);
  const zCells = field.cells.filter((cell) => cell.section === "Z");
  assert.equal(zCells.length, FIELD_MAP_Z_ROWS + 1);
  assert.ok(zCells.filter((cell) => cell.columnNumber <= FIELD_MAP_Z_ROWS).every((cell) => cell.status === "Reserved" && cell.serviceFlag));
  assert.equal(field.cells.filter((cell) => cell.section === "QQ").length, 1);
  assert.equal(field.cells.filter((cell) => cell.section === "AA").length, FIELD_MAP_ROWS);
  assert.ok(field.aisle);
  assert.ok(field.aisle.y > row21.y);
  assert.ok(field.aisle.y < row1.y);
  const z01 = field.cells.find((cell) => cell.flagGridName === "Z-01");
  const zSection = field.sections.find((section) => section.section === "Z");
  assert.equal(field.pavilion.label, "Pavilion");
  assert.equal(field.pavilion.x + field.pavilion.width / 2, zSection.x + zSection.width / 2);
  assert.ok(field.pavilion.y >= z01.y + z01.height);
  assert.ok(field.rowLabels.some((label) => label.row === 1 && label.y === z01.y));
  assert.ok(field.columnLabels.some((label) => label.label === "AA" && label.x === aa.x));
  const empty = layoutFlagField([]);
  assert.equal(empty.flags.length, 0);
  assert.equal(empty.sections.length, PAVILION_COLUMNS_LEFT_TO_RIGHT.length);
  assert.equal(empty.cells.filter((cell) => cell.section === "Z" && cell.status === "Reserved").length, FIELD_MAP_Z_ROWS);
  assert.ok(empty.cells.some((cell) => cell.flagGridName === "Z-10" && cell.serviceFlag));
  assert.equal(empty.cells.some((cell) => cell.flagGridName === "Z-11"), false);
  assert.equal(empty.serviceFlags.label, "Service Flags");
  assert.deepEqual(empty.zones.filter((zone) => zone.section === "C").map((zone) => zone.label), ["C 21\u201340", "C 1\u201320"]);
  assert.deepEqual(empty.zones.filter((zone) => zone.section === "Z").map((zone) => zone.label), []);
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
  assert.match(css, /\.publicFlagDetailLayer\.isKiosk \.publicFlagPhotoFrame/);
  assert.match(css, /max-height:\s*min\(32vh, 220px\)/);
});

test("branch seals render only when fewer than 10 flags are on screen", () => {
  assert.equal(SEAL_ON_SCREEN_LIMIT, 10);
  assert.equal(sealImagesAllowed(10), false);
  assert.equal(sealImagesAllowed(9), true);
  assert.equal(sealImagesAllowed(0), true);
  const wide = flagMapDetailGates(3, 40);
  assert.equal(wide.seals, false);
  assert.equal(wide.names, true);
  assert.equal(wide.flagsInteractive, true);
  const close = flagMapDetailGates(3, 9);
  assert.equal(close.seals, true);
  assert.equal(close.names, true);
  assert.equal(close.ranks, true);
  assert.equal(close.flagsInteractive, true);
  const mid = flagMapDetailGates(0.12, 400);
  assert.equal(mid.seals, false);
  assert.equal(mid.names, false);
  assert.equal(mid.sectionsInteractive, true);
  assert.equal(mid.flagsInteractive, false);
  assert.equal(flagMapDetailGates(3, 10).seals, false);
  assert.equal(flagMapDetailGates(3).seals, false);

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
  assert.match(flush.slice(flush.indexOf("setTimeout")), /setTimeout\(settleGesture, WHEEL_SETTLE_MS\)/);
  const settle = page.slice(page.indexOf("function settleGesture()"), page.indexOf("function paintView("));
  assert.match(settle, /publishCull\(true\)/);
  assert.match(settle, /animateTo\(target\)/);

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
  let gates = flagMapDetailGates(view.scale);
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
    const nextGates = flagMapDetailGates(view.scale);
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
  let gates = flagMapDetailGates(view.scale);
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
    const nextGates = flagMapDetailGates(view.scale);
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

test("the in-view list sorts sections by letter and range, and spots naturally", () => {
  const zone = (label) => ({ kind: "zone", label });
  const spot = (name) => ({ kind: "spot", name, label: `${name} \u00b7 Open` });
  const service = { kind: "service", label: "Service Flags, Z 1\u201310" };
  const sections = [zone("Z 21\u201340"), zone("B 1\u201320"), zone("AA 1\u201320"), zone("Z 11\u201320"), service, zone("A 21\u201340"), zone("A 1\u201320"), zone("YY 1\u201320")]
    .sort(compareInViewItems).map((item) => item.label);
  assert.deepEqual(sections, ["A 1\u201320", "A 21\u201340", "AA 1\u201320", "B 1\u201320", "YY 1\u201320", "Service Flags, Z 1\u201310", "Z 11\u201320", "Z 21\u201340"]);
  const spots = [spot("B-1"), spot("A-10"), spot("AA-3"), spot("A-2")].sort(compareInViewItems).map((item) => item.name);
  assert.deepEqual(spots, ["A-2", "A-10", "AA-3", "B-1"]);
});

test("2.0.7: field sections show letters only, the list keeps ranges, and the map opens full screen", () => {
  const zoneButton = page.slice(page.indexOf('className={["publicFlagTile publicFlagZone"'), page.indexOf("{field?.serviceFlags ? ("));
  assert.match(zoneButton, /<strong [^>]*>\{zone\.section\}<\/strong>/);
  assert.doesNotMatch(zoneButton, /sizes\.range|rangeSize|<span/, "no flag-number range under the letter");
  assert.doesNotMatch(css, /\.publicFlagZone span/);
  assert.match(page, /serviceFlagsLabelSize\(area, renderScale, sectionLabels\)/);
  assert.match(page, /label\.vertical \? <span className="isVertical">\{label\.text\}<\/span>/);

  const field = pavilionField(40);
  const viewport = { width: 1406, height: 618 };
  const fitted = fitWholeField({ x: 0, y: 0, width: field.worldWidth, height: field.worldHeight }, viewport, FIT_PADDING);
  const uniform = sectionLabelSizes(field.zones, fitted.scale);
  const letters = new Set(field.zones.map((zone) => zoneLabelSizes(zone, fitted.scale, uniform).letter));
  assert.equal(letters.size, 1, "every letter is the same size");
  assert.ok([...letters][0] > 0);
  const box = field.serviceFlags;
  assert.equal(serviceFlagsLabelSize(serviceFlagsLabelBox(box, null), fitted.scale, uniform).text.length > 0, true);

  const labels = flagMapItemsInView(field, fitted, viewport, flagMapDetailGates(fitted.scale, 1000)).items.map((item) => item.label);
  assert.ok(labels.includes("C 21\u201340") && labels.includes("Z 11\u201320") && labels.includes(`${SERVICE_FLAGS_LABEL}, Z 1\u201310`));
  assert.match(page, /<option key=\{item\.key\} value=\{item\.key\}>\{item\.label\}<\/option>/);

  assert.equal(initialMapFullScreen(false), true);
  assert.equal(initialMapFullScreen(true), false, "kiosk keeps its own fullscreen and lockdown");
  assert.match(page, /const mapFullRef = useRef\(initialMapFullScreen\(kiosk\)\)/);
  assert.match(page, /const \[mapFull, setMapFull\] = useState\(initialMapFullScreen\(kiosk\)\)/);
  assert.match(page, /if \(mapFullRef\.current\) document\.documentElement\.classList\.add\(MAP_FULLSCREEN_CLASS\)/);
  const opening = page.slice(page.indexOf("const mapFullRef"), page.indexOf("async function enterMapFull"));
  assert.doesNotMatch(opening, /requestElementFullscreen\(/, "no browser Fullscreen API call on load");
});

test("Esc closes the topmost open thing before leaving full screen", () => {
  const press = (state) => {
    const target = flagMapEscapeTarget(state);
    if (target === "card") return { ...state, cardOpen: false };
    if (target === "list") return { ...state, listOpen: false };
    if (target === "fullscreen") return { ...state, mapFull: false };
    return state;
  };
  const drawer = press({ cardOpen: true, listOpen: true, mapFull: true });
  assert.deepEqual(drawer, { cardOpen: false, listOpen: true, mapFull: true }, "drawer closes, still full screen");
  const list = press(drawer);
  assert.deepEqual(list, { cardOpen: false, listOpen: false, mapFull: true }, "suggestions close, still full screen");
  assert.deepEqual(press(list), { cardOpen: false, listOpen: false, mapFull: false }, "nothing open exits full screen");
  assert.equal(flagMapEscapeTarget({ cardOpen: false, listOpen: false, mapFull: false }), null);
  assert.match(page, /flagMapEscapeTarget\(\{ \.\.\.escapeStateRef\.current, mapFull: mapFullRef\.current \}\)/);
  assert.match(page, /listOpen: \(open && searchRows\.length > 0\) \|\| Boolean\(jumpPending\)/);
});

test("row numbers and section letters stay pinned in their viewport-edge gutters and track pan and zoom", () => {
  const field = pavilionField(40);
  const viewport = { width: 600, height: 400 };
  const view = { x: -900, y: -700, scale: 0.6 };
  const labels = pinnedAxisLabels(field, view, viewport);
  const rows = labels.filter((label) => label.kind === "row");
  const columns = labels.filter((label) => label.kind === "column");
  assert.equal(rows.length, field.rowLabels.length);
  assert.equal(columns.length, field.columnLabels.length);
  for (const label of rows) {
    const source = field.rowLabels.find((item) => `row-${item.side}-${item.row}` === label.key);
    assert.ok(Math.abs(label.y - (view.y + source.y * view.scale)) < 1e-6, "each row number lines up with its row");
    assert.ok(Math.abs(label.height - source.height * view.scale) < 1e-6);
    if (label.side === "left") assert.ok(label.x >= 0 && label.x + label.width <= AXIS_STRIP.row, "left numbers sit inside the left gutter");
    else assert.ok(label.x >= viewport.width - AXIS_STRIP.row && label.x + label.width <= viewport.width, "right numbers sit inside the right gutter");
    const middle = label.y + label.height / 2;
    assert.equal(label.hidden, middle < 0 || middle > viewport.height - AXIS_STRIP.column || !label.fontSize, "(b) no row number draws into the bottom corners");
  }
  for (const label of columns) {
    const source = field.columnLabels.find((item) => item.section === label.label);
    assert.ok(Math.abs(label.x + label.width / 2 - (view.x + (source.x + source.width / 2) * view.scale)) < 1e-6, "each letter centres on its column");
    assert.ok(label.y >= viewport.height - AXIS_STRIP.column && label.y + label.height <= viewport.height, "letters sit inside the bottom gutter");
    const middle = label.x + label.width / 2;
    const endRoom = AXIS_STRIP.row + AXIS_LABEL_PAD + label.fontSize / 2;
    assert.equal(label.hidden, middle < endRoom || middle > viewport.width - endRoom || !label.fontSize, "(a) no letter stacks behind a side gutter or sits flush with its corner");
  }
  assert.ok(rows.some((label) => label.hidden) && rows.some((label) => !label.hidden));
  assert.ok(columns.some((label) => label.hidden) && columns.some((label) => !label.hidden));
  assert.ok(rows.every((label) => label.width <= AXIS_STRIP.row) && columns.every((label) => label.height <= AXIS_STRIP.column));

  const fitted = fitWholeField(fieldFitBounds(field), { width: 1406, height: 618 }, FIT_PADDING);
  const whole = pinnedAxisLabels(field, fitted, { width: 1406, height: 618 });
  const left = whole.find((label) => label.key === `row-left-1`);
  const source = field.rowLabels.find((item) => item.side === "left" && item.row === 1);
  assert.ok(Math.abs(left.y + left.height / 2 - (fitted.y + (source.y + source.height / 2) * fitted.scale)) < 1e-6, "with the whole field in view, numbers still centre on their rows");
  const frame = axisStripFrame(field, fitted, { width: 1406, height: 618 });
  assert.ok(left.x >= frame.left && left.x + left.width <= frame.left + AXIS_STRIP.row, "and stay inside the left strip");
  assert.match(page, /<div ref=\{axisRef\} className="publicFlagAxis" aria-hidden="true">/);
  assert.match(css, /\.publicFlagAxis \{[^}]*z-index: 3;[^}]*pointer-events: none;/);
});

test("on the live field the fit keeps YY, AA and the pavilion inside the gutter insets at 320, 390, 1440 and the kiosk", () => {
  const field = pavilionField(40);
  assert.equal(field.worldWidth, 4912);
  assert.equal(field.worldHeight, 5176);
  assert.equal(field.rowLabels.find((label) => label.side === "left").x + field.rowLabels[0].width + 12, 200, "row-label margin of 200 world px");
  const inset = gutterInsets(FIT_PADDING);
  assert.ok(inset.left >= AXIS_STRIP.row + FIT_PADDING && inset.right >= AXIS_STRIP.row + FIT_PADDING && inset.bottom >= AXIS_STRIP.column + FIT_PADDING);
  const yy = boundsOf(field.cells.filter((cell) => cell.section === "YY"));
  const aa = boundsOf(field.cells.filter((cell) => cell.section === "AA"));
  // 320 and 390 phone maps, 1440, and the kiosk.
  for (const viewport of [{ width: 284, height: 567 }, { width: 346, height: 567 }, { width: 1406, height: 618 }, { width: 1874, height: 858 }]) {
    {
      const view = clampView(fitWholeField(fieldFitBounds(field), viewport, FIT_PADDING), viewport, fieldFitBounds(field), { pad: FIT_PADDING });
      const screen = (box) => ({ left: view.x + box.x * view.scale, right: view.x + (box.x + box.width) * view.scale, top: view.y + box.y * view.scale, bottom: view.y + (box.y + box.height) * view.scale });
      const where = `${viewport.width}x${viewport.height}`;
      assert.ok(screen(yy).left >= inset.left - 0.01, `YY clears the left gutter at ${where}`);
      assert.ok(screen(aa).right <= viewport.width - inset.right + 0.01, `AA clears the right gutter at ${where}`);
      assert.ok(screen(field.pavilion).bottom <= viewport.height - inset.bottom + 0.01, `the pavilion clears the bottom gutter at ${where}`);
      assert.ok(screen(field.pavilion).left >= inset.left - 0.01 && screen(field.pavilion).right <= viewport.width - inset.right + 0.01);
    }
    const max = spotFitMaxScale(viewport, FIT_PADDING);
    assert.ok(SPOT_SIZE.width * max <= viewport.width - inset.left - inset.right + 0.01 || spotScreenWidth(max) <= FLAG_MAP_ZOOM.closestAt + 1.01, `the closest zoom fits one spot between the gutters at ${viewport.width}`);
  }
});

test("pinned axis labels sit on solid navy gutter strips that taps pass through", () => {
  const gutters = page.slice(page.indexOf('className="publicFlagAxisGutters"'), page.indexOf('<div ref={axisRef}'));
  assert.match(page, /<div ref=\{guttersRef\} className="publicFlagAxisGutters" aria-hidden="true">/);
  for (const side of ["isLeft", "isRight", "isBottom"]) {
    assert.match(gutters, new RegExp(`<div className="publicFlagAxisGutter ${side}" />`));
  }
  assert.ok(page.indexOf('className="publicFlagAxisGutters"') < page.indexOf("<div ref={axisRef}"), "gutters paint under the labels");
  assert.match(css, /\.publicFlagAxisGutters \{[^}]*z-index: 3;[^}]*pointer-events: none;/);
  assert.match(css, /\.publicFlagAxisGutter \{[^}]*background: rgba\(11, 31, 58, 0\.9\d?\);[^}]*pointer-events: none;/);
  assert.match(css, /\.publicFlagAxisGutter\.isLeft,\s*\.publicFlagAxisGutter\.isRight \{[^}]*bottom: 28px;[^}]*width: 36px;/);
  assert.match(css, /\.publicFlagAxisGutter\.isBottom \{[^}]*height: 28px;/);
  assert.match(css, /\.publicFlagAxis \.publicFlagRowLabel,\s*\.publicFlagAxis \.publicFlagColumnLabel \{[^}]*pointer-events: none;/);
});

test("left and right row numbers have the same inner padding and mirror each other's inset from the viewport edge (2.0.10)", () => {
  const field = pavilionField(40);
  for (const viewport of [{ width: 284, height: 567 }, { width: 346, height: 567 }, { width: 1406, height: 618 }, { width: 1874, height: 858 }]) {
    for (const scale of [0.05, 0.2, 0.6, 1.2]) {
      const labels = pinnedAxisLabels(field, { x: -100, y: -100, scale }, viewport);
      for (const left of labels.filter((label) => label.kind === "row" && label.side === "left")) {
        const right = labels.find((label) => label.key === left.key.replace("row-left-", "row-right-"));
        assert.ok(right, `row ${left.label} has a right-side twin`);
        assert.ok(Math.abs(left.x - (viewport.width - right.x - right.width)) < 1e-9, `row ${left.label} insets match at ${viewport.width}px, scale ${scale}`);
        assert.equal(left.width, right.width);
        assert.equal(left.fontSize, right.fontSize, "label size is the same on both sides");
      }
    }
  }
  const axisRule = css.match(/\.publicFlagAxis \.publicFlagRowLabel,\s*\.publicFlagAxis \.publicFlagColumnLabel \{([^}]*)\}/)?.[1] ?? "";
  assert.match(axisRule, /justify-content: center;/, "text is centred in each strip, not pushed to one edge");
  assert.match(axisRule, new RegExp(`padding: 0 ${AXIS_LABEL_PAD}px;`), "equal inline padding on both sides");
  assert.ok(css.indexOf(".publicFlagAxis .publicFlagRowLabel,") > css.indexOf(".publicFlagRowLabel.isRight {"), "the axis rule wins over the one-sided isRight padding");
  assert.match(page, /pinnedAxisLabels\(field, view, \{\s*width: axis\.clientWidth \|\| size\.width,\s*height: axis\.clientHeight \|\| size\.height\s*\}\)/, "labels are placed in the overlay's own box, not the bordered viewport rect");
});

// Spot-tile maths, independent of the label code: a row's centre is the middle of
// its spots and a column's centre is the middle of its section's spots, both
// taken through the same translate-then-scale transform as .publicFlagLayer.
function spotRowCentre(field, view, row) {
  const box = boundsOf(field.cells.filter((cell) => cell.columnNumber === row));
  return view.y + (box.y + box.height / 2) * view.scale;
}
function spotColumnCentre(field, view, section) {
  const box = boundsOf(field.cells.filter((cell) => cell.section === section));
  return view.x + (box.x + box.width / 2) * view.scale;
}
const AXIS_MAPS = [
  { width: 284, height: 567, name: "320 phone map" },
  { width: 346, height: 567, name: "390 phone map" },
  { width: 1406, height: 618, name: "1440x618 map" },
  { width: 320, height: 567, name: "320x567" },
  { width: 390, height: 567, name: "390x567" },
  { width: 1440, height: 618, name: "1440x618" }
];

test("every axis label sits at its true row or column centre, at the fit and zoomed in (2.0.12)", () => {
  const field = pavilionField(40);
  const box = fieldFitBounds(field);
  for (const viewport of AXIS_MAPS) {
    const fitted = clampView(fitWholeField(box, viewport, FIT_PADDING), viewport, box, { pad: FIT_PADDING });
    const zoomed = clampView(zoomAt(fitted, viewport.width / 2, viewport.height / 2, fitted.scale * 3), viewport, box, { pad: FIT_PADDING });
    for (const [level, view] of [["fit", fitted], ["zoomed", zoomed]]) {
      const where = `${viewport.name} ${level}`;
      const labels = pinnedAxisLabels(field, view, viewport).filter((label) => !label.hidden);
      assert.ok(labels.length > 0, `labels show at ${where}`);
      for (const label of labels) {
        if (label.kind === "row") assert.ok(Math.abs(label.y + label.height / 2 - spotRowCentre(field, view, label.row)) <= 1, `row ${label.row} (${label.side}) centres on its spots at ${where}`);
        else assert.ok(Math.abs(label.x + label.width / 2 - spotColumnCentre(field, view, label.label)) <= 1, `${label.label} centres on its spots at ${where}`);
      }
      const z = labels.find((label) => label.label === "Z");
      const m = labels.find((label) => label.label === "M");
      if (z && m) {
        const gap = spotColumnCentre(field, view, "M") - spotColumnCentre(field, view, "Z");
        assert.ok(Math.abs((m.x + m.width / 2) - (z.x + z.width / 2) - gap) <= 1, `the Z|M aisle shows at its true width at ${where}`);
      }
    }
  }
});

test("at full zoom-out every row number and every letter shows, one font per axis, with no overlaps (2.0.12)", () => {
  const field = pavilionField(40);
  const box = fieldFitBounds(field);
  const letters = field.columnLabels.map((label) => label.label);
  assert.equal(letters.length, PAVILION_COLUMNS_LEFT_TO_RIGHT.length);
  const report = [];
  for (const viewport of AXIS_MAPS) {
    const view = clampView(fitWholeField(box, viewport, FIT_PADDING), viewport, box, { pad: FIT_PADDING });
    const labels = pinnedAxisLabels(field, view, viewport);
    const where = viewport.name;
    for (const side of ["left", "right"]) {
      const rows = labels.filter((label) => label.kind === "row" && label.side === side && !label.hidden);
      assert.equal(rows.length, 40, `all 40 rows show on the ${side} at ${where}`);
      assert.deepEqual(rows.map((label) => label.row).sort((a, b) => a - b), Array.from({ length: 40 }, (_, i) => i + 1));
      assert.equal(new Set(rows.map((label) => label.fontSize)).size, 1, `one row font at ${where}`);
      const sorted = [...rows].sort((a, b) => a.y - b.y);
      sorted.slice(1).forEach((label, i) => assert.ok(sorted[i].y + sorted[i].height <= label.y + 1e-6, `rows ${sorted[i].label} and ${label.label} do not overlap at ${where}`));
      const pitch = Math.min(...sorted.slice(1).map((label, i) => spotRowCentre(field, view, label.row) - spotRowCentre(field, view, sorted[i].row)).map(Math.abs));
      for (const label of rows) {
        assert.ok(label.fontSize <= label.height + 1e-6 && label.width - AXIS_LABEL_PAD * 2 >= String(label.label).length * 0.72 * label.fontSize, `row ${label.row} fits its box at ${where}`);
        assert.ok(label.fontSize >= AXIS_MIN_FONT_PX || Math.abs(label.fontSize - pitch) < 1e-6, `row font is at the ${AXIS_MIN_FONT_PX}px floor or, below it, the full row pitch at ${where}`);
      }
    }
    const columns = labels.filter((label) => label.kind === "column" && !label.hidden);
    assert.deepEqual(columns.map((label) => label.label), letters, `every letter shows, in order, at ${where}`);
    assert.equal(new Set(columns.map((label) => label.fontSize)).size, 1, `one letter font at ${where}`);
    columns.slice(1).forEach((label, i) => assert.ok(columns[i].x + columns[i].width <= label.x + 1e-6, `${columns[i].label} and ${label.label} do not overlap at ${where}`));
    for (const label of columns) assert.ok(label.width >= String(label.label).length * 0.7 * label.fontSize, `${label.label} fits its box at ${where}`);
    const columnFont = columns[0].fontSize;
    const tightest = Math.min(...columns.slice(1).map((label, i) => {
      const distance = spotColumnCentre(field, view, label.label) - spotColumnCentre(field, view, columns[i].label);
      return (distance - 1) / (0.7 * (label.label.length + columns[i].label.length) / 2);
    }));
    assert.ok(columnFont >= AXIS_MIN_FONT_PX || Math.abs(columnFont - tightest) < 1e-6, `letter font is at the floor or, below it, the largest that keeps letters apart at ${where}`);
    if (Math.min(14, tightest) >= AXIS_MIN_FONT_PX) assert.ok(columnFont >= AXIS_MIN_FONT_PX, `letters that fit at ${AXIS_MIN_FONT_PX}px are not shrunk below it at ${where}`);
    report.push(`${where}: rows ${labels.find((label) => label.kind === "row").fontSize.toFixed(2)}px, letters ${columnFont.toFixed(2)}px`);
    assert.deepEqual(pinnedAxisLabels(field, view, viewport), labels, "labels are deterministic");
  }
  assert.ok(report.length === AXIS_MAPS.length, report.join("; "));
});

test("letters fit at the floor on a wide map and none are dropped (2.0.12)", () => {
  const field = pavilionField(40);
  const box = fieldFitBounds(field);
  const viewport = { width: 1874, height: 858 };
  const view = clampView(fitWholeField(box, viewport, FIT_PADDING), viewport, box, { pad: FIT_PADDING });
  const columns = pinnedAxisLabels(field, view, viewport).filter((label) => label.kind === "column");
  assert.ok(columns.every((label) => !label.hidden && label.fontSize >= AXIS_MIN_FONT_PX), "every letter shows at or above the floor");
  assert.equal(columns.length, field.columnLabels.length);
});

test("at the fit the label strips sit just outside the field and pin back to the window edges once it overflows (2.0.11)", () => {
  const field = pavilionField(40);
  const box = fieldFitBounds(field);
  for (const viewport of [{ width: 1920, height: 900 }, { width: 1440, height: 618 }, { width: 390, height: 567 }, { width: 320, height: 567 }]) {
    const view = fitWholeField(box, viewport, FIT_PADDING);
    const frame = axisStripFrame(field, view, viewport);
    const left = view.x + box.x * view.scale;
    const right = view.x + (box.x + box.width) * view.scale;
    const bottom = view.y + (box.y + box.height) * view.scale;
    const where = `${viewport.width}x${viewport.height}`;
    assert.ok(Math.abs(frame.left + AXIS_STRIP.row + FIT_PADDING - left) < 1e-6, `left strip hugs the field at ${where}`);
    assert.ok(Math.abs(frame.right - FIT_PADDING - right) < 1e-6, `right strip hugs the field at ${where}`);
    assert.ok(Math.abs(frame.bottom - FIT_PADDING - bottom) < 1e-6, `bottom strip hugs the field at ${where}`);
    assert.ok(frame.left >= 0 && frame.right <= viewport.width - AXIS_STRIP.row && frame.bottom <= viewport.height - AXIS_STRIP.column, `strips stay inside the window at ${where}`);
    assert.ok(Math.abs(frame.left - (viewport.width - AXIS_STRIP.row - frame.right)) < 1e-6, "side strips mirror each other");
    const zoomed = zoomAt(view, viewport.width / 2, viewport.height / 2, view.scale * 4);
    assert.deepEqual(axisStripFrame(field, zoomed, viewport), { left: 0, right: viewport.width - AXIS_STRIP.row, bottom: viewport.height - AXIS_STRIP.column }, `overflowing field pins strips to the edges at ${where}`);
  }
  assert.match(page, /const frame = axisStripFrame\(field, view, box\);/);
});

test("the flag detail keeps its action row pinned below a scrolling body with safe-area padding (2.0.10)", () => {
  const detail = page.slice(page.indexOf("function FlagDetail("), page.indexOf("const ICON_PATHS"));
  const scrollAt = detail.indexOf('<div className="publicFlagDetailScroll">');
  const actionsAt = detail.indexOf('<div className="publicFlagDetailActions">');
  assert.ok(scrollAt > 0 && actionsAt > scrollAt, "the actions follow the scroll area");
  const scroll = detail.slice(scrollAt, actionsAt);
  const actions = detail.slice(actionsAt, detail.indexOf("</aside>"));
  assert.doesNotMatch(scroll, /<button/, "no action button sits inside the scroll container");
  assert.ok(scroll.indexOf("publicFlagPhotoFrame") > 0 && scroll.indexOf("publicFlagBio") > 0 && scroll.indexOf("publicFlagQrBlock") > 0, "photo, bio and QR scroll");
  assert.ok(actions.indexOf("Preview card") > 0 && actions.indexOf("Preview card") < actions.indexOf(">Close</button>"), "Preview card then Close, same order");
  assert.match(css, /\.publicFlagDetail \{[^}]*overflow: hidden;[^}]*display: flex;[^}]*flex-direction: column;/);
  assert.match(css, /\.publicFlagDetailScroll \{[^}]*flex: 1 1 auto;[^}]*min-height: 0;[^}]*overflow-y: auto;/);
  assert.match(css, /\.publicFlagDetailActions \{[^}]*flex: none;[^}]*padding-bottom: max\(16px, env\(safe-area-inset-bottom\)\);[^}]*background: #fffdf8;[^}]*border-top: 1px solid/);
});

test("the in-view list sits inside the map controls toolbar group", () => {
  const group = page.slice(page.indexOf('className="publicFlagControls" role="toolbar"'), page.indexOf('<p className="publicFlagLevel srOnly"'));
  assert.match(group, /<div className="publicFlagJump">\s*<label htmlFor=\{jumpId\} className="srOnly">/);
  assert.match(group, /<select id=\{jumpId\} value=\{jumpPending\}/);
  assert.match(group, /<\/select>\s*<\/div>\s*<\/div>\s*$/, "the select closes inside the toolbar group");
  assert.match(css, /\.publicFlagControls \{[^}]*flex-wrap: nowrap;[^}]*min-width: 0;/);
  assert.match(css, /\.publicFlagControls \{[^}]*display: flex;[^}]*flex: 1 1 0%;[^}]*justify-content: space-between;[^}]*width: 100%;/, "the toolbar spans the full width like the search box");
  assert.match(css, /\.publicFlagControls \.publicFlagIconButton \{[^}]*flex: 0 0 auto;/, "the icon buttons keep their size, grouped on the left");
  assert.match(css, /\.publicFlagJump \{[^}]*flex: 1 1 116px;[^}]*min-width: 0;[^}]*margin-left: auto;/, "the in-view list is pushed to the right and fills what is left");
  assert.ok(group.lastIndexOf("publicFlagIconButton") < group.indexOf('<div className="publicFlagJump">'), "the in-view list is the last item in the toolbar");
  assert.match(css, /\.publicFlagJump select \{[^}]*text-overflow: ellipsis;[^}]*border: 0;[^}]*background-color: transparent;/);
});
