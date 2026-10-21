import { createElement as h } from "react";

function iconSvg(paths) {
  return h(
    "svg",
    {
      viewBox: "0 0 24 24",
      width: "22",
      height: "22",
      "aria-hidden": "true",
      focusable: "false",
      fill: "none",
      stroke: "currentColor",
      strokeWidth: "2.2",
      strokeLinecap: "round",
      strokeLinejoin: "round"
    },
    paths.map((d) => h("path", { key: d, d }))
  );
}

const ICONS = {
  back: ["M15 5 8 12l7 7", "M8 12h11"],
  refresh: ["M20 12a8 8 0 1 1-2.3-5.6", "M20 4.5V9h-4.5"]
};

/**
 * Compact icon button shared by Back and Refresh on the duplicate screens.
 * @param {{ label: string, kind: "back" | "refresh", disabled?: boolean, onClick?: () => void, className?: string }} props
 */
export function DuplicateIconButton({ label, kind, disabled, onClick, className }) {
  return h(
    "button",
    {
      type: "button",
      className: ["duplicateIconButton", className].filter(Boolean).join(" "),
      title: label,
      "aria-label": label,
      disabled,
      onClick
    },
    iconSvg(ICONS[kind] ?? ICONS.back)
  );
}

/**
 * @param {{ onClick?: () => void }} props
 */
export function DuplicateHistoryLink({ onClick }) {
  return h(
    "button",
    { type: "button", className: "duplicateTextLink", onClick },
    "History"
  );
}
