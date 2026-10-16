import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { filterFlagMapSuggestions, publicFlagMapUrls } from "../src/publicFlagMap.js";

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
  assert.match(page, /role="combobox"/);
  assert.match(page, /Preview card/);
  assert.match(page, /aria-label="Zoom in"/);
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
