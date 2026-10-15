import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { createBlobUrlCache } from "../src/blobUrlCache.js";

test("a second owner keeps the blob url when the first owner releases", () => {
  const revoked = [];
  let created = 0;
  const cache = createBlobUrlCache({
    createObjectURL: () => {
      created += 1;
      return `blob:photo-${created}`;
    },
    revokeObjectURL: (url) => revoked.push(url)
  });
  const blob = { size: 4 };

  assert.equal(cache.hold("66:pending/66.png", blob), "blob:photo-1");
  assert.equal(cache.hold("66:pending/66.png", blob), "blob:photo-1");
  assert.equal(created, 1);

  cache.release("66:pending/66.png");
  assert.deepEqual(revoked, []);
  cache.release("66:pending/66.png");
  assert.deepEqual(revoked, ["blob:photo-1"]);
});

test("release is explicit and does not revoke a url that is still held", () => {
  const revoked = [];
  const cache = createBlobUrlCache({
    createObjectURL: () => "blob:kept",
    revokeObjectURL: (url) => revoked.push(url)
  });

  cache.hold("review-15", {});
  cache.release("missing");
  assert.deepEqual(revoked, []);
  cache.release("review-15");
  assert.deepEqual(revoked, ["blob:kept"]);
  cache.release("review-15");
  assert.deepEqual(revoked, ["blob:kept"]);
});

test("review thumbnails are owned by the image component", () => {
  const source = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");
  assert.match(source, /function AuthorizedThumbnail/);
  assert.match(source, /thumbnailUrls\.hold\(photoKey, blob\)/);
  assert.match(source, /thumbnailUrls\.release\(photoKey\)/);
  assert.doesNotMatch(source, /reviewPhotoUrls/);
  assert.doesNotMatch(source, /pendingReviewPhotoKey/);
});
