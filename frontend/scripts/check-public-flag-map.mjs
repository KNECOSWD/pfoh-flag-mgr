import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import {
  FLAG_MAP_ZOOM,
  KIOSK_IDLE_MS,
  attractView,
  createWheelBatch,
  cullRects,
  filterFlagMapSuggestions,
  fitView,
  flagDeepLink,
  flagMapDetailGates,
  flagMapLayerOpacities,
  layoutFlagField,
  pointerDragCapture,
  pointerIsTap,
  pointerShouldOpen,
  publicFlagMapUrls,
  readFlagMapLocation,
  springEase,
  viewportWorldRect,
  wheelFrameShouldCull,
  wheelScaleFactor,
  zoomAt
} from "../src/publicFlagMap.js";
import { qrModules } from "../src/qrCode.js";

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
  assert.match(page, /translate\(/);
  assert.match(page, /onWheel|addEventListener\("wheel"/);
  assert.match(page, /touchmove/);
});

test("robots.txt and the API header keep the page out of search results", () => {
  assert.match(robots, /Disallow:\s*\/flag-map/);
  assert.match(program, /X-Robots-Tag/);
  assert.match(program, /\/api\/public\/flag-map/);
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
  assert.ok(Math.abs(mid.seal - 0.5) < 0.001);
  assert.equal(mid.section + mid.seal, 1);
  assert.equal(mid.name, 0);
  assert.equal(mid.rank, 0);

  const seals = flagMapLayerOpacities(2.05, fit);
  assert.equal(seals.section, 0);
  assert.equal(seals.seal, 1);
  assert.equal(seals.name, 0);
  assert.equal(seals.rank, 0);

  const names = flagMapLayerOpacities(2.9, fit);
  assert.equal(names.seal, 1);
  assert.equal(names.name, 1);
  assert.equal(names.rank, 0);

  const closest = flagMapLayerOpacities(FLAG_MAP_ZOOM.rankFadeEnd, fit);
  assert.equal(closest.section, 0);
  assert.equal(closest.seal, 1);
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

test("kiosk mode, deep links, and the client-side QR stay on the public map", () => {
  assert.equal(KIOSK_IDLE_MS, 60000);
  assert.equal(readFlagMapLocation("?kiosk=1").kiosk, true);
  assert.equal(readFlagMapLocation("?flag=14").flagId, 14);
  assert.equal(readFlagMapLocation("?kiosk=1&flag=14").kiosk, true);
  const link = flagDeepLink("https://flags.example/flag-map?kiosk=1", 14);
  assert.match(link, /\/flag-map\?/);
  assert.match(link, /flag=14/);
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

function assertFinder(modules, x0, y0) {
  for (let y = 0; y < 7; y += 1) {
    for (let x = 0; x < 7; x += 1) {
      const border = x === 0 || y === 0 || x === 6 || y === 6;
      const center = x >= 2 && x <= 4 && y >= 2 && y <= 4;
      assert.equal(modules[y0 + y][x0 + x], border || center ? 1 : 0);
    }
  }
}
