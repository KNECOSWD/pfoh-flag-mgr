import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import {
  FLAG_MAP_ZOOM,
  cullRects,
  filterFlagMapSuggestions,
  fitView,
  flagMapLayerOpacities,
  layoutFlagField,
  pointerIsTap,
  publicFlagMapUrls,
  viewportWorldRect,
  zoomAt
} from "../src/publicFlagMap.js";

const app = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");
const program = readFileSync(new URL("../../backend/PFOH.Api/Program.cs", import.meta.url), "utf8");
const robots = readFileSync(new URL("../public/robots.txt", import.meta.url), "utf8");
const page = readFileSync(new URL("../src/PublicFlagMap.tsx", import.meta.url), "utf8");

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
