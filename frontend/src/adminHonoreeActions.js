import { createElement as h } from "react";

function closeMenu(event) {
  event.currentTarget.closest("details")?.removeAttribute("open");
}

export function AdminHonoreeActions({
  open,
  busy,
  regenerating,
  queueing,
  claimantBusy,
  claimantsLoaded,
  hasFlagGrid,
  removing,
  onEdit,
  onQueue,
  onClaimants,
  onAssign,
  onRegenerate,
  onRemove
}) {
  return h(
    "details",
    {
      className: "adminActionsMenu",
      open: open || undefined,
      onBlur: (event) => {
        const next = event.relatedTarget;
        if (!next || !event.currentTarget.contains(next)) {
          event.currentTarget.removeAttribute("open");
        }
      }
    },
    h("summary", null, "Admin actions"),
    h(
      "div",
      { className: "adminActionsPanel" },
      h(
        "button",
        {
          type: "button",
          className: "btn btn-outline-secondary",
          onClick: (event) => {
            closeMenu(event);
            onEdit?.();
          },
          disabled: busy || regenerating
        },
        "Edit + reprint"
      ),
      h(
        "button",
        {
          type: "button",
          className: "btn btn-outline-secondary",
          onClick: (event) => {
            closeMenu(event);
            onQueue?.();
          },
          disabled: busy || queueing || regenerating
        },
        queueing ? "Adding..." : "Add to reprint queue"
      ),
      h(
        "button",
        {
          type: "button",
          className: "btn btn-outline-secondary",
          onClick: (event) => {
            closeMenu(event);
            onClaimants?.();
          },
          disabled: claimantBusy
        },
        claimantBusy ? "Loading claimants..." : claimantsLoaded ? "Hide claimants" : "View claimants"
      ),
      h(
        "button",
        {
          type: "button",
          className: "btn btn-outline-secondary",
          onClick: (event) => {
            closeMenu(event);
            onAssign?.();
          },
          disabled: busy
        },
        "Assign flag grid"
      ),
      h(
        "button",
        {
          type: "button",
          className: "btn btn-outline-secondary",
          onClick: (event) => {
            closeMenu(event);
            onRegenerate?.();
          },
          disabled: busy || regenerating || queueing
        },
        regenerating ? "Generating..." : "Regenerate PDF"
      ),
      h(
        "button",
        {
          type: "button",
          className: "btn btn-outline-danger",
          onClick: (event) => {
            closeMenu(event);
            onRemove?.();
          },
          disabled: busy || removing || !hasFlagGrid
        },
        removing ? "Removing..." : "Remove from flag grid"
      )
    )
  );
}
