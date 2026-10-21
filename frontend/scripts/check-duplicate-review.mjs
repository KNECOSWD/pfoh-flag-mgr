import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import {
  DuplicateReviewBoard,
  MergeActions,
  ResultingRecord,
  SharedFieldChoices,
  UseMatchingButton,
  applyMatchingValues,
  blankChoice,
  buildMergeRequest,
  canConfirmMerge,
  combineAwardLists,
  mergeFields,
  previewDraft,
  previewRows,
  previewValue,
  recordsDiffer,
  survivorIdFromChoices
} from "../src/duplicateReview.js";
import { MergeHistoryScreen } from "../src/mergeHistory.js";

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
const included = [1, 2];
const identicalKeys = ["firstName", "middleName", "lastName", "nickname"];
const differingKeys = mergeFields.map((field) => field.key).filter((key) => !identicalKeys.includes(key));

function choice(overrides = {}) {
  return { ...blankChoice(), explicit: true, ...overrides };
}

function reviewMarkup(choices = {}) {
  return renderToStaticMarkup(
    createElement(
      "div",
      null,
      createElement(UseMatchingButton, { disabled: false, onClick: () => {} }),
      createElement(SharedFieldChoices, {
        includedCount: 2,
        choices,
        onNoPhoto: () => {},
        onCombine: () => {}
      }),
      createElement(DuplicateReviewBoard, {
        honorees,
        includedIds: included,
        choices,
        onToggleIncluded: () => {},
        onChooseSource: () => {}
      }),
      createElement(ResultingRecord, {
        honorees,
        choices,
        onEdit: () => {},
        onGrid: () => {}
      }),
      createElement(MergeActions, {
        ready: canConfirmMerge(included, honorees, choices),
        busy: false,
        onMerge: () => {},
        onPreview: () => {}
      })
    )
  );
}

function tags(html, name) {
  return [...html.matchAll(/<input\b[^>]*>/g)]
    .map((match) => match[0])
    .filter((tag) => tag.includes(`name="${name}"`));
}

function buttons(html) {
  return [...html.matchAll(/<button\b[^>]*>[\s\S]*?<\/button>/g)].map((match) => match[0]);
}

function fillFrom(sourceId) {
  const choices = {};
  for (const field of mergeFields) {
    choices[field.key] = choice({ sourceId });
  }
  return choices;
}

test("every field has a select on both records and nothing is preselected", () => {
  assert.equal(recordsDiffer(["Jr.", "Sr."]), true);
  assert.equal(recordsDiffer(["Army", "Army"]), false);
  assert.equal(mergeFields.length, 17);

  const html = reviewMarkup();
  assert.match(html, /Side-by-side duplicate review/);
  assert.match(html, /Use all matching values/);
  assert.match(html, /John Q Smith Jr\./);
  assert.match(html, /John Q Smith Sr\./);
  assert.match(html, /No photo/);
  assert.match(html, /Combine both/);
  assert.doesNotMatch(html, /surviving record/i);
  assert.doesNotMatch(html, /name="survivor"/);

  for (const field of mergeFields) {
    const radios = tags(html, `field-${field.key}`).filter((tag) => tag.includes('value="1"') || tag.includes('value="2"'));
    assert.equal(radios.length, 2, `${field.key} has a select on both records`);
    assert.equal(radios.filter((tag) => tag.includes("checked")).length, 0, `${field.key} is not preselected`);
    assert.equal(radios.filter((tag) => tag.includes("disabled")).length, 0, `${field.key} can be chosen on an included record`);

    const block = html.split(`data-field="${field.key}"`)[1]?.split("data-field=")[0] ?? "";
    if (differingKeys.includes(field.key)) {
      assert.match(block, />differs</, `${field.label} keeps the differs label`);
    } else {
      assert.doesNotMatch(block, />differs</, `${field.label} matches and has no differs label`);
    }
  }

  assert.equal(tags(html, "field-photo").filter((tag) => tag.includes('value="none"') && tag.includes("checked")).length, 0);
  assert.equal(tags(html, "field-awards").filter((tag) => tag.includes('value="combine"') && tag.includes("checked")).length, 0);
  assert.equal(canConfirmMerge(included, honorees, {}), false);
  assert.ok(buttons(html).filter((tag) => tag.includes("Merge") || tag.includes("Preview card PDF")).every((tag) => tag.includes("disabled")));
});

test("use all matching values fills only identical fields", () => {
  const prior = { suffix: choice({ sourceId: 2 }) };
  const filled = applyMatchingValues(honorees, prior);
  for (const key of identicalKeys) {
    assert.equal(filled[key].sourceId, 1, `${key} takes the shared value`);
    assert.equal(filled[key].useCustom, false);
  }
  for (const key of differingKeys) {
    if (key === "suffix") {
      assert.equal(filled.suffix.sourceId, 2, "a differing field that was already chosen stays chosen");
      continue;
    }
    assert.equal(filled[key], undefined, `${key} differs and stays empty`);
  }

  const html = reviewMarkup(applyMatchingValues(honorees, {}));
  for (const key of identicalKeys) {
    const radios = tags(html, `field-${key}`);
    assert.equal(radios.filter((tag) => tag.includes('value="1"') && tag.includes("checked")).length, 1);
    assert.equal(radios.filter((tag) => tag.includes('value="2"') && tag.includes("checked")).length, 0);
  }
  for (const key of differingKeys) {
    assert.equal(tags(html, `field-${key}`).filter((tag) => tag.includes("checked")).length, 0, `${key} stays unselected`);
  }
  assert.equal(canConfirmMerge(included, honorees, applyMatchingValues(honorees, {})), false);
});

test("use all matching values does not choose the flag grid when the grids match", () => {
  const sameGrid = [{ ...left, flagGridName: "AA-1" }, { ...right, flagGridName: "AA-1" }];
  const bothBlank = [{ ...left, flagGridName: "" }, { ...right, flagGridName: "" }];
  for (const pair of [sameGrid, bothBlank]) {
    const filled = applyMatchingValues(pair, {});
    assert.equal(filled.flagGrid, undefined);
    assert.equal(survivorIdFromChoices(filled, [1, 2]), null);
    const readyExceptGrid = { ...filled };
    for (const field of mergeFields) {
      if (field.key === "flagGrid" || readyExceptGrid[field.key]) continue;
      readyExceptGrid[field.key] = choice({ sourceId: 1 });
    }
    assert.equal(canConfirmMerge([1, 2], pair, readyExceptGrid), false);
    readyExceptGrid.flagGrid = choice({ sourceId: 2 });
    assert.equal(survivorIdFromChoices(readyExceptGrid, [1, 2]), 2);
    assert.equal(canConfirmMerge([1, 2], pair, readyExceptGrid), true);
  }
});

test("use all matching values does not overwrite a custom or explicit choice", () => {
  const pair = [
    { ...left, rank: "Sgt", awards: "Bronze Star" },
    { ...right, rank: "Sgt", awards: "Bronze Star" }
  ];
  const prior = {
    firstName: choice({ useCustom: true, sourceId: null, custom: "Patricia Ann" }),
    rank: choice({ sourceId: 2 }),
    awards: choice({ combine: true, sourceId: null })
  };
  const filled = applyMatchingValues(pair, prior);
  assert.equal(filled.firstName.custom, "Patricia Ann");
  assert.equal(filled.firstName.useCustom, true);
  assert.equal(filled.firstName.sourceId, null);
  assert.equal(filled.rank.sourceId, 2);
  assert.equal(filled.awards.combine, true);
  assert.equal(filled.middleName.sourceId, 1);
  assert.equal(filled.flagGrid, undefined);
  assert.equal(filled.nickname.sourceId, 1);
});

test("the survivor is the flag-grid row, including a blank grid, and merge waits for every field", () => {
  const blank = { ...right, flagGridName: "" };
  const choices = fillFrom(1);
  choices.flagGrid = choice({ sourceId: 2 });
  assert.equal(survivorIdFromChoices(choices, included), 2);
  assert.equal(buildMergeRequest([left, blank], choices).survivorId, 2);
  assert.equal(buildMergeRequest([left, blank], choices).fields.flagGrid, 2);
  assert.equal(survivorIdFromChoices(choices, [1]), null);
  assert.equal(canConfirmMerge(included, [left, blank], choices), true);

  const missingNickname = fillFrom(2);
  delete missingNickname.nickname;
  assert.equal(canConfirmMerge(included, honorees, missingNickname), false);
  const blocked = renderToStaticMarkup(
    createElement(MergeActions, { ready: false, busy: false, onMerge: () => {}, onPreview: () => {} })
  );
  assert.ok(buttons(blocked).every((tag) => tag.includes("disabled")));

  const ready = reviewMarkup(choices);
  const mergeButtons = buttons(ready).filter((tag) => tag.includes("Merge") || tag.includes("Preview card PDF"));
  assert.equal(mergeButtons.length, 2);
  assert.ok(mergeButtons.every((tag) => !tag.includes("disabled")));
  const gridRadios = tags(ready, "field-flagGrid").filter((tag) => tag.includes('value="1"') || tag.includes('value="2"'));
  assert.equal(gridRadios.filter((tag) => tag.includes('value="2"') && tag.includes("checked")).length, 1);
});

test("the preview normalizes typed text the same way the save request does", () => {
  const choices = fillFrom(2);
  choices.firstName = choice({ useCustom: true, sourceId: null, custom: "Patricia  Ann" });
  const field = mergeFields.find((item) => item.key === "firstName");
  const preview = previewValue(field, honorees, choices.firstName);
  assert.equal(preview.saved, "Patricia Ann");
  assert.equal(preview.input, "Patricia Ann");
  assert.equal(previewDraft("Patricia ", false), "Patricia ");
  assert.equal(previewDraft("Line one\n", true), "Line one\n");
  assert.equal(preview.custom, true);
  const rows = previewRows(honorees, choices);
  assert.equal(rows.find((row) => row.key === "firstName").value, "Patricia Ann");

  const body = buildMergeRequest(honorees, choices);
  assert.equal(body.survivorId, 2);
  assert.deepEqual(body.duplicateIds, [1, 2]);
  assert.equal(body.values.firstName, preview.saved);
  assert.equal(body.fields.firstName, undefined);
  assert.equal(canConfirmMerge(included, honorees, choices), true);

  const html = reviewMarkup(choices);
  assert.match(html, /data-preview="firstName"[^>]*data-custom="true"/);
  assert.match(html, /class="customTag">custom</);
  assert.match(html, /value="Patricia Ann"/);

  const tooLong = {
    ...choices,
    firstName: choice({ useCustom: true, sourceId: null, custom: "A".repeat(201) })
  };
  const blocked = previewRows(honorees, tooLong).find((row) => row.key === "firstName");
  assert.equal(blocked.value, "First name must be 200 characters or fewer.");
  assert.equal(canConfirmMerge(included, honorees, tooLong), false);

  const noPhoto = fillFrom(2);
  noPhoto.photo = choice({ noPhoto: true, sourceId: null });
  const photoBody = buildMergeRequest(honorees, noPhoto);
  assert.equal(photoBody.values.photo, "");
  assert.equal(photoBody.fields.photo, undefined);
  assert.equal(previewValue(mergeFields.find((item) => item.key === "photo"), honorees, noPhoto.photo).display, "No photo");

  assert.equal(combineAwardLists(["Bronze Star", "Purple Heart\nBronze  Star"]), "Bronze Star\nPurple Heart");
  const combined = fillFrom(2);
  delete combined.awards;
  combined.awards = choice({ combine: true, sourceId: null });
  const combinedBody = buildMergeRequest(honorees, combined);
  assert.equal(combinedBody.combineAwards, true);
  assert.equal(combinedBody.fields.awards, undefined);
  assert.equal(combinedBody.values, undefined);

  const editedAwards = {
    ...combined,
    awards: choice({ useCustom: true, sourceId: null, custom: "  Medal of Honor\r\nSilver Star  " })
  };
  assert.equal(buildMergeRequest(honorees, editedAwards).values.awards, "Medal of Honor\nSilver Star");
  assert.equal(buildMergeRequest(honorees, editedAwards).combineAwards, undefined);

  const customGrid = fillFrom(2);
  customGrid.flagGrid = choice({ sourceId: 2, useCustom: true, custom: "AA-10" });
  const gridHtml = reviewMarkup(customGrid);
  const gridRadios = tags(gridHtml, "field-flagGrid").filter((tag) => tag.includes('value="2"'));
  assert.equal(gridRadios.filter((tag) => tag.includes("checked")).length, 1);
  assert.match(gridHtml, /aria-label="Flag grid"/);
  assert.match(gridHtml, /placeholder="AA-10"/);
  assert.equal(buildMergeRequest(honorees, customGrid).fields.flagGrid, 2);
  assert.equal(buildMergeRequest(honorees, customGrid).values.flagGrid, "AA-10");

  const css = readFileSync(new URL("../src/style.css", import.meta.url), "utf8");
  const stacked = css.slice(css.indexOf("@media (max-width: 720px)"));
  assert.match(stacked, /\.mergeBoard\s*\{[^}]*grid-template-columns:\s*minmax\(0,\s*1fr\)/);
  assert.match(stacked, /\.resultField\s*\{[^}]*grid-template-columns:\s*minmax\(0,\s*1fr\)/);
});

test("merge history lists newest first and shows when undo is blocked", () => {
  const older = {
    id: 9,
    survivorName: "Mary Ann Lee",
    absorbed: [{ id: 5, name: "Mary A Lee" }],
    mergedBy: "earlier@example.com",
    mergedUtc: "2026-10-18T11:30:00Z",
    undoneBy: "admin@example.com",
    undoneUtc: "2026-10-19T09:00:00Z"
  };
  const newer = {
    id: 12,
    survivorName: "John Q Smith Sr.",
    absorbed: [{ id: 1, name: "John Q Smith Jr." }],
    mergedBy: "admin@example.com",
    mergedUtc: "2026-10-21T15:04:00Z",
    undoneBy: null,
    undoneUtc: null
  };
  const html = renderToStaticMarkup(
    createElement(MergeHistoryScreen, {
      records: [older, newer],
      loaded: true,
      loadError: "",
      busy: false,
      error: "",
      onUndo() {},
      onBack() {},
      onReload() {}
    })
  );
  assert.ok(html.indexOf("John Q Smith Sr.") < html.indexOf("Mary Ann Lee"));
  assert.match(html, /John Q Smith Jr\./);
  assert.match(html, /admin@example.com/);
  assert.match(html, />Undo</);
  assert.match(html, /Undone /);

  const blocked = renderToStaticMarkup(
    createElement(MergeHistoryScreen, {
      records: [newer],
      loaded: true,
      loadError: "",
      busy: false,
      error: "This merge cannot be undone because the surviving record was edited after the merge.",
      onUndo() {},
      onBack() {},
      onReload() {}
    })
  );
  assert.match(blocked, /role="alert"/);
  assert.match(blocked, /edited after the merge/);
});

test("merge history is linked from duplicates and stays off the admin nav", () => {
  const app = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");
  const nav = app.match(/const adminNav: \{ route: AppRoute; label: string \}\[\] = \[[\s\S]*?\];/);
  assert.ok(nav);
  assert.doesNotMatch(nav[0], /history|Merge history/);
  assert.match(app, /onOpenHistory=\{\(\) => navigateToRoute\("\/admin\/duplicates\/history"\)\}/);
  assert.match(app, /isMergeHistoryRoute \? \([\s\S]*<MergeHistory/);
  assert.doesNotMatch(readFileSync(new URL("../src/publicRoutes.js", import.meta.url), "utf8"), /duplicates\/history/);
  const loadAdmin = app.slice(app.indexOf("async function loadAdminData"), app.indexOf("async function loadData"));
  assert.doesNotMatch(loadAdmin, /mergeHistory|duplicates\/merges/);
});
