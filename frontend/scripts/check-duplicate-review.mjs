import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import {
  DuplicateCustomFields,
  DuplicateReviewBoard,
  MergePreview,
  buildMergeRequest,
  canConfirmMerge,
  choicesForSurvivor,
  previewRows,
  recordsDiffer
} from "../src/duplicateReview.js";

const left = {
  id: 1,
  name: "John Q Smith Jr.",
  firstName: "John",
  middleName: "Q",
  lastName: "Smith",
  suffix: "Jr.",
  nickname: "",
  rank: "Sgt",
  serviceBranchName: "Army",
  serviceYears: "1942 - 1945",
  conflictsServed: "WWII",
  awards: "Bronze Star",
  description: "Photo bio",
  kia: false,
  photoFileName: "john.jpg",
  photoUrl: "/api/honorees/1/photo",
  flagGridName: "AA-1",
  phoneNumber: "214-555-0101",
  emailAddress: "one@example.com",
  sponsorName: "Ada",
  claimants: []
};

const right = {
  ...left,
  id: 2,
  name: "John Q Smith Sr.",
  suffix: "Sr.",
  rank: "Cpl",
  serviceBranchName: "Navy",
  serviceYears: "1950 - 1953",
  conflictsServed: "Korea",
  awards: "Purple Heart",
  description: "Kept bio",
  kia: true,
  photoFileName: "",
  photoUrl: null,
  flagGridName: "AA-2",
  phoneNumber: "214-555-0199",
  emailAddress: "two@example.com",
  sponsorName: "Bea"
};

const honorees = [left, right];

function reviewMarkup({ survivorId = null, includedIds = [], choices = {}, preview = null } = {}) {
  return renderToStaticMarkup(
    createElement(
      "div",
      null,
      createElement(DuplicateReviewBoard, {
        honorees,
        includedIds,
        survivorId,
        choices,
        onToggleIncluded: () => {},
        onSurvivor: () => {},
        onChooseSource: () => {},
        onCustom: () => {}
      }),
      createElement(DuplicateCustomFields, {
        survivorId,
        choices,
        onCustom: () => {}
      }),
      createElement(MergePreview, { rows: preview })
    )
  );
}

function tags(html, name) {
  return [...html.matchAll(/<input\b[^>]*>/g)]
    .map((match) => match[0])
    .filter((tag) => tag.includes(`name="${name}"`));
}

test("side-by-side review shows names, photos, and differs labels with nothing selected", () => {
  assert.equal(recordsDiffer(["Jr.", "Sr."]), true);
  assert.equal(recordsDiffer(["Army", "Army"]), false);

  const html = reviewMarkup();
  assert.match(html, /Side-by-side duplicate review/);
  assert.match(html, /John Q Smith Jr\./);
  assert.match(html, /John Q Smith Sr\./);
  assert.match(html, /class="duplicateNoPhoto"/);
  assert.match(html, /No photo/);
  assert.match(html, /src="\/api\/honorees\/1\/photo"/);
  assert.match(html, /Photo bio/);
  assert.match(html, /Kept bio/);
  assert.match(html, /Army/);
  assert.match(html, /Navy/);
  assert.match(html, /1942 - 1945/);
  assert.match(html, /1950 - 1953/);
  assert.match(html, /Bronze Star/);
  assert.match(html, /Purple Heart/);

  for (const field of ["fullName", "photo", "suffix", "description", "serviceBranch", "serviceYears", "awards", "rank"]) {
    const block = html.split(`data-field="${field}"`)[1]?.split("data-field=")[0] ?? "";
    assert.match(block, />differs</, `${field} is marked differs`);
  }

  assert.equal(html.includes("checked"), false);
  assert.match(html, /Choose a surviving record to preview the merged values/);
  assert.equal(canConfirmMerge(null, []), false);
  assert.match(tags(html, "field-firstName")[0], /disabled/);
  assert.doesNotMatch(html, /data-custom="photo"/);
  assert.doesNotMatch(html, /data-custom="awards"/);
  assert.match(html, /data-custom="firstName"/);
});

test("choosing a survivor selects that record's fields and a custom value replaces one", () => {
  const choices = choicesForSurvivor(2);
  const selected = reviewMarkup({
    survivorId: 2,
    includedIds: [1, 2],
    choices,
    preview: previewRows(honorees, choices)
  });
  const description = tags(selected, "field-description");
  assert.equal(description.filter((tag) => tag.includes('value="2"') && tag.includes("checked")).length, 1);
  assert.equal(description.filter((tag) => tag.includes('value="1"') && tag.includes("checked")).length, 0);
  assert.match(selected, /data-preview="description"[\s\S]*Kept bio/);
  assert.match(selected, /data-preview="photo"[\s\S]*No photo/);
  assert.equal(canConfirmMerge(2, [1, 2]), true);

  const customChoices = {
    ...choices,
    firstName: { useCustom: true, sourceId: null, custom: "Patricia" }
  };
  const custom = reviewMarkup({
    survivorId: 2,
    includedIds: [1, 2],
    choices: customChoices,
    preview: previewRows(honorees, customChoices)
  });
  const firstName = tags(custom, "field-firstName");
  assert.equal(firstName.filter((tag) => tag.includes('value="custom"') && tag.includes("checked")).length, 1);
  assert.equal(firstName.filter((tag) => tag.includes("checked") && !tag.includes('value="custom"')).length, 0);
  assert.match(custom, /data-preview="firstName"[\s\S]*Patricia/);
  assert.match(custom, /value="Patricia"/);

  const body = buildMergeRequest(2, honorees, customChoices);
  assert.equal(body.survivorId, 2);
  assert.deepEqual(body.duplicateIds, [1, 2]);
  assert.equal(body.values.firstName, "Patricia");
  assert.equal(body.fields.firstName, undefined);
  assert.equal(body.fields.photo, 2);
  assert.equal(body.fields.awards, 2);
  assert.equal(body.values.photo, undefined);
  assert.equal(body.values.awards, undefined);
  assert.equal(body.fields.description, 2);
});
