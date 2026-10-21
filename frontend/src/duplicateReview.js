import { createElement as h } from "react";

export const mergeFields = [
  { key: "firstName", label: "First name", custom: "text", maxLength: 200, value: (honoree) => honoree.firstName },
  { key: "middleName", label: "Middle name", custom: "text", maxLength: 200, value: (honoree) => honoree.middleName || "" },
  { key: "lastName", label: "Last name", custom: "text", maxLength: 200, value: (honoree) => honoree.lastName },
  { key: "suffix", label: "Suffix", custom: "text", maxLength: 100, value: (honoree) => honoree.suffix || "" },
  { key: "nickname", label: "Nickname", custom: "text", maxLength: 200, value: (honoree) => honoree.nickname || "" },
  { key: "rank", label: "Rank", custom: "text", maxLength: 200, value: (honoree) => honoree.rank },
  { key: "serviceBranch", label: "Service branch", custom: "text", maxLength: 200, placeholder: "Existing branch name", value: (honoree) => honoree.serviceBranchName || "" },
  { key: "serviceYears", label: "Years of service", custom: "text", maxLength: 500, placeholder: "1942 - 1945", value: (honoree) => honoree.serviceYears },
  { key: "conflictsServed", label: "Conflicts served", custom: "textarea", value: (honoree) => honoree.conflictsServed },
  { key: "awards", label: "Awards", custom: false, value: (honoree) => honoree.awards },
  { key: "description", label: "Bio", custom: "textarea", value: (honoree) => honoree.description },
  { key: "kia", label: "Killed in action", custom: "text", maxLength: 5, placeholder: "Yes or No", value: (honoree) => (honoree.kia ? "Yes" : "No") },
  { key: "photo", label: "Photo", custom: false, value: (honoree) => honoree.photoFileName || "" },
  { key: "flagGrid", label: "Flag grid", custom: "text", maxLength: 100, placeholder: "AA-10", value: (honoree) => honoree.flagGridName || "" },
  { key: "phone", label: "Phone", custom: "text", maxLength: 50, value: (honoree) => honoree.phoneNumber },
  { key: "email", label: "Email", custom: "text", maxLength: 255, value: (honoree) => honoree.emailAddress },
  { key: "sponsor", label: "Sponsor", custom: "text", maxLength: 400, placeholder: "Existing sponsor name", value: (honoree) => honoree.sponsorName }
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

export function choicesForSurvivor(survivorId) {
  return Object.fromEntries(
    mergeFields.map((field) => [field.key, { useCustom: false, sourceId: survivorId, custom: "" }])
  );
}

export function canConfirmMerge(survivorId, includedIds) {
  return survivorId != null && includedIds.includes(survivorId) && includedIds.length >= 2;
}

export function displayValue(field, honoree) {
  const raw = field.value(honoree);
  if (field.key === "photo") return raw ? raw : "No photo";
  const text = String(raw ?? "").trim();
  return text || "Blank";
}

export function previewRows(honorees, choices) {
  return mergeFields.map((field) => {
    const choice = choices?.[field.key];
    if (choice?.useCustom && field.custom) {
      const typed = String(choice.custom ?? "").trim();
      return { key: field.key, label: field.label, value: typed || "Blank" };
    }
    const honoree = honorees.find((item) => item.id === choice?.sourceId);
    return { key: field.key, label: field.label, value: honoree ? displayValue(field, honoree) : "Blank" };
  });
}

export function buildMergeRequest(survivorId, honorees, choices) {
  /** @type {Record<string, number>} */
  const fields = {};
  /** @type {Record<string, string>} */
  const values = {};
  for (const field of mergeFields) {
    const choice = choices?.[field.key];
    if (field.custom && choice?.useCustom) {
      values[field.key] = choice.custom ?? "";
      continue;
    }
    if (choice?.sourceId) fields[field.key] = choice.sourceId;
  }
  const body = {
    survivorId,
    duplicateIds: honorees.map((honoree) => honoree.id),
    fields
  };
  if (Object.keys(values).length > 0) body.values = values;
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

export function MergePreview({ rows }) {
  if (!rows) {
    return h(
      "section",
      { className: "mergePreview", "aria-label": "Merged record preview" },
      h("h3", null, "Merged record preview"),
      h("p", null, "Choose a surviving record to preview the merged values. Nothing is selected automatically.")
    );
  }

  return h(
    "section",
    { className: "mergePreview", "aria-label": "Merged record preview" },
    h("h3", null, "Merged record preview"),
    h(
      "dl",
      null,
      rows.map((row) =>
        h(
          "div",
          { key: row.key, "data-preview": row.key },
          h("dt", null, row.label),
          h("dd", null, row.value)
        )
      )
    )
  );
}

export function DuplicateReviewBoard({
  honorees,
  includedIds,
  survivorId,
  choices,
  onToggleIncluded,
  onSurvivor,
  onChooseSource,
  onCustom
}) {
  const survivorChosen = survivorId != null;
  const nameDiffers = recordsDiffer(honorees.map(fullName));
  const photoDiffers = recordsDiffer(honorees.map((honoree) => honoree.photoFileName || ""));

  return h(
    "div",
    { className: "mergeBoard", "aria-label": "Side-by-side duplicate review" },
    honorees.map((honoree) => {
      const included = includedIds.includes(honoree.id);
      const name = fullName(honoree) || honoree.name;
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
          "label",
          { className: "duplicateCheck" },
          h("input", {
            type: "radio",
            name: "duplicate-survivor",
            value: String(honoree.id),
            checked: survivorId === honoree.id,
            disabled: !included,
            onChange: () => onSurvivor(honoree.id)
          }),
          h("span", null, `Keep record #${honoree.id} as the surviving record`)
        ),
        h(
          "div",
          { className: nameDiffers ? "mergeIdentity isDiff" : "mergeIdentity", "data-field": "fullName" },
          photoBlock(honoree),
          h("h3", null, name),
          differsFlag(nameDiffers)
        ),
        h(
          "div",
          { className: photoDiffers ? "mergeField isDiff" : "mergeField", "data-field": "photo" },
          h(
            "label",
            { className: "mergeChoice" },
            h("input", {
              type: "radio",
              name: "field-photo",
              value: String(honoree.id),
              checked: !choices?.photo?.useCustom && choices?.photo?.sourceId === honoree.id,
              disabled: !survivorChosen || !included,
              onChange: () => onChooseSource("photo", honoree.id)
            }),
            h(
              "span",
              null,
              h("strong", null, "Photo"),
              " ",
              differsFlag(photoDiffers),
              h("em", null, honoree.photoFileName ? honoree.photoFileName : "No photo")
            )
          )
        ),
        mergeFields
          .filter((field) => field.key !== "photo")
          .map((field) => {
            const differs = recordsDiffer(honorees.map((item) => field.value(item)));
            const choice = choices?.[field.key];
            const sourceChecked = !choice?.useCustom && choice?.sourceId === honoree.id;
            return h(
              "div",
              {
                key: field.key,
                className: differs ? "mergeField isDiff" : "mergeField",
                "data-field": field.key
              },
              h(
                "label",
                { className: "mergeChoice" },
                h("input", {
                  type: "radio",
                  name: `field-${field.key}`,
                  value: String(honoree.id),
                  checked: sourceChecked,
                  disabled: !survivorChosen || !included,
                  onChange: () => onChooseSource(field.key, honoree.id)
                }),
                h(
                  "span",
                  null,
                  h("strong", null, field.label),
                  " ",
                  differsFlag(differs),
                  h("em", { className: field.key === "awards" || field.key === "description" ? "mergeMultiline" : undefined }, displayValue(field, honoree))
                )
              )
            );
          })
      );
    })
  );
}

// Custom inputs are rendered once per field, under the columns, so a phone
// stack does not repeat a text box inside every record.
export function DuplicateCustomFields({ survivorId, choices, onCustom }) {
  const survivorChosen = survivorId != null;
  return h(
    "div",
    { className: "mergeCustomFields" },
    h("h3", null, "Or type a value"),
    mergeFields
      .filter((field) => field.custom)
      .map((field) => {
        const choice = choices?.[field.key];
        const useCustom = Boolean(choice?.useCustom);
        const inputId = `custom-${field.key}`;
        const control =
          field.custom === "textarea"
            ? h("textarea", {
                id: inputId,
                rows: 3,
                maxLength: field.maxLength,
                disabled: !survivorChosen,
                value: choice?.custom ?? "",
                onChange: (event) => onCustom(field.key, event.target.value)
              })
            : h("input", {
                id: inputId,
                type: "text",
                maxLength: field.maxLength,
                placeholder: field.placeholder || "Custom value",
                disabled: !survivorChosen,
                value: choice?.custom ?? "",
                onChange: (event) => onCustom(field.key, event.target.value)
              });
        return h(
          "div",
          { key: field.key, className: "mergeCustom", "data-custom": field.key },
          h(
            "label",
            { className: "mergeChoice" },
            h("input", {
              type: "radio",
              name: `field-${field.key}`,
              value: "custom",
              checked: useCustom,
              disabled: !survivorChosen,
              onChange: () => onCustom(field.key, choice?.custom ?? "")
            }),
            h("span", null, h("strong", null, field.label))
          ),
          h("label", { className: "mergeCustomInput", htmlFor: inputId }, "Custom value", control)
        );
      }),
    h(
      "p",
      { className: "helperText" },
      "Photo and awards keep one record's photo or awards. Those are a file and a stored set, so they have no typed value."
    )
  );
}
