import { createElement as h } from "react";

export function visibleReason(value) {
  return String(value ?? "").replace(/\p{Cf}/gu, "").replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]/g, "").trim();
}

export function RemoveFromGridDialog({
  name,
  gridName,
  reason,
  duplicates,
  loading,
  error,
  busy,
  onReason,
  onCancel,
  onConfirm,
  onMerge
}) {
  const others = duplicates ?? [];
  const hasDuplicates = others.length > 0;
  const reasonReady = visibleReason(reason).length > 0;
  return h(
    "div",
    {
      className: "modalOverlay appConfirmOverlay",
      role: "dialog",
      "aria-modal": "true",
      "aria-labelledby": "remove-grid-title",
      onClick: onCancel
    },
    h(
      "div",
      { className: "modalCard appConfirmModal", onClick: (event) => event.stopPropagation() },
      h(
        "div",
        null,
        h("p", { className: "eyebrow" }, "Confirm action"),
        h("h2", { id: "remove-grid-title" }, "Remove from flag grid"),
        h(
          "p",
          { className: "helperText" },
          `Remove ${name || "this honoree"} from flag grid ${gridName || "the current spot"}? The honoree is marked inactive, the spot is freed, and the card is added to the Removal Queue.`
        ),
        h(
          "label",
          { className: "form-label", htmlFor: "remove-grid-reason" },
          "Reason",
          h("textarea", {
            id: "remove-grid-reason",
            className: "form-control",
            rows: 3,
            maxLength: 1000,
            required: true,
            value: reason ?? "",
            onChange: (event) => onReason?.(event.target.value)
          })
        ),
        loading
          ? h("p", { className: "helperText" }, "Checking for similar honorees...")
          : null,
        hasDuplicates
          ? h(
              "div",
              { className: "alert alert-warning", role: "status" },
              h("p", { className: "mb-2" }, "Other active honorees have a similar name or details on a different grid."),
              h(
                "ul",
                { className: "mb-0" },
                others.map((item) =>
                  h(
                    "li",
                    { key: item.honoreeId },
                    h("strong", null, item.name),
                    h(
                      "span",
                      { className: "d-block" },
                      [item.flagGridName, item.rank, item.serviceBranchName].filter(Boolean).join(" · ")
                    ),
                    item.reasons ? h("small", { className: "d-block" }, item.reasons) : null
                  )
                )
              )
            )
          : null,
        error ? h("p", { className: "message error", role: "alert" }, error) : null
      ),
      h(
        "div",
        { className: "modalActions" },
        h(
          "button",
          { type: "button", className: "btn btn-outline-secondary", onClick: onCancel, disabled: busy },
          "Cancel"
        ),
        hasDuplicates
          ? h(
              "button",
              { type: "button", className: "btn btn-outline-secondary", onClick: onMerge, disabled: busy },
              "Merge instead"
            )
          : null,
        h(
          "button",
          {
            type: "button",
            className: "btn btn-danger",
            onClick: onConfirm,
            disabled: busy || loading || !reasonReady
          },
          busy ? "Removing..." : hasDuplicates ? "Not a duplicate" : "Remove"
        )
      )
    )
  );
}
