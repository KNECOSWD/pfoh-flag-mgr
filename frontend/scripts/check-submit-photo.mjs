import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import {
  HONOR_PHOTO_MAX_BYTES,
  HONOR_PHOTO_MAX_EDGE,
  shouldCompressHonoreePhoto,
  withDeadline
} from "../src/prepareHonoreePhoto.js";

test("large or wide photos are compressed before upload", () => {
  assert.equal(shouldCompressHonoreePhoto({ size: HONOR_PHOTO_MAX_BYTES + 1 }, 800, 600), true);
  assert.equal(shouldCompressHonoreePhoto({ size: 1000 }, HONOR_PHOTO_MAX_EDGE + 1, 10), true);
  assert.equal(shouldCompressHonoreePhoto({ size: HONOR_PHOTO_MAX_BYTES }, HONOR_PHOTO_MAX_EDGE, HONOR_PHOTO_MAX_EDGE), false);
  assert.equal(shouldCompressHonoreePhoto(null, 0, 0), false);
});

test("a hung photo preparation becomes an error", async () => {
  await assert.rejects(
    () => withDeadline(() => new Promise(() => {}), 30, "Preparing the photo took too long."),
    /Preparing the photo took too long/
  );

  assert.equal(await withDeadline(async () => "ready", 1000, "too slow"), "ready");
});

test("submit shows progress and does not wait on the admin reload", () => {
  const source = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");
  const nomination = source.slice(
    source.indexOf("async function submitNomination"),
    source.indexOf("async function saveDraft")
  );
  assert.match(nomination, /Preparing photo\.\.\./);
  assert.match(nomination, /Uploading submission\.\.\./);
  assert.match(nomination, /Finishing submission\.\.\./);
  assert.match(nomination, /void loadData\(\)/);
  assert.doesNotMatch(nomination, /await loadData\(\)/);

  const loadStart = source.indexOf("async function loadData");
  const load = source.slice(loadStart, source.indexOf("useEffect(() => {", loadStart));
  assert.match(load, /setLoading\(false\)/);
  assert.match(load, /void loadAdminData\(\)/);
  assert.doesNotMatch(load, /await loadAdminData\(\)/);
  assert.match(source, /submitPhase \|\| "Submitting\.\.\."/);
});
