import { createElement as h } from "react";

export function formatMergeWhen(value) {
  if (!value) return "";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return String(value);
  return new Intl.DateTimeFormat(undefined, {
    year: "numeric",
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit"
  }).format(date);
}

export function mergeHistoryRows(records) {
  return [...(records ?? [])].sort((a, b) => {
    const left = Date.parse(a?.mergedUtc ?? "") || 0;
    const right = Date.parse(b?.mergedUtc ?? "") || 0;
    if (right !== left) return right - left;
    return (b?.id ?? 0) - (a?.id ?? 0);
  });
}

function absorbedLabel(row) {
  const names = (row.absorbed ?? []).map((item) => item.name).filter(Boolean);
  return names.length ? names.join(", ") : "None";
}

export function MergeHistoryScreen({
  records,
  loaded,
  loadError,
  busy,
  error,
  onUndo,
  onBack,
  onReload
}) {
  const rows = mergeHistoryRows(records);
  return h(
    "section",
    { className: "card adminCard duplicateMergeCard", "aria-label": "Merge history" },
    h(
      "div",
      { className: "sectionHeader" },
      h(
        "div",
        null,
        h("p", { className: "eyebrow" }, "Administrator"),
        h("h2", null, "Merge history"),
        h(
          "p",
          { className: "helperText" },
          "Each merge is listed newest first. Undo puts the surviving record and the absorbed records back, including the rows the merge moved. Any administrator can undo a merge that has not already been undone."
        )
      ),
      h(
        "div",
        { className: "adminHeaderActions" },
        onBack
          ? h(
              "button",
              { type: "button", className: "secondary", onClick: onBack },
              "Back to duplicates"
            )
          : null,
        h(
          "button",
          { type: "button", className: "secondary", onClick: onReload, disabled: busy },
          busy ? "Loading..." : "Refresh"
        )
      )
    ),
    loadError ? h("p", { className: "message error", role: "alert" }, loadError) : null,
    error ? h("p", { className: "message error", role: "alert" }, error) : null,
    loaded && rows.length === 0 ? h("p", null, "No merges have been recorded.") : null,
    rows.length
      ? h(
          "ol",
          { className: "duplicateGroupList" },
          rows.map((row) =>
            h(
              "li",
              { key: row.id, className: "duplicateGroup", "data-merge": row.id },
              h("p", null, h("strong", null, "Survivor"), " ", row.survivorName),
              h("p", null, h("strong", null, "Absorbed"), " ", absorbedLabel(row)),
              h("p", null, h("strong", null, "Administrator"), " ", row.mergedBy),
              h("p", null, h("strong", null, "Time"), " ", formatMergeWhen(row.mergedUtc)),
              row.undoneUtc
                ? h(
                    "p",
                    null,
                    `Undone ${formatMergeWhen(row.undoneUtc)}${row.undoneBy ? ` by ${row.undoneBy}` : ""}.`
                  )
                : h(
                    "button",
                    {
                      type: "button",
                      className: "secondary",
                      disabled: busy,
                      onClick: () => onUndo?.(row.id)
                    },
                    "Undo"
                  )
            )
          )
        )
      : null
  );
}
