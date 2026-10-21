import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { FindSearchFrame } from "../src/findSearchLayout.js";

const removed =
  "Search our honoree records—no account needed. To submit a new honoree record or suggest changes to an existing record, log in or create an account. All submissions and changes require administrator approval before they appear publicly.";

test("the Find page renders the heading, search box, and steps without the intro paragraph", () => {
  const html = renderToStaticMarkup(
    createElement(
      FindSearchFrame,
      {
        heading: "Search honoree records",
        help: "Search by name, military branch, rank, submitter’s name, or flag grid location. A flag grid location is the spot on the field, such as AA-10.",
        infoButton: createElement("button", { type: "button", "aria-label": "How this works" }, "i")
      },
      createElement(
        "form",
        { className: "searchBar nativeSearchBar" },
        createElement("input", {
          id: "honoree-search",
          type: "search",
          placeholder: "Enter a name, submitter, branch, rank, or flag grid (such as AA-10)",
          "aria-label": "Search honorees"
        })
      ),
      createElement(
        "section",
        { className: "inlineHowItWorks", "aria-label": "How to submit or suggest a change" },
        createElement("div", null, createElement("strong", null, "Search first")),
        createElement("div", null, createElement("strong", null, "Submit a record or suggest a change")),
        createElement("div", null, createElement("strong", null, "Administrator review"))
      )
    )
  );

  assert.equal(html.includes(removed), false);
  assert.equal(html.includes("no account needed"), false);
  assert.equal(html.includes("searchIntro"), false);
  assert.match(html, /<h2[^>]*>Search honoree records/);
  assert.match(html, /id="honoree-search"/);
  assert.match(html, /Search first/);
  assert.match(html, /Submit a record or suggest a change/);
  assert.match(html, /Administrator review/);
  assert.match(html, /class="inlineHowItWorks"/);

  const app = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");
  assert.equal(app.includes(removed), false);
  assert.equal(app.includes("searchIntro"), false);
  assert.equal(app.includes("no account needed"), false);
  assert.match(app, /<FindSearchFrame/);
  assert.match(app, /id="honoree-search"/);
  assert.match(app, /className="inlineHowItWorks"/);
  assert.match(app, /publicCopy\.stepSearchTitle/);
  assert.match(app, /publicCopy\.stepSubmitTitle/);
  assert.match(app, /publicCopy\.stepReviewTitle/);
});
