import { createElement as h } from "react";

/** End of April 21, 2027 in Central Time (CDT, UTC-5). Shown while now is earlier. */
export const TEN_YEARS_BANNER_END = "2027-04-22T05:00:00.000Z";

export function showTenYearsBanner(now = new Date()) {
  const instant = now instanceof Date ? now : new Date(now);
  return instant.getTime() < Date.parse(TEN_YEARS_BANNER_END);
}

export function TenYearsBanner() {
  return h(
    "section",
    { className: "anniversaryBanner", "aria-label": "10 Years of Honor" },
    h(
      "div",
      { className: "anniversaryBannerInner" },
      h("span", { className: "anniversaryBadge", "aria-hidden": "true" }, "10"),
      h(
        "p",
        { className: "anniversaryText" },
        h("strong", null, "10 Years of Honor"),
        h("span", { className: "anniversaryStars", "aria-hidden": "true" }, "★ ★ ★"),
        h("span", null, "Celebrating the 10th year of Plano Flags of Honor.")
      )
    )
  );
}
