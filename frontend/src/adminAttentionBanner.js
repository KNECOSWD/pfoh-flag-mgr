import { createElement as h } from "react";

/**
 * The existing administrator attention bar. Markup and classes match the bar
 * that already sits under the header.
 */
export function AdminAttentionBanner({ show, items, totalCount, activeRoute, onNavigate }) {
  if (!show || !items || items.length === 0) return null;

  return h(
    "section",
    { className: "adminAlertBanner", "aria-label": "Administrator notifications" },
    h(
      "div",
      { className: "adminAlertBannerText" },
      h("strong", null, "Admin attention needed"),
      h(
        "span",
        null,
        `${totalCount} item${totalCount === 1 ? "" : "s"} need attention.`
      )
    ),
    h(
      "div",
      { className: "adminAlertBannerLinks" },
      items.map((item) =>
        h(
          "a",
          {
            key: item.key,
            href: item.route,
            className: activeRoute === item.route ? "isActive" : "",
            onClick: (event) => onNavigate(event, item.route)
          },
          h("strong", null, item.count),
          h("span", null, item.label)
        )
      )
    )
  );
}
