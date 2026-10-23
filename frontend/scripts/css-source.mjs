import { readFileSync } from "node:fs";

const order = [
  "tokens.css",
  "base.css",
  "header.css"
];

export function cssSource() {
  return order
    .map((name) => readFileSync(new URL(`../src/styles/${name}`, import.meta.url), "utf8"))
    .join("\n");
}
