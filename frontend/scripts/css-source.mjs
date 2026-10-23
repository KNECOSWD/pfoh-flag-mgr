import { readFileSync } from "node:fs";

const order = [
  "tokens.css",
  "bootstrap-bridge.css",
  "base.css",
  "header.css",
  "footer.css",
  "search.css",
  "forms.css",
  "cards.css",
  "my-flags.css",
  "admin-shared.css",
  "admin-review.css",
  "admin-duplicates.css",
  "admin-print.css",
  "admin-flag-manager.css",
  "admin-removal.css",
  "flag-map.css",
  "kiosk.css"
];

export function cssSource() {
  return order
    .map((name) => readFileSync(new URL(`../src/styles/${name}`, import.meta.url), "utf8"))
    .join("\n");
}
