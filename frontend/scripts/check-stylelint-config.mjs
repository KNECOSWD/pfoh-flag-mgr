import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const config = readFileSync(new URL("../stylelint.config.mjs", import.meta.url), "utf8");

test("stylelint gates important and duplicate selectors", () => {
  assert.doesNotMatch(config, /stylelint-config-standard/);
  for (const rule of ["declaration-no-important", "no-duplicate-selectors"]) {
    assert.match(config, new RegExp(`["']${rule}["']`));
    assert.doesNotMatch(config, new RegExp(`["']${rule}["']\\s*:\\s*(?:null|0)\\b`));
  }
});
