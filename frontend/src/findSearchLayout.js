import { createElement as h } from "react";

export function FindSearchFrame({ heading, help, infoButton, children }) {
  return h(
    "section",
    { id: "search", className: "card searchCard", "aria-label": "Search honoree records" },
    h(
      "div",
      { className: "sectionHeader searchHeader" },
      h(
        "div",
        null,
        h("h2", null, heading, infoButton ?? null),
        h("p", { className: "helperText" }, help)
      )
    ),
    children
  );
}
