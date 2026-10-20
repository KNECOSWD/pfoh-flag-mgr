import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { thumbnailLoadPlan } from "../src/authorizedThumbnail.js";

const app = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");

test("AuthorizedThumbnail fetches once per image across re-renders and revokes on change", () => {
  const fetches = [];
  const revoked = [];
  let previous = null;

  function render(props) {
    const plan = thumbnailLoadPlan(previous, props);
    if (!plan.load) return;
    if (plan.releasePrevious && previous) {
      revoked.push(previous.key);
      previous = { ...previous, held: false };
    }
    fetches.push(plan.key);
    previous = { key: plan.key, held: true };
  }

  const account = () => ({ homeAccountId: "same-user", object: {} });
  const instance = () => ({ id: Math.random() });
  const image = { photoKey: "review-15", url: "/api/flagclaims/change-requests/15/photo" };
  render({ ...image, account: account(), instance: instance() });
  render({ ...image, account: account(), instance: instance() });
  render({ ...image, account: account(), instance: instance() });
  render({ ...image, account: account(), instance: instance() });
  assert.deepEqual(fetches, ["review-15\n/api/flagclaims/change-requests/15/photo"]);
  assert.deepEqual(revoked, []);

  render({
    photoKey: "review-16",
    url: "/api/flagclaims/change-requests/16/photo",
    account: account(),
    instance: instance()
  });
  assert.equal(fetches.length, 2);
  assert.deepEqual(revoked, ["review-15\n/api/flagclaims/change-requests/15/photo"]);
});

test("the thumbnail effect depends on the image and releases the object url", () => {
  const start = app.indexOf("function AuthorizedThumbnail");
  const effect = app.slice(start, app.indexOf("if (!src && !failed)", start));
  assert.match(effect, /thumbnailLoadKey\(photoKey, url\)/);
  assert.match(effect, /\}, \[loadKey\]\)/);
  assert.match(effect, /thumbnailUrls\.release\(photoKey\)/);
  assert.doesNotMatch(effect, /\}, \[account, instance, photoKey, url\]\)/);
});
