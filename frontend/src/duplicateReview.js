import { createElement as h, useEffect, useRef, useState } from "react";

export const mergeFields = [
  { key: "firstName", label: "First name", serverLabel: "First name", custom: "text", required: true, maxLength: 200, serverMax: 200, value: (honoree) => honoree.firstName },
  { key: "middleName", label: "Middle name", serverLabel: "Middle name", custom: "text", maxLength: 200, serverMax: 200, value: (honoree) => honoree.middleName || "" },
  { key: "lastName", label: "Last name", serverLabel: "Last name", custom: "text", required: true, maxLength: 200, serverMax: 200, value: (honoree) => honoree.lastName },
  { key: "suffix", label: "Suffix", serverLabel: "Suffix", custom: "text", maxLength: 100, serverMax: 100, value: (honoree) => honoree.suffix || "" },
  { key: "nickname", label: "Nickname", serverLabel: "Nickname", custom: "text", maxLength: 200, serverMax: 200, value: (honoree) => honoree.nickname || "" },
  { key: "rank", label: "Rank", serverLabel: "Rank", custom: "text", maxLength: 200, serverMax: 200, value: (honoree) => honoree.rank },
  { key: "serviceBranch", label: "Service branch", serverLabel: "Service branch", custom: "text", required: true, maxLength: 200, serverMax: 200, placeholder: "Existing branch name", value: (honoree) => honoree.serviceBranchName || "" },
  { key: "serviceYears", label: "Years of service", serverLabel: "Service years", custom: "text", maxLength: 500, serverMax: 500, placeholder: "1942 - 1945", value: (honoree) => honoree.serviceYears },
  { key: "conflictsServed", label: "Conflicts served", serverLabel: "Conflicts served", custom: "textarea", value: (honoree) => honoree.conflictsServed },
  { key: "awards", label: "Awards", serverLabel: "Awards", custom: "textarea", value: (honoree) => honoree.awards },
  { key: "description", label: "Bio", serverLabel: "Honoree description", custom: "textarea", required: true, value: (honoree) => honoree.description },
  { key: "kia", label: "Killed in action", serverLabel: "Killed in action", custom: "text", maxLength: 5, placeholder: "Yes or No", value: (honoree) => (honoree.kia ? "Yes" : "No") },
  { key: "photo", label: "Photo", custom: false, value: (honoree) => honoree.photoFileName || "" },
  { key: "flagGrid", label: "Flag grid", serverLabel: "Flag grid", custom: "text", picker: true, maxLength: 100, serverMax: 100, placeholder: "AA-10", value: (honoree) => honoree.flagGridName || "" },
  { key: "phone", label: "Phone", serverLabel: "Phone", custom: "text", maxLength: 50, serverMax: 50, value: (honoree) => honoree.phoneNumber },
  { key: "email", label: "Email", serverLabel: "Email", custom: "text", maxLength: 255, serverMax: 255, value: (honoree) => honoree.emailAddress },
  { key: "sponsor", label: "Sponsor", serverLabel: "Sponsor", custom: "text", maxLength: 400, serverMax: 400, placeholder: "Existing sponsor name", value: (honoree) => honoree.sponsorName }
];

export function fullName(honoree) {
  return [honoree.firstName, honoree.middleName, honoree.lastName, honoree.suffix]
    .map((part) => String(part ?? "").trim())
    .filter(Boolean)
    .join(" ");
}

export function normalizeCompare(value) {
  return String(value ?? "").replace(/\s+/g, " ").trim();
}

export function recordsDiffer(values) {
  return new Set(values.map(normalizeCompare)).size > 1;
}

// Same calendar date the pending-review queue uses for a submission.
export function formatSubmissionDate(value) {
  if (value == null || value === "") return "";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return String(value);
  return new Intl.DateTimeFormat(undefined, {
    year: "numeric",
    month: "short",
    day: "numeric"
  }).format(date);
}

export function submissionDateLabel(value) {
  const formatted = formatSubmissionDate(value);
  return formatted ? `Submitted ${formatted}` : "";
}

function isControlCharacter(character) {
  const code = character.codePointAt(0);
  return code <= 0x1f || (code >= 0x7f && code <= 0x9f);
}

// Same text cleanup the merge API applies before it saves a typed value.
export function normalizeMergeText(value, multiline) {
  if (value == null || value === "") return "";
  let buffer = "";
  const source = String(value).replace(/\r\n/g, "\n").replace(/\r/g, "\n");
  for (const character of source) {
    if (character === "\n" || character === "\t") {
      buffer += multiline && character === "\n" ? "\n" : " ";
      continue;
    }
    if (isControlCharacter(character)) continue;
    buffer += character;
  }
  let text = buffer.trim();
  if (!multiline) {
    while (text.includes("  ")) text = text.replaceAll("  ", " ");
  }
  return text;
}

// The saved text is normalized, and a single trailing space or newline stays
// so the next word or line can be typed. A finished value matches the save.
export function previewDraft(raw, multiline) {
  const saved = normalizeMergeText(raw, multiline);
  if (multiline && raw === `${saved}\n`) return raw;
  if (!multiline && raw === `${saved} `) return raw;
  return saved;
}

export function canonicalKia(value) {
  const text = normalizeMergeText(value, false).toLowerCase();
  if (text === "yes" || text === "true" || text === "y") return "Yes";
  if (text === "no" || text === "false" || text === "n") return "No";
  return null;
}

function emailIssue(text) {
  if (text.length === 0) return null;
  if (text.length > 255) return "Email must be 255 characters or fewer.";
  if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(text)) return "Email is not a valid email address.";
  return null;
}

export function customValueIssue(field, raw) {
  if (!field?.custom) return null;
  const text = field.key === "kia"
    ? (canonicalKia(raw) ?? normalizeMergeText(raw, false))
    : normalizeMergeText(raw, field.custom === "textarea");
  const label = field.serverLabel || field.label;
  if (field.key === "kia" && canonicalKia(raw) == null) return "Killed in action must be Yes or No.";
  if (field.key === "email") return emailIssue(text);
  if (field.required && text.length === 0) return `${label} is required.`;
  if (field.serverMax && text.length > field.serverMax) {
    return `${label} must be ${field.serverMax} characters or fewer.`;
  }
  return null;
}

export function normalizedCustom(field, raw) {
  if (field.key === "kia") return canonicalKia(raw) ?? normalizeMergeText(raw, false);
  return normalizeMergeText(raw, field.custom === "textarea");
}

export function splitAwardItems(value) {
  const text = String(value ?? "").replace(/\r\n/g, "\n").replace(/\r/g, "\n");
  const items = [];
  for (const line of text.split("\n")) {
    for (const piece of line.split(/[;,]/)) {
      const item = normalizeMergeText(piece, false);
      if (item) items.push(item);
    }
  }
  return items;
}

function awardDelimiter(sources) {
  let hasNewline = false;
  let hasSemicolon = false;
  let hasComma = false;
  for (const source of sources) {
    const text = String(source ?? "").replace(/\r\n/g, "\n").replace(/\r/g, "\n");
    if (text.includes("\n")) hasNewline = true;
    if (String(source ?? "").includes(";")) hasSemicolon = true;
    if (String(source ?? "").includes(",")) hasComma = true;
  }
  if (hasNewline) return "\n";
  if (hasSemicolon && !hasComma) return "; ";
  if (hasComma && !hasSemicolon) return ", ";
  return "\n";
}

export function combineAwardLists(lists) {
  const sources = (lists ?? []).map((value) => String(value ?? ""));
  const delimiter = awardDelimiter(sources);
  const seen = new Set();
  const items = [];
  for (const source of sources) {
    for (const item of splitAwardItems(source)) {
      if (seen.has(item)) continue;
      seen.add(item);
      items.push(item);
    }
  }
  return items.join(delimiter);
}

export function blankChoice() {
  return { useCustom: false, sourceId: null, custom: "", explicit: false, noPhoto: false, combine: false };
}

export function survivorIdFromChoices(choices, includedIds) {
  const id = choices?.flagGrid?.sourceId ?? null;
  if (id == null || !(includedIds ?? []).includes(id)) return null;
  return id;
}

function fieldResolved(field, choice, includedIds) {
  if (!choice) return false;
  if (field.key === "photo") {
    if (choice.noPhoto) return true;
    return choice.sourceId != null && includedIds.includes(choice.sourceId);
  }
  if (field.key === "flagGrid") {
    if (choice.sourceId == null || !includedIds.includes(choice.sourceId)) return false;
    if (choice.useCustom) return customValueIssue(field, choice.custom) == null;
    return true;
  }
  if (field.key === "awards" && choice.combine && !choice.useCustom) return true;
  if (choice.useCustom && field.custom) return customValueIssue(field, choice.custom) == null;
  return choice.sourceId != null && includedIds.includes(choice.sourceId);
}

function choiceAlreadySet(choice) {
  if (!choice) return false;
  return Boolean(
    choice.useCustom ||
    choice.explicit ||
    choice.noPhoto ||
    choice.combine ||
    choice.sourceId != null
  );
}

export function applyMatchingValues(includedHonorees, previous) {
  const next = { ...(previous ?? {}) };
  const included = includedHonorees ?? [];
  if (included.length < 2) return next;
  for (const field of mergeFields) {
    // The flag grid chooses the survivor. A matching grid, including both blank, stays unset.
    if (field.key === "flagGrid") continue;
    if (choiceAlreadySet(previous?.[field.key])) continue;
    const values = included.map((honoree) => field.value(honoree));
    if (recordsDiffer(values)) continue;
    next[field.key] = {
      useCustom: false,
      sourceId: included[0].id,
      custom: "",
      explicit: true,
      noPhoto: false,
      combine: false
    };
  }
  return next;
}

function stickyChoice(prior, includedIds) {
  if (!prior) return null;
  if (prior.useCustom || prior.noPhoto || prior.combine) return prior;
  // An explicit pick stays. An automatic exact-match pick is rebuilt below.
  if (prior.explicit && prior.sourceId != null && includedIds.has(prior.sourceId)) return prior;
  return null;
}

export function choicesAfterIncludedChange(includedHonorees, previous) {
  const included = includedHonorees ?? [];
  const includedIds = new Set(included.map((honoree) => honoree.id));
  const next = {};
  for (const field of mergeFields) {
    const prior = previous?.[field.key];
    if (field.key === "flagGrid") {
      if (prior?.sourceId != null && includedIds.has(prior.sourceId)) next[field.key] = prior;
      continue;
    }
    const sticky = stickyChoice(prior, includedIds);
    if (sticky) {
      next[field.key] = sticky;
      continue;
    }
    if (included.length < 2) continue;
    const values = included.map((honoree) => field.value(honoree));
    if (recordsDiffer(values)) continue;
    next[field.key] = { ...blankChoice(), sourceId: included[0].id };
  }
  return next;
}

export function canConfirmMerge(includedIds, honorees, choices) {
  if (!includedIds || includedIds.length < 2) return false;
  const included = (honorees ?? []).filter((honoree) => includedIds.includes(honoree.id));
  if (included.length < 2) return false;
  if (survivorIdFromChoices(choices, includedIds) == null) return false;
  return mergeFields.every((field) => fieldResolved(field, choices?.[field.key], includedIds));
}

export function displayValue(field, honoree) {
  const raw = field.value(honoree);
  if (field.key === "photo") return raw ? raw : "No photo";
  const text = String(raw ?? "").trim();
  return text || "Blank";
}

export function previewValue(field, honorees, choice) {
  if (field.key === "photo" && choice?.noPhoto) {
    return { display: "No photo", input: "", custom: false, issue: null, saved: "" };
  }
  if (field.key === "awards" && choice?.combine && !choice.useCustom) {
    const saved = combineAwardLists((honorees ?? []).map((honoree) => field.value(honoree)));
    return { display: saved || "Blank", input: saved, custom: false, issue: null, saved };
  }
  if (choice?.useCustom && field.custom) {
    const issue = customValueIssue(field, choice.custom);
    const saved = normalizedCustom(field, choice.custom);
    const input = field.key === "kia"
      ? saved
      : previewDraft(String(choice.custom ?? ""), field.custom === "textarea");
    return { display: issue || saved || "Blank", input, custom: true, issue, saved };
  }
  const honoree = (honorees ?? []).find((item) => item.id === choice?.sourceId);
  if (!honoree) return { display: "Not chosen", input: "", custom: false, issue: null, saved: "" };
  if (field.key === "photo") {
    const name = field.value(honoree);
    return { display: name || "No photo", input: name, custom: false, issue: null, saved: name };
  }
  const raw = String(field.value(honoree) ?? "");
  return { display: raw.trim() ? raw : "Blank", input: raw, custom: false, issue: null, saved: raw };
}

export function previewRows(honorees, choices) {
  return mergeFields.map((field) => {
    const preview = previewValue(field, honorees, choices?.[field.key]);
    return { key: field.key, label: field.label, value: preview.display, custom: preview.custom };
  });
}

export const PREVIEW_FLASH_MS = 1000;
export const PREVIEW_NOTE_MS = 2000;

function previewSignature(field, honorees, choice) {
  const preview = previewValue(field, honorees, choice);
  if (field.key === "photo") {
    if (choice?.noPhoto) return "no-photo";
    const honoree = (honorees ?? []).find((item) => item.id === choice?.sourceId);
    if (!honoree) return "not-chosen";
    return honoree.photoUrl ? `photo:${honoree.photoUrl}` : "no-photo";
  }
  return `${preview.custom ? "1" : "0"}|${preview.display}|${preview.input}`;
}

export function changedPreviewKeys(honorees, previousChoices, nextChoices) {
  const keys = [];
  for (const field of mergeFields) {
    const before = previewSignature(field, honorees, previousChoices?.[field.key]);
    const after = previewSignature(field, honorees, nextChoices?.[field.key]);
    if (before !== after) keys.push(field.key);
  }
  return keys;
}

export function schedulePreviewFlash(keys, now = 0, holdMs = PREVIEW_FLASH_MS) {
  return { keys: [...(keys ?? [])], until: now + holdMs };
}

export function activeFlashKeys(flash, now) {
  if (!flash || now >= flash.until) return [];
  return flash.keys;
}

export function stillToChooseHint(includedIds, choices) {
  const ids = includedIds ?? [];
  const labels = mergeFields
    .filter((field) => !fieldResolved(field, choices?.[field.key], ids))
    .map((field) => field.label);
  if (!labels.length) return "";
  return `Still to choose: ${labels.join(", ")}.`;
}

export function rowIsInView(rect, viewport) {
  if (!rect || !viewport) return false;
  const vertical = rect.bottom > viewport.top && rect.top < viewport.bottom;
  const horizontal = rect.right > viewport.left && rect.left < viewport.right;
  return vertical && horizontal;
}

export function shouldShowPreviewNote(rect, viewport) {
  if (!rect || !viewport) return false;
  return !rowIsInView(rect, viewport);
}

export function previewUpdateText(label) {
  return `Preview updated: ${label}`;
}

export function previewNotePlacement(buttonRect, viewportHeight) {
  const height = viewportHeight ?? 0;
  const obstacles = Array.isArray(buttonRect) ? buttonRect : buttonRect ? [buttonRect] : [];
  const hits = (top, bottom) => obstacles.some((rect) => rect && rect.bottom > top && rect.top < bottom);
  if (!hits(0, 56)) return "top";
  if (!hits(64, 116)) return "offset";
  if (height > 0 && !hits(height - 56, height)) return "bottom";
  return "top";
}

function currentViewport() {
  return {
    top: 0,
    left: 0,
    bottom: window.innerHeight,
    right: window.innerWidth
  };
}

export function buildMergeRequest(honorees, choices) {
  /** @type {Record<string, number>} */
  const fields = {};
  /** @type {Record<string, string>} */
  const values = {};
  let combineAwards = false;
  for (const field of mergeFields) {
    const choice = choices?.[field.key];
    if (!choice) continue;
    if (field.key === "photo") {
      if (choice.noPhoto) {
        values.photo = "";
        continue;
      }
      if (choice.sourceId) fields.photo = choice.sourceId;
      continue;
    }
    if (field.key === "awards") {
      if (choice.useCustom) {
        values.awards = normalizedCustom(field, choice.custom);
        continue;
      }
      if (choice.combine) {
        combineAwards = true;
        continue;
      }
      if (choice.sourceId) fields.awards = choice.sourceId;
      continue;
    }
    if (field.key === "flagGrid") {
      if (choice.sourceId) fields.flagGrid = choice.sourceId;
      if (choice.useCustom) values.flagGrid = normalizedCustom(field, choice.custom);
      continue;
    }
    if (field.custom && choice.useCustom) {
      values[field.key] = normalizedCustom(field, choice.custom);
      continue;
    }
    if (choice.sourceId) fields[field.key] = choice.sourceId;
  }
  const body = {
    survivorId: choices?.flagGrid?.sourceId ?? 0,
    duplicateIds: honorees.map((honoree) => honoree.id),
    fields
  };
  if (Object.keys(values).length > 0) body.values = values;
  if (combineAwards) body.combineAwards = true;
  return body;
}

function differsFlag(show) {
  if (!show) return null;
  return h("span", { className: "differsFlag" }, "differs");
}

function photoBlock(honoree) {
  const name = fullName(honoree) || "Honoree";
  if (honoree.photoUrl) {
    return h("img", {
      className: "duplicatePhoto",
      src: honoree.photoUrl,
      alt: `${name} photo`
    });
  }
  return h("div", { className: "duplicateNoPhoto", role: "img", "aria-label": "No photo" }, "No photo");
}

function resultingPhoto(honorees, choice) {
  if (choice?.noPhoto) return photoBlock({ photoUrl: null });
  const honoree = (honorees ?? []).find((item) => item.id === choice?.sourceId);
  if (!honoree) return h("p", { className: "resultPhoto" }, "Not chosen");
  return photoBlock(honoree);
}

function choiceClass(selected) {
  return selected ? "mergeChoice isSelected" : "mergeChoice";
}

export function UseMatchingButton({ disabled, onClick }) {
  return h(
    "button",
    { type: "button", className: "secondary", disabled, onClick },
    "Use matches"
  );
}

export function SharedFieldChoices({ includedCount, choices, onNoPhoto, onCombine }) {
  const disabled = includedCount < 1;
  return h(
    "div",
    { className: "mergeSharedChoices" },
    h(
      "div",
      { className: "mergeSharedField" },
      h("h3", null, "Photo"),
      h(
        "label",
        { className: choiceClass(Boolean(choices?.photo?.noPhoto)) },
        h("input", {
          type: "radio",
          name: "field-photo",
          value: "none",
          checked: Boolean(choices?.photo?.noPhoto),
          "aria-checked": Boolean(choices?.photo?.noPhoto),
          disabled,
          onChange: onNoPhoto
        }),
        h("span", null, h("strong", null, "No photo"))
      )
    ),
    h(
      "div",
      { className: "mergeSharedField" },
      h("h3", null, "Awards"),
      h(
        "label",
        { className: choiceClass(Boolean(choices?.awards?.combine) && !choices?.awards?.useCustom) },
        h("input", {
          type: "radio",
          name: "field-awards",
          value: "combine",
          checked: Boolean(choices?.awards?.combine) && !choices?.awards?.useCustom,
          "aria-checked": Boolean(choices?.awards?.combine) && !choices?.awards?.useCustom,
          disabled: includedCount < 2,
          onChange: onCombine
        }),
        h("span", null, h("strong", null, "Combine both"))
      )
    )
  );
}

export function DuplicateReviewBoard({
  honorees,
  includedIds,
  choices,
  onToggleIncluded,
  onChooseSource
}) {
  const nameDiffers = recordsDiffer(honorees.map(fullName));

  return h(
    "div",
    { className: "mergeBoard", "aria-label": "Side-by-side duplicate review" },
    honorees.map((honoree) => {
      const included = includedIds.includes(honoree.id);
      const name = fullName(honoree) || honoree.name;
      const submitted = submissionDateLabel(honoree.createdDate);
      return h(
        "article",
        { key: honoree.id, className: "mergeColumn", "data-record": honoree.id },
        h(
          "label",
          { className: "duplicateCheck" },
          h("input", {
            type: "checkbox",
            checked: included,
            onChange: () => onToggleIncluded(honoree.id)
          }),
          h("span", null, `Include record #${honoree.id} in the merge`)
        ),
        h(
          "div",
          { className: nameDiffers ? "mergeIdentity isDiff" : "mergeIdentity", "data-field": "fullName" },
          photoBlock(honoree),
          h("h3", null, name),
          differsFlag(nameDiffers),
          submitted ? h("span", null, submitted) : null
        ),
        mergeFields.map((field) => {
          const differs = recordsDiffer(honorees.map((item) => field.value(item)));
          const choice = choices?.[field.key];
          const sourceChecked = field.key === "flagGrid"
            ? choice?.sourceId === honoree.id
            : !choice?.useCustom && !choice?.noPhoto && !choice?.combine && choice?.sourceId === honoree.id;
          return h(
            "div",
            {
              key: field.key,
              className: differs ? "mergeField isDiff" : "mergeField",
              "data-field": field.key
            },
            h(
              "label",
              { className: choiceClass(sourceChecked) },
              h("input", {
                type: "radio",
                name: `field-${field.key}`,
                value: String(honoree.id),
                checked: sourceChecked,
                "aria-checked": sourceChecked,
                disabled: !included,
                onChange: () => onChooseSource(field.key, honoree.id)
              }),
              h(
                "span",
                null,
                h("strong", null, field.label),
                " ",
                differsFlag(differs),
                h("em", { className: field.key === "awards" || field.key === "description" || field.key === "conflictsServed" ? "mergeMultiline" : undefined }, displayValue(field, honoree))
              )
            )
          );
        })
      );
    })
  );
}

function customTag(show) {
  if (!show) return null;
  return h("span", { className: "customTag" }, "custom");
}

function noteEntries(notes) {
  return (notes ?? []).map((item) => (typeof item === "string" ? { key: item, label: item } : item));
}

export function ResultingRecord({
  honorees,
  choices,
  onEdit,
  onGrid,
  highlightKeys,
  highlightToken = 0,
  litKeys = [],
  notes = []
}) {
  const [lit, setLit] = useState(litKeys);
  const [liveNotes, setLiveNotes] = useState(() => noteEntries(notes));
  const [notePlace, setNotePlace] = useState("top");
  const seenToken = useRef(highlightToken);
  const sectionRef = useRef(null);
  const flashTimers = useRef(new Map());
  const noteTimers = useRef(new Map());

  useEffect(() => {
    const flashes = flashTimers.current;
    const announcements = noteTimers.current;
    return () => {
      for (const id of flashes.values()) window.clearTimeout(id);
      for (const id of announcements.values()) window.clearTimeout(id);
    };
  }, []);

  useEffect(() => {
    if (!liveNotes.length) return undefined;
    function hideVisible() {
      const viewport = currentViewport();
      setLiveNotes((current) => current.filter((item) => {
        const node = sectionRef.current?.querySelector(`[data-preview="${item.key}"]`);
        return shouldShowPreviewNote(node?.getBoundingClientRect(), viewport);
      }));
    }
    window.addEventListener("scroll", hideVisible, true);
    window.addEventListener("resize", hideVisible);
    return () => {
      window.removeEventListener("scroll", hideVisible, true);
      window.removeEventListener("resize", hideVisible);
    };
  }, [liveNotes.length]);

  useEffect(() => {
    if (!highlightToken || highlightToken === seenToken.current) return;
    seenToken.current = highlightToken;
    const keys = highlightKeys ?? [];
    if (!keys.length) return;
    const flash = schedulePreviewFlash(keys, Date.now());
    const active = activeFlashKeys(flash, Date.now());
    setLit((current) => [...new Set([...current, ...active])]);
    for (const key of active) {
      const existing = flashTimers.current.get(key);
      if (existing) window.clearTimeout(existing);
      const timer = window.setTimeout(() => {
        flashTimers.current.delete(key);
        setLit((current) => current.filter((item) => item !== key));
      }, PREVIEW_FLASH_MS);
      flashTimers.current.set(key, timer);
    }

    const viewport = currentViewport();
    const root = sectionRef.current;
    const announced = [];
    for (const key of active) {
      const node = root?.querySelector(`[data-preview="${key}"]`);
      const rect = node?.getBoundingClientRect();
      if (!shouldShowPreviewNote(rect, viewport)) continue;
      const field = mergeFields.find((item) => item.key === key);
      if (!field) continue;
      announced.push({ key, label: field.label });
    }
    if (announced.length) {
      const obstacles = [...document.querySelectorAll(".mergeStepFooter, .mergeActions, .mergeChoice, button")].map((node) => node.getBoundingClientRect());
      setNotePlace(previewNotePlacement(obstacles, window.innerHeight));
      setLiveNotes((current) => {
        const rest = current.filter((item) => !announced.some((entry) => entry.key === item.key));
        return [...rest, ...announced];
      });
      for (const entry of announced) {
        const existing = noteTimers.current.get(entry.key);
        if (existing) window.clearTimeout(existing);
        const timer = window.setTimeout(() => {
          noteTimers.current.delete(entry.key);
          setLiveNotes((current) => current.filter((item) => item.key !== entry.key));
        }, PREVIEW_NOTE_MS);
        noteTimers.current.set(entry.key, timer);
      }
    }
    return () => {
      // A strict-mode remount runs this cleanup and then the effect again.
      seenToken.current = 0;
    };
  }, [highlightToken, highlightKeys]);

  return h(
    "section",
    { className: "mergePreview", "aria-label": "Resulting record", ref: sectionRef },
    h(
      "div",
      {
        className: notePlace === "bottom" ? "previewUpdateNote isBottom" : notePlace === "offset" ? "previewUpdateNote isOffset" : "previewUpdateNote",
        "aria-live": "polite",
        role: "status",
        "data-place": notePlace
      },
      liveNotes.map((item) => h("p", { key: item.key }, previewUpdateText(item.label)))
    ),
    h("h3", null, "Resulting record"),
    h(
      "div",
      { className: "resultGrid" },
      mergeFields.map((field) => {
        const preview = previewValue(field, honorees, choices?.[field.key]);
        const inputId = `result-${field.key}`;
        let control;
        if (field.key === "photo") {
          control = resultingPhoto(honorees, choices?.[field.key]);
        } else if (field.picker) {
          control = h("input", {
            id: inputId,
            type: "text",
            maxLength: field.maxLength,
            placeholder: field.placeholder || "AA-10",
            "aria-label": "Flag grid",
            value: preview.input,
            onChange: (event) => onGrid(event.target.value)
          });
        } else if (field.custom === "textarea") {
          control = h("textarea", {
            id: inputId,
            rows: field.key === "description" ? 4 : 3,
            maxLength: field.maxLength,
            value: preview.input,
            onChange: (event) => onEdit(field.key, event.target.value)
          });
        } else {
          control = h("input", {
            id: inputId,
            type: "text",
            maxLength: field.maxLength,
            placeholder: preview.display === "Not chosen" ? "Not chosen" : field.placeholder,
            value: preview.input,
            onChange: (event) => onEdit(field.key, event.target.value)
          });
        }
        return h(
          "div",
          { key: field.key, className: lit.includes(field.key) ? "resultField resultFlash" : "resultField", "data-preview": field.key, "data-custom": preview.custom ? "true" : "false" },
          h("label", { htmlFor: field.key === "photo" ? undefined : inputId }, field.label, " ", customTag(preview.custom)),
          h(
            "div",
            { className: "resultControl" },
            control,
            preview.issue ? h("p", { className: "message error", role: "alert" }, preview.issue) : null
          )
        );
      })
    )
  );
}

function StepFooter({ primaryLabel, primaryClass, primaryDisabled, previewDisabled, onPrimary, onPreview, warning, hint }) {
  return h(
    "div",
    { className: "mergeStepFooter" },
    warning ? h("p", { className: "mergeWarning" }, warning) : null,
    h(
      "div",
      { className: "mergeStepFooterActions" },
      h(
        "div",
        { className: "mergeStepFooterPrimary" },
        h(
          "button",
          {
            type: "button",
            className: primaryClass || undefined,
            disabled: primaryDisabled,
            onClick: onPrimary
          },
          primaryLabel
        ),
        hint ?? null
      ),
      h(
        "button",
        {
          type: "button",
          className: "secondary",
          disabled: previewDisabled,
          onClick: onPreview
        },
        "Preview PDF"
      )
    )
  );
}

export function ReviewStepActions({ ready, busy, onPreview, onContinue, hint }) {
  return h(StepFooter, {
    primaryLabel: "Continue",
    primaryDisabled: !ready,
    previewDisabled: !ready || busy,
    onPrimary: onContinue,
    onPreview,
    hint
  });
}

export function StillToChoose({ includedIds, choices }) {
  const text = stillToChooseHint(includedIds, choices);
  if (!text) return null;
  return h("p", { className: "helperText" }, text);
}

export function MergeActions({ ready, busy, onMerge, onPreview, hint }) {
  return h(StepFooter, {
    primaryLabel: "Merge",
    primaryClass: "mergeCommit",
    primaryDisabled: !ready || busy,
    previewDisabled: !ready || busy,
    onPrimary: onMerge,
    onPreview,
    warning: "Other records become inactive and their flag spots are released.",
    hint
  });
}
