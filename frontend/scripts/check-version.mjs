import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const version = readFileSync(new URL("../../VERSION", import.meta.url), "utf8").trim();

test("package.json matches the VERSION file", () => {
  const pkg = JSON.parse(readFileSync(new URL("../package.json", import.meta.url), "utf8"));
  assert.equal(pkg.version, version);
  assert.equal(version, "3.0.1");
  assert.match(version, /^\d+\.\d+\.\d+$/, "major.minor.patch, any number of digits each");
});

test("the API assembly version is read from VERSION", () => {
  const props = readFileSync(new URL("../../backend/Directory.Build.props", import.meta.url), "utf8");
  assert.match(props, /\[System\.IO\.Path\]::Combine\('\$\(MSBuildThisFileDirectory\)', '\.\.', 'VERSION'\)/);
});

test("the footer stamps the shared version", () => {
  const app = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");
  assert.match(app, /className="appVersionStamp"/);
  assert.match(app, /Version \{packageInfo\.version\}/);
});

test("the changelog records 1.0.0 through 3.0.1", () => {
  const changelog = readFileSync(new URL("../../CHANGELOG.md", import.meta.url), "utf8");
  const headings = [...changelog.matchAll(/^## (\S+)\s*$/gm)].map((match) => match[1]);
  assert.equal(headings[0], version, "the newest entry is the current version");
  const expected = ["1.0.0", "1.1.0", "1.2.0", "1.3.0", "1.4.0", "1.5.0", "1.5.1", "1.5.2", "2.0.0", "2.0.1", "2.0.2", "2.0.3", "2.0.4", "2.0.5", "2.0.6", "2.0.7", "2.0.8", "2.0.9", "2.0.10", "2.0.11", "2.0.12", "2.0.13", "2.0.14", "2.0.15", "2.0.16", "2.0.17", "2.0.18", "2.0.19", "2.0.20", "2.0.21", "2.0.22", "2.1.0", "2.1.1", "2.1.2", "2.1.3", "2.1.4", "2.1.5", "2.2.0", "2.2.1", "3.0.0", "3.0.1"];
  for (const entry of expected) assert.ok(headings.includes(entry), `## ${entry} is an exact heading, not a prefix of a longer one`);
  assert.ok(!headings.includes("1.4.1"));
  assert.ok(!headings.includes("1.6.0"));
});
