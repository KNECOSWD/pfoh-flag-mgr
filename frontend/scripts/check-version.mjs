import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const version = readFileSync(new URL("../../VERSION", import.meta.url), "utf8").trim();

test("package.json matches the VERSION file", () => {
  const pkg = JSON.parse(readFileSync(new URL("../package.json", import.meta.url), "utf8"));
  assert.equal(pkg.version, version);
  assert.equal(version, "1.5.1");
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

test("the changelog records 1.0.0 through 1.5.1", () => {
  const changelog = readFileSync(new URL("../../CHANGELOG.md", import.meta.url), "utf8");
  assert.match(changelog, /## 1\.0\.0/);
  assert.match(changelog, /## 1\.1\.0/);
  assert.match(changelog, /## 1\.2\.0/);
  assert.match(changelog, /## 1\.3\.0/);
  assert.match(changelog, /## 1\.4\.0/);
  assert.match(changelog, /## 1\.5\.0/);
  assert.match(changelog, /## 1\.5\.1/);
  assert.doesNotMatch(changelog, /## 1\.4\.1/);
});
