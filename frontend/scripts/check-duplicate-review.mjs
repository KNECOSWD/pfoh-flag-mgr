import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import {
  DuplicateReviewBoard,
  MergeActions,
  ResultingRecord,
  ReviewStepActions,
  SharedFieldChoices,
  StillToChoose,
  UseMatchingButton,
  activeFlashKeys,
  applyMatchingValues,
  blankChoice,
  buildMergeRequest,
  canConfirmMerge,
  changedPreviewKeys,
  choicesAfterIncludedChange,
  combineAwardLists,
  mergeFields,
  previewDraft,
  previewNotePlacement,
  previewRows,
  previewUpdateText,
  previewValue,
  recordsDiffer,
  schedulePreviewFlash,
  shouldShowPreviewNote,
  stillToChooseHint,
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

function inputChecked(tag) {
  return /(?:^|\s)checked(?:=|\s|>|\/)/.test(tag);
}

function previewBlock(html, key) {
  const marker = `data-preview="${key}"`;
  const start = html.indexOf(marker);
  if (start < 0) return "";
  const tagStart = html.lastIndexOf("<", start);
  const nextMarker = html.indexOf("data-preview=", start + marker.length);
  if (nextMarker < 0) return html.slice(tagStart);
  return html.slice(tagStart, html.lastIndexOf("<", nextMarker));
}

function contrast(foreground, background) {
  const luminance = (hex) => {
    const channels = [0, 1, 2].map((index) => {
      const value = parseInt(hex.slice(1 + index * 2, 3 + index * 2), 16) / 255;
      return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
    });
    return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
  };
  const lighter = Math.max(luminance(foreground), luminance(background));
  const darker = Math.min(luminance(foreground), luminance(background));
  return (lighter + 0.05) / (darker + 0.05);
}

function fillFrom(sourceId) {
  const choices = {};
  for (const field of mergeFields) {
    choices[field.key] = choice({ sourceId });
  }
  return choices;
}

test("every field has a select on both records and only exact matches are preselected", () => {
  assert.equal(recordsDiffer(["Jr.", "Sr."]), true);
  assert.equal(recordsDiffer(["Army", "Army"]), false);
  assert.equal(mergeFields.length, 17);

  const choices = choicesAfterIncludedChange(honorees, {});
  const html = reviewMarkup(choices);
  assert.match(html, /Side-by-side duplicate review/);
  assert.match(html, /Use matches/);
  assert.match(html, /John Q Smith Jr\./);
  assert.match(html, /John Q Smith Sr\./);
  assert.match(html, /No photo/);
  assert.match(html, /Combine both/);
  assert.doesNotMatch(html, /surviving record/i);
  assert.doesNotMatch(html, /name="survivor"/);

  for (const field of mergeFields) {
    const radios = tags(html, `field-${field.key}`).filter((tag) => tag.includes('value="1"') || tag.includes('value="2"'));
    assert.equal(radios.length, 2, `${field.key} has a select on both records`);
    const checked = radios.filter(inputChecked);
    if (identicalKeys.includes(field.key)) {
      assert.equal(checked.length, 1, `${field.key} is preselected`);
      assert.equal(choices[field.key].sourceId, 1);
      assert.equal(choices[field.key].explicit, false);
      assert.equal(choices[field.key].useCustom, false);
      assert.match(checked[0], /value="1"/);
      assert.match(checked[0], /aria-checked="true"/);
      assert.match(html, new RegExp(`class="mergeChoice isSelected"><input[^>]*name="field-${field.key}"[^>]*value="1"`));
    } else {
      assert.equal(checked.length, 0, `${field.key} is not preselected`);
      assert.equal(choices[field.key], undefined);
    }
    assert.equal(radios.filter((tag) => tag.includes("disabled")).length, 0, `${field.key} can be chosen on an included record`);

    const block = html.split(`data-field="${field.key}"`)[1]?.split("data-field=")[0] ?? "";
    if (differingKeys.includes(field.key)) {
      assert.match(block, />differs</, `${field.label} keeps the differs label`);
    } else {
      assert.doesNotMatch(block, />differs</, `${field.label} matches and has no differs label`);
    }
  }

  assert.equal(tags(html, "field-photo").filter((tag) => tag.includes('value="none"') && inputChecked(tag)).length, 0);
  assert.equal(tags(html, "field-awards").filter((tag) => tag.includes('value="combine"') && inputChecked(tag)).length, 0);
  assert.equal(choices.flagGrid, undefined);
  assert.equal(canConfirmMerge(included, honorees, choices), false);
  assert.ok(buttons(html).filter((tag) => tag.includes(">Merge<") || tag.includes("Preview PDF")).every((tag) => tag.includes("disabled")));
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
    assert.equal(radios.filter((tag) => tag.includes('value="1"') && inputChecked(tag)).length, 1);
    assert.equal(radios.filter((tag) => tag.includes('value="2"') && inputChecked(tag)).length, 0);
  }
  for (const key of differingKeys) {
    assert.equal(tags(html, `field-${key}`).filter(inputChecked).length, 0, `${key} stays unselected`);
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
  const mergeButtons = buttons(ready).filter((tag) => tag.includes(">Merge<") || tag.includes("Preview PDF"));
  assert.equal(mergeButtons.length, 2);
  assert.ok(mergeButtons.every((tag) => !tag.includes("disabled")));
  const gridRadios = tags(ready, "field-flagGrid").filter((tag) => tag.includes('value="1"') || tag.includes('value="2"'));
  assert.equal(gridRadios.filter((tag) => tag.includes('value="2"') && inputChecked(tag)).length, 1);
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
  assert.equal(gridRadios.filter(inputChecked).length, 1);
  assert.match(gridHtml, /aria-label="Flag grid"/);
  assert.match(gridHtml, /placeholder="AA-10"/);
  assert.equal(buildMergeRequest(honorees, customGrid).fields.flagGrid, 2);
  assert.equal(buildMergeRequest(honorees, customGrid).values.flagGrid, "AA-10");

  const css = readFileSync(new URL("../src/styles/admin-duplicates.css", import.meta.url), "utf8");
  const board = css.slice(css.indexOf(".mergeBoard {"), css.indexOf("@media (min-width: 761px)", css.indexOf(".mergeBoard {")));
  const resultField = css.slice(css.indexOf(".resultField {"), css.indexOf("@media (min-width: 761px)", css.indexOf(".resultField {")));
  assert.match(board, /grid-template-columns:\s*minmax\(0,\s*1fr\)/);
  assert.match(resultField, /grid-template-columns:\s*minmax\(0,\s*1fr\)/);
  assert.doesNotMatch(board, /!important/);
});

test("exact matches are preselected on load and the flag grid never is", () => {
  const loaded = choicesAfterIncludedChange(honorees, {});
  for (const key of identicalKeys) {
    assert.equal(loaded[key].sourceId, 1, `${key} is preselected`);
    assert.equal(loaded[key].explicit, false);
    assert.equal(loaded[key].useCustom, false);
  }
  for (const key of differingKeys) assert.equal(loaded[key], undefined, `${key} is not preselected`);
  assert.equal(loaded.flagGrid, undefined);

  const sameGrid = [{ ...left, flagGridName: "AA-1" }, { ...right, flagGridName: "AA-1" }];
  const bothBlank = [{ ...left, flagGridName: "" }, { ...right, flagGridName: "" }];
  for (const pair of [sameGrid, bothBlank]) {
    const filled = choicesAfterIncludedChange(pair, {});
    assert.equal(filled.flagGrid, undefined);
    assert.equal(survivorIdFromChoices(filled, [1, 2]), null);
    assert.equal(filled.firstName.sourceId, 1);
    assert.equal(filled.firstName.explicit, false);
  }

  const third = { ...left, id: 3, firstName: "Joan" };
  const widened = choicesAfterIncludedChange([left, right, third], loaded);
  assert.equal(widened.firstName, undefined, "a preselect is cleared when the values no longer match");
  assert.equal(widened.middleName.sourceId, 1);
  assert.equal(widened.flagGrid, undefined);
});

test("a preselected field can be changed or custom-edited and matching values do not overwrite it", () => {
  const loaded = choicesAfterIncludedChange(honorees, {});
  const changed = {
    ...loaded,
    firstName: choice({ sourceId: 2 })
  };
  const kept = choicesAfterIncludedChange(honorees, changed);
  assert.equal(kept.firstName.sourceId, 2);
  assert.equal(kept.firstName.explicit, true);
  assert.equal(kept.firstName.useCustom, false);

  const custom = {
    ...loaded,
    firstName: choice({ useCustom: true, sourceId: null, custom: "Patricia Ann" })
  };
  const afterInclude = choicesAfterIncludedChange(honorees, custom);
  assert.equal(afterInclude.firstName.useCustom, true);
  assert.equal(afterInclude.firstName.custom, "Patricia Ann");
  assert.equal(afterInclude.firstName.sourceId, null);
  const matched = applyMatchingValues(honorees, custom);
  assert.equal(matched.firstName.custom, "Patricia Ann");
  assert.equal(matched.firstName.useCustom, true);
  assert.equal(matched.firstName.sourceId, null);
  assert.equal(matched.middleName.sourceId, 1);
  assert.equal(matched.flagGrid, undefined);

  const html = reviewMarkup(changed);
  const firstName = tags(html, "field-firstName").filter((tag) => tag.includes('value="1"') || tag.includes('value="2"'));
  assert.equal(firstName.filter((tag) => tag.includes('value="2"') && inputChecked(tag)).length, 1);
  assert.equal(firstName.filter((tag) => tag.includes('value="1"') && inputChecked(tag)).length, 0);
  assert.match(html, /class="mergeChoice isSelected"><input[^>]*name="field-firstName"[^>]*value="2"/);
  assert.match(html, /aria-checked="true"/);
});

test("the review step previews the card and names only the fields still to choose", () => {
  const screen = readFileSync(new URL("../src/DuplicateMerge.tsx", import.meta.url), "utf8");
  const review = screen.slice(screen.indexOf('step === "review"'), screen.indexOf('step === "confirm"'));
  const confirm = screen.slice(screen.indexOf('step === "confirm"'), screen.indexOf('step === "done"'));
  assert.match(review, /ReviewStepActions/);
  assert.match(review, /previewCard\(\)/);
  assert.match(review, /StillToChoose/);
  assert.match(confirm, /Preview PDF|onPreview=\{\(\) => void previewCard\(\)\}/);
  assert.match(review, /label="Back to groups"/);
  assert.match(confirm, /label="Back to review"/);
  assert.match(screen, />\s*Review\s*</);
  assert.match(screen, /DuplicateHistoryLink/);
  assert.match(screen, /label=\{busy \? "Loading" : "Refresh"\}/);
  assert.match(screen, />\s*Confirm\s*</);
  assert.doesNotMatch(screen, /Review side by side/);
  assert.doesNotMatch(screen, /Continue to confirm/);
  assert.doesNotMatch(screen, /Merge history/);
  assert.doesNotMatch(screen, /Confirm not duplicates</);
  const chrome = readFileSync(new URL("../src/duplicateChrome.js", import.meta.url), "utf8");
  assert.match(chrome, /"aria-label": label/);
  assert.match(chrome, /title: label/);
  assert.match(chrome, /"History"/);
  assert.match(confirm, /StillToChoose/);
  assert.match(screen, /Fields that match exactly are preselected/);
  assert.match(screen, /Everything else, including the flag grid, needs a pick/);
  assert.doesNotMatch(screen, /Nothing is selected for you/);
  assert.doesNotMatch(screen, /Nothing is selected automatically/);
  const open = screen.slice(screen.indexOf("function openGroup"), screen.indexOf("function toggleIncluded"));
  const toggle = screen.slice(screen.indexOf("function toggleIncluded"), screen.indexOf("function chooseSource"));
  assert.match(open, /choicesAfterIncludedChange/);
  assert.doesNotMatch(open, /flashChanges/);
  assert.match(toggle, /choicesAfterIncludedChange/);
  assert.doesNotMatch(toggle, /flashChanges/);
  assert.match(screen.slice(screen.indexOf("function chooseSource"), screen.indexOf("function chooseNoPhoto")), /flashChanges/);
  assert.match(screen.slice(screen.indexOf("function useMatching"), screen.indexOf("async function confirmNotDuplicates")), /flashChanges/);
  assert.match(screen.slice(screen.indexOf("function editPreview"), screen.indexOf("function editGrid")), /flashChanges/);

  const loaded = choicesAfterIncludedChange(honorees, {});
  const hint = stillToChooseHint(included, loaded);
  assert.match(hint, /^Still to choose: /);
  assert.match(hint, /\.$/);
  for (const key of identicalKeys) {
    const label = mergeFields.find((field) => field.key === key).label;
    assert.equal(hint.includes(label), false, `${label} is preselected and stays off the hint`);
  }
  assert.match(hint, /Flag grid/);
  assert.match(hint, /Photo/);
  assert.equal(hint, stillToChooseHint(included, loaded));

  const waiting = renderToStaticMarkup(
    createElement(
      "div",
      null,
      createElement(ReviewStepActions, { ready: false, busy: false, onPreview() {}, onContinue() {} }),
      createElement(StillToChoose, { includedIds: included, choices: loaded })
    )
  );
  const previewButtons = buttons(waiting).filter((tag) => tag.includes("Preview PDF"));
  const continueButtons = buttons(waiting).filter((tag) => tag.includes(">Continue<"));
  assert.equal(previewButtons.length, 1);
  assert.equal(continueButtons.length, 1);
  assert.match(previewButtons[0], /disabled/);
  assert.match(continueButtons[0], /disabled/);
  assert.match(waiting, /Still to choose:/);
  assert.doesNotMatch(waiting, /First name/);
  assert.match(waiting, /Flag grid/);

  const readyChoices = fillFrom(1);
  assert.equal(stillToChooseHint(included, readyChoices), "");
  const readyHtml = renderToStaticMarkup(
    createElement(
      "div",
      null,
      createElement(ReviewStepActions, { ready: true, busy: false, onPreview() {}, onContinue() {} }),
      createElement(StillToChoose, { includedIds: included, choices: readyChoices })
    )
  );
  assert.doesNotMatch(buttons(readyHtml).find((tag) => tag.includes("Preview PDF")), /disabled/);
  assert.doesNotMatch(readyHtml, /Still to choose/);
});

test("the resulting photo is a thumbnail, or No photo, or Not chosen", () => {
  const chosen = renderToStaticMarkup(
    createElement(ResultingRecord, {
      honorees,
      choices: { photo: choice({ sourceId: 1 }) },
      onEdit() {},
      onGrid() {}
    })
  );
  const photo = previewBlock(chosen, "photo");
  assert.match(photo, /<img /);
  assert.match(photo, /class="duplicatePhoto"/);
  assert.match(photo, /src="\/api\/honorees\/1\/photo"/);
  assert.match(photo, /alt="John Q Smith Jr\. photo"/);
  assert.doesNotMatch(photo, /john\.jpg/);

  const none = renderToStaticMarkup(
    createElement(ResultingRecord, {
      honorees,
      choices: { photo: choice({ noPhoto: true, sourceId: null }) },
      onEdit() {},
      onGrid() {}
    })
  );
  const noneBlock = previewBlock(none, "photo");
  assert.match(noneBlock, /No photo/);
  assert.doesNotMatch(noneBlock, /<img /);

  const emptyRecord = renderToStaticMarkup(
    createElement(ResultingRecord, {
      honorees,
      choices: { photo: choice({ sourceId: 2 }) },
      onEdit() {},
      onGrid() {}
    })
  );
  const emptyBlock = previewBlock(emptyRecord, "photo");
  assert.match(emptyBlock, /No photo/);
  assert.doesNotMatch(emptyBlock, /<img /);

  const unset = renderToStaticMarkup(
    createElement(ResultingRecord, { honorees, choices: {}, onEdit() {}, onGrid() {} })
  );
  assert.match(previewBlock(unset, "photo"), /Not chosen/);
  assert.doesNotMatch(previewBlock(unset, "photo"), /<img /);

  const css = readFileSync(new URL("../src/styles/admin-duplicates.css", import.meta.url), "utf8");
  assert.match(css, /\.resultControl \.duplicatePhoto\s*\{[^}]*max-width:\s*100%/);
  assert.match(css, /\.resultControl \.duplicateNoPhoto\s*\{[^}]*max-width:\s*100%/);
});

test("a changed resulting row highlights, then clears, and reduced motion does not animate", () => {
  const before = {};
  const after = { rank: choice({ sourceId: 2 }) };
  assert.deepEqual(changedPreviewKeys(honorees, before, after), ["rank"]);
  const flash = schedulePreviewFlash(["rank"], 0);
  assert.deepEqual(activeFlashKeys(flash, 0), ["rank"]);
  assert.deepEqual(activeFlashKeys(flash, 999), ["rank"]);
  assert.deepEqual(activeFlashKeys(flash, 1000), []);

  const sameGrid = [{ ...left, flagGridName: "AA-1" }, { ...right, flagGridName: "AA-1" }];
  const matched = applyMatchingValues(sameGrid, {});
  assert.equal(changedPreviewKeys(sameGrid, {}, matched).includes("flagGrid"), false);

  const lit = renderToStaticMarkup(
    createElement(ResultingRecord, {
      honorees,
      choices: after,
      litKeys: ["rank"],
      onEdit() {},
      onGrid() {}
    })
  );
  assert.match(previewBlock(lit, "rank"), /class="resultField resultFlash"/);
  assert.doesNotMatch(previewBlock(lit, "firstName"), /resultFlash/);
  const cleared = renderToStaticMarkup(
    createElement(ResultingRecord, { honorees, choices: after, litKeys: [], onEdit() {}, onGrid() {} })
  );
  assert.doesNotMatch(cleared, /resultFlash/);

  const source = readFileSync(new URL("../src/duplicateReview.js", import.meta.url), "utf8");
  const resulting = source.slice(source.indexOf("export function ResultingRecord"), source.indexOf("export function ReviewStepActions"));
  assert.match(resulting, /schedulePreviewFlash/);
  assert.match(resulting, /activeFlashKeys/);
  assert.match(resulting, /PREVIEW_FLASH_MS/);
  assert.match(resulting, /setLit\(\(current\) => current\.filter/);

  const css = readFileSync(new URL("../src/styles/admin-duplicates.css", import.meta.url), "utf8");
  assert.match(css, /\.resultField\.resultFlash\s*\{[^}]*animation:\s*resultFlash 1s/);
  assert.match(css, /\.resultField\.resultFlash\s*\{[\s\S]*prefers-reduced-motion:\s*reduce[\s\S]*animation:\s*none/);
  const selected = css.slice(css.indexOf(".mergeChoice.isSelected {"), css.indexOf(".mergeChoice.isSelected strong"));
  assert.match(selected, /background:\s*(?:#e7eef8|var\(--color-surface-muted\))/i);
  assert.match(selected, /color:\s*(?:#0A2342|#102f5f|var\(--color-navy-deep\))/i);
  assert.match(selected, /outline:[^;]*solid\s+(?:#0A2342|#102f5f|var\(--color-navy-deep\))/i);
  assert.doesNotMatch(selected, /background:\s*(?:#0A2342|#102f5f|var\(--color-navy-deep\))/i);
  assert.ok(contrast("#102f5f", "#f7f9fc") >= 4.5);
  assert.ok(contrast("#0A2342", "#E7EEF8") >= 4.5);
  assert.ok(contrast("#162033", "#FFF4D6") >= 4.5);
  assert.ok(contrast("#162033", "#FFE08A") >= 4.5);
});

test("the preview note names an off-screen field and stays out of the layout", () => {
  const viewport = { top: 0, left: 0, bottom: 800, right: 1280 };
  assert.equal(shouldShowPreviewNote({ top: 20, bottom: 80, left: 0, right: 400 }, viewport), false);
  assert.equal(shouldShowPreviewNote({ top: 860, bottom: 920, left: 0, right: 400 }, viewport), true);
  assert.equal(shouldShowPreviewNote(null, viewport), false);
  assert.equal(previewUpdateText("Email"), "Preview updated: Email");
  assert.equal(previewNotePlacement(null, 800), "top");
  assert.equal(previewNotePlacement({ top: 400, bottom: 460, left: 0, right: 240 }, 800), "top");
  assert.equal(previewNotePlacement({ top: 4, bottom: 48, left: 0, right: 240 }, 800), "offset");
  assert.equal(previewNotePlacement([{ top: 4, bottom: 48 }, { top: 70, bottom: 110 }], 800), "bottom");

  const shown = renderToStaticMarkup(
    createElement(ResultingRecord, {
      honorees,
      choices: {},
      notes: ["Email"],
      onEdit() {},
      onGrid() {}
    })
  );
  assert.match(shown, /aria-live="polite"/);
  assert.match(shown, /Preview updated: Email/);
  assert.match(shown, /class="previewUpdateNote"/);

  const hidden = renderToStaticMarkup(
    createElement(ResultingRecord, { honorees, choices: {}, notes: [], onEdit() {}, onGrid() {} })
  );
  assert.match(hidden, /aria-live="polite"/);
  assert.doesNotMatch(hidden, /Preview updated:/);

  const source = readFileSync(new URL("../src/duplicateReview.js", import.meta.url), "utf8");
  const resulting = source.slice(source.indexOf("export function ResultingRecord"), source.indexOf("export function ReviewStepActions"));
  assert.match(resulting, /getBoundingClientRect/);
  assert.match(resulting, /shouldShowPreviewNote/);
  assert.match(resulting, /PREVIEW_NOTE_MS/);
  assert.match(resulting, /previewNotePlacement/);
  const css = readFileSync(new URL("../src/styles/admin-duplicates.css", import.meta.url), "utf8");
  assert.match(css, /\.previewUpdateNote\s*\{[^}]*position:\s*fixed/);
  assert.match(css, /\.previewUpdateNote\.isOffset\s*\{[^}]*top:\s*4\.25rem/);
  assert.match(css, /\.previewUpdateNote\.isBottom\s*\{[^}]*bottom:\s*(?:0\.75rem|var\(--space-xs\))/);
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
  assert.match(html, /class="destructive"/);
  assert.match(html, /Undone /);
  assert.match(html, /aria-label="Back to duplicates"/);
  assert.match(html, /title="Back to duplicates"/);
  assert.match(html, /aria-label="Refresh"/);
  assert.match(html, /title="Refresh"/);
  assert.doesNotMatch(html, />Back to duplicates</);
  assert.doesNotMatch(html, />Refresh</);

  const blocked = renderToStaticMarkup(
    createElement(MergeHistoryScreen, {
      records: [newer],
      loaded: true,
      loadError: "",
      busy: false,
      error: "This merge cannot be undone because the surviving record was edited after the merge.",
      errorId: newer.id,
      onUndo() {},
      onBack() {},
      onReload() {}
    })
  );
  const row = blocked.slice(blocked.indexOf("<li"), blocked.indexOf("</li>") + 5);
  const beforeList = blocked.slice(0, blocked.indexOf("<ol"));
  assert.doesNotMatch(beforeList, /role="alert"/);
  assert.match(row, /class="destructive"/);
  assert.match(row, />Undo</);
  assert.ok(row.indexOf(">Undo<") < row.indexOf('role="alert"'));
  assert.match(row, /class="message error"/);
  assert.match(row, /edited after the merge/);
  const historyCss = readFileSync(new URL("../src/styles/admin-duplicates.css", import.meta.url), "utf8");
  assert.match(historyCss, /\.mergeHistoryActions\s*\{[^}]*align-items:\s*flex-end/);
  assert.match(historyCss, /button\.destructive\s*\{[^}]*color:\s*(?:#8b1e2c|var\(--color-crimson\))/);
  assert.equal(2.75 * 16, 44);
  assert.match(historyCss, /\.duplicateIconButton\s*\{[^}]*min-width:\s*2\.75rem/);
  assert.match(historyCss, /\.duplicateIconButton\s*\{[^}]*min-height:\s*2\.75rem/);
  assert.match(historyCss, /\.mergeStepFooterActions\s*\{[^}]*flex-direction:\s*row-reverse/);
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
