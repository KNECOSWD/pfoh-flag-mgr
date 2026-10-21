import { createElement as h } from "react";
import { DuplicateIconButton } from "./duplicateChrome.js";

const SPOT_TIME_ZONE = "America/Chicago";

export function formatRemovalWhen(value) {
  if (!value) return "";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return String(value);
  return new Intl.DateTimeFormat("en-US", {
    timeZone: SPOT_TIME_ZONE,
    year: "numeric",
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit"
  }).format(date);
}

export function removalSummary(item) {
  const spot = item?.flagGridName || "the spot";
  const next = item?.newFlagGridName || "the new spot";
  const detail = String(item?.sourceDetail ?? "").trim();
  const closed = Boolean(item?.doneUtc || item?.canceledUtc);
  if (item?.source === "Manual") {
    return closed
      ? `Removed from ${spot} in Flag Manager.`
      : `Removed from ${spot} in Flag Manager. No new spot.`;
  }
  if (item?.source === "Move" && !detail) {
    return `Moved from ${spot} to ${next}. The new card for ${next} is in Ready to print.`;
  }
  if (item?.source === "Merge") {
    return detail || `Spot released by Merge #${item?.mergeAuditId ?? ""}.`;
  }
  if (/withdrawal/i.test(detail)) {
    return "Withdrawal confirmed as removed from the field in Pending review.";
  }
  return detail || "Changed another way.";
}

export function removalHistoryNote(item) {
  const withdrawal = /withdrawal/i.test(String(item?.sourceDetail ?? ""));
  const loggedAlready =
    item?.loggedAlreadyDone ||
    (item?.doneUtc && item.doneUtc === item.createdUtc && item?.source === "Other" && withdrawal);
  if (loggedAlready) {
    return "✓ Done when it was logged (the admin confirmed the card was already off the field)";
  }
  if (item?.canceledUtc) {
    const when = formatRemovalWhen(item.canceledUtc);
    const who = item.canceledBy ? ` by ${item.canceledBy}` : "";
    if (item.mergeAuditId && /merge undone/i.test(String(item.cancelReason ?? ""))) {
      return `Canceled · Merge #${item.mergeAuditId} was undone ${when}${who} before the card was pulled. The honoree is back at ${item.flagGridName}.`;
    }
    if (/same spot/i.test(String(item.cancelReason ?? ""))) {
      return `Canceled · put back at ${item.flagGridName} ${when}${who}.`;
    }
    return `Canceled ${when}${who}.`;
  }
  if (item?.doneUtc) {
    const when = formatRemovalWhen(item.doneUtc);
    const who = item.doneBy ? ` by ${item.doneBy}` : "";
    return `✓ Pulled · marked done ${when}${who}`;
  }
  return "";
}

export function removalLink(item) {
  if (item?.source === "Merge" && item.mergeAuditId) {
    return {
      href: "/admin/duplicates/history",
      label: `Merge #${item.mergeAuditId} in Merge history`
    };
  }
  if (item?.source === "Move" && !String(item.sourceDetail ?? "").trim()) {
    return { href: "/admin/printing", label: "Ready to print" };
  }
  return null;
}

export function spotTileLabel(item) {
  if (String(item?.sourceDetail ?? "").startsWith("Put back")) return "Put back";
  if (item?.doneUtc || item?.canceledUtc) return "Was at";
  return "Pull from";
}

function rowLabel(name) {
  const value = String(name ?? "").trim().toUpperCase();
  const prefix = value.replace(/[0-9].*$/, "").replace(/[-_\s]+$/, "");
  return prefix ? prefix : "OTHER";
}

function columnNumber(name) {
  const match = String(name ?? "").match(/\d+/);
  return match ? Number(match[0]) : Number.MAX_SAFE_INTEGER;
}

export function compareFlagSpots(left, right) {
  const leftRow = rowLabel(left);
  const rightRow = rowLabel(right);
  if (leftRow === "OTHER" && rightRow !== "OTHER") return 1;
  if (rightRow === "OTHER" && leftRow !== "OTHER") return -1;
  if (leftRow.length !== rightRow.length) return leftRow.length - rightRow.length;
  const row = leftRow.localeCompare(rightRow);
  if (row !== 0) return row;
  const column = columnNumber(left) - columnNumber(right);
  if (column !== 0) return column;
  return String(left ?? "").localeCompare(String(right ?? ""), undefined, { sensitivity: "accent" });
}

export function sortOpenRemovalItems(items) {
  return [...(items ?? [])].sort((left, right) => {
    const spot = compareFlagSpots(left.flagGridName, right.flagGridName);
    return spot !== 0 ? spot : (left.id ?? 0) - (right.id ?? 0);
  });
}

export function allRemovalItemsSelected(ids, selected) {
  return ids.length > 0 && ids.every((id) => selected.includes(id));
}

export function toggleRemovalSelection(selected, id) {
  return selected.includes(id) ? selected.filter((current) => current !== id) : [...selected, id];
}

export function toggleSelectAllRemovalItems(ids, selected) {
  return allRemovalItemsSelected(ids, selected) ? [] : [...ids];
}

function sourceClass(source) {
  const name = String(source ?? "other").toLowerCase();
  if (name === "manual" || name === "move" || name === "merge" || name === "other") return name;
  return "other";
}

export function RemovalQueueScreen({
  status,
  openItems,
  doneItems,
  loaded,
  loadError,
  busy,
  error,
  selectedIds,
  onToggle,
  onToggleAll,
  onMarkDone,
  onRefresh,
  onShowOpen,
  onShowDone,
  onNavigate
}) {
  const open = sortOpenRemovalItems(openItems);
  const done = doneItems ?? [];
  const openCount = open.length;
  const viewingDone = status === "done";
  const rows = viewingDone ? done : open;
  const selected = selectedIds ?? [];
  const openIds = open.map((item) => item.id);
  const allSelected = allRemovalItemsSelected(openIds, selected);
  const lead = viewingDone
    ? "Done items are kept as history. They can't be changed. A card is listed when its honoree is taken off a flag spot: removed or moved in Flag Manager, released by a duplicate merge, or changed another way."
    : openCount === 0
      ? "No flag cards are waiting to be pulled. A card is listed when its honoree is taken off a flag spot: removed or moved in Flag Manager, released by a duplicate merge, or changed another way."
      : `${openCount} card${openCount === 1 ? "" : "s"} to pull from the field. A card is listed when its honoree is taken off a flag spot: removed or moved in Flag Manager, released by a duplicate merge, or changed another way.`;

  return h(
    "section",
    { className: "card adminCard adminPrintingCard removalQueueCard", "aria-label": "Removal Queue" },
    h(
      "div",
      { className: "sectionHeader printCenterHeader" },
      h(
        "div",
        null,
        h("p", { className: "eyebrow" }, "Administrator"),
        h("h2", null, "Removal Queue", h("span", { className: "countBadge" }, String(openCount))),
        h("p", { className: "helperText" }, lead)
      ),
      h(
        "div",
        { className: "actions printActions" },
        viewingDone
          ? null
          : h("span", { className: "selectedCount" }, `${selected.length} selected`),
        h(DuplicateIconButton, {
          kind: "refresh",
          className: "rqIconButton",
          label: busy ? "Loading" : "Refresh",
          disabled: busy,
          onClick: onRefresh
        }),
        viewingDone
          ? null
          : h(
              "button",
              {
                type: "button",
                disabled: busy || selected.length === 0,
                onClick: () => onMarkDone?.(selected)
              },
              "Mark done"
            )
      )
    ),
    h(
      "nav",
      { className: "rqTabs", "aria-label": "Removal Queue views" },
      h(
        "button",
        {
          type: "button",
          className: viewingDone ? "" : "isActive",
          "aria-current": viewingDone ? "false" : "page",
          onClick: onShowOpen
        },
        "Open ",
        h("span", { className: openCount === 0 ? "rqCount isMuted" : "rqCount" }, String(openCount))
      ),
      h(
        "button",
        {
          type: "button",
          className: viewingDone ? "isActive" : "",
          "aria-current": viewingDone ? "page" : "false",
          onClick: onShowDone
        },
        "Done"
      )
    ),
    loadError ? h("p", { className: "message error", role: "alert" }, loadError) : null,
    error ? h("p", { className: "message error", role: "alert" }, error) : null,
    viewingDone
      ? h("div", { className: "rqToolbar" }, h("span", null, "Newest first · kept as history"))
      : loaded && openCount === 0
        ? null
        : h(
            "div",
            { className: "rqToolbar" },
            h(
              "label",
              { className: "tableSelectAll rqSelectAll" },
              h("input", {
                type: "checkbox",
                checked: allSelected,
                disabled: openCount === 0 || busy,
                "aria-label": "Select all cards to pull",
                onChange: () => onToggleAll?.()
              }),
              h("span", null, "Select all")
            ),
            h("span", null, "Sorted by flag spot")
          ),
    loaded && !viewingDone && openCount === 0
      ? h(
          "div",
          { className: "rqEmpty", role: "status" },
          h("span", { className: "rqEmptyIcon", "aria-hidden": "true" }, "✓"),
          h("h3", null, "No flag cards to pull"),
          h(
            "p",
            null,
            "When a flag card is removed from a person or moved to a new spot, it shows up here so someone can pull the old card from the field."
          ),
          h(
            "button",
            { type: "button", className: "rqEmptyLink", onClick: onShowDone },
            "See done items"
          )
        )
      : null,
    rows.length
      ? h(
          "ol",
          { className: "rqList" },
          rows.map((item) => {
            const closed = Boolean(item.doneUtc || item.canceledUtc);
            const checked = selected.includes(item.id);
            const link = removalLink(item);
            const history = removalHistoryNote(item);
            return h(
              "li",
              {
                key: item.id,
                className: ["rqItem", closed ? "isDone" : "", !closed && checked ? "isSelected" : ""]
                  .filter(Boolean)
                  .join(" ")
              },
              closed
                ? null
                : h(
                    "label",
                    { className: "rqSelect" },
                    h("input", {
                      type: "checkbox",
                      checked,
                      disabled: busy,
                      "aria-label": `Select ${item.honoreeName} at ${item.flagGridName}`,
                      onChange: () => onToggle?.(item.id)
                    })
                  ),
              h(
                "div",
                { className: "rqGrid", "aria-label": `Flag spot ${item.flagGridName}` },
                h("small", null, spotTileLabel(item)),
                h("b", null, item.flagGridName)
              ),
              h(
                "div",
                { className: "rqBody" },
                h(
                  "h3",
                  null,
                  item.honoreeName,
                  h("span", { className: `rqSource ${sourceClass(item.source)}` }, item.source)
                ),
                h("p", { className: "rqWhat" }, removalSummary(item)),
                h(
                  "p",
                  { className: "rqMeta" },
                  h("span", null, formatRemovalWhen(item.createdUtc)),
                  h("span", null, `by ${item.createdBy}`),
                  link
                    ? h(
                        "a",
                        {
                          href: link.href,
                          onClick: (event) => {
                            event.preventDefault();
                            onNavigate?.(link.href);
                          }
                        },
                        link.label
                      )
                    : null
                ),
                history
                  ? h(
                      "p",
                      { className: item.canceledUtc ? "rqDoneNote isCanceled" : "rqDoneNote" },
                      history
                    )
                  : null
              ),
              closed
                ? null
                : h(
                    "div",
                    { className: "rqActions" },
                    h(
                      "button",
                      {
                        type: "button",
                        className: "secondary compactButton",
                        disabled: busy,
                        onClick: () => onMarkDone?.([item.id])
                      },
                      "Mark done"
                    )
                  )
            );
          })
        )
      : null
  );
}
