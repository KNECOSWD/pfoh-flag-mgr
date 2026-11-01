import { mkdirSync } from "node:fs";
import { expect, test } from "@playwright/test";
import { createElement as h } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { AdminHonoreeActions } from "../src/adminHonoreeActions.js";
import { RemoveFromGridDialog } from "../src/removeFromGridDialog.js";
import { RemovalQueueScreen } from "../src/removalQueue.js";

const mann = {
  honoreeId: 42,
  flagGridId: 1401,
  flagGridName: "A-14",
  section: "A",
  columnNumber: 14,
  displayName: "Ada Mann",
  firstName: "Ada",
  lastName: "Mann",
  rank: "Captain",
  serviceBranchName: "Army",
  status: "Occupied"
};

const field = {
  sections: [
    {
      section: "A",
      flagCount: 40,
      occupiedCount: 1,
      seats: [mann]
    }
  ]
};

test("typing Mann shows a clickable suggestion above the map and focuses that flag", async ({ page }) => {
  await page.route("**/api/public/flag-map/**", async (route) => {
    const url = route.request().url();
    if (url.includes("/sections/") || url.endsWith("/sections")) {
      await route.fulfill({
        json: [{ section: "A", flagCount: 40, occupiedCount: 1 }]
      });
      return;
    }
    if (url.includes("search-index")) {
      await route.fulfill({ json: [mann] });
      return;
    }
    if (url.includes("/field")) {
      await route.fulfill({ json: field });
      return;
    }
    if (route.request().method() === "POST") {
      await route.fulfill({ json: { suggestions: [] } });
      return;
    }
    await route.fulfill({ json: {} });
  });
  await page.route("**/api/version", async (route) => {
    await route.fulfill({ json: { version: "5.2.0" } });
  });

  await page.goto("/flag-map");
  const search = page.getByRole("combobox", { name: "Search honorees" });
  await search.click();
  await search.fill("Mann");

  const list = page.getByRole("listbox");
  await expect(list).toBeVisible();
  await expect(list.getByRole("option").first()).toContainText("Ada Mann");

  const topmost = await list.evaluate((node) => {
    const rect = node.getBoundingClientRect();
    const hit = document.elementFromPoint(rect.left + rect.width / 2, rect.top + rect.height / 2);
    const style = getComputedStyle(node);
    return {
      insideList: Boolean(hit && node.contains(hit)),
      position: style.position,
      zIndex: style.zIndex
    };
  });
  expect(topmost.insideList).toBe(true);
  expect(topmost.position).toBe("fixed");
  expect(Number(topmost.zIndex)).toBeGreaterThanOrEqual(30);

  mkdirSync("e2e-shots", { recursive: true });
  await page.screenshot({
    path: "e2e-shots/flag-map-typeahead.png",
    fullPage: false
  });

  await list.getByRole("option").first().click();
  await expect(page).toHaveURL(/flag=1401/);
  await expect(page.locator(".publicFlagMarker.isHighlighted")).toBeVisible();
  await expect(page.getByRole("heading", { name: "Ada Mann" })).toBeVisible();

  const dialog = renderToStaticMarkup(h(RemoveFromGridDialog, {
    name: "Ada Mann",
    gridName: "A-14",
    reason: "The family asked to take this flag down",
    duplicates: [{
      honoreeId: 77,
      name: "Ada Mann",
      flagGridName: "B-04",
      rank: "Captain",
      serviceBranchName: "Army",
      reasons: "same full name and same service branch"
    }],
    loading: false,
    error: "",
    busy: false
  }));
  const actions = renderToStaticMarkup(h(AdminHonoreeActions, {
    open: true,
    busy: false,
    regenerating: false,
    queueing: false,
    claimantBusy: false,
    claimantsLoaded: false,
    hasFlagGrid: true,
    removing: false
  }));
  const queue = renderToStaticMarkup(h(RemovalQueueScreen, {
    status: "open",
    openItems: [{
      id: 9,
      honoreeName: "Ada Mann",
      flagGridName: "A-14",
      source: "Manual",
      sourceDetail: "The family asked to take this flag down",
      createdUtc: "2026-11-01T21:00:00.000Z",
      createdBy: "matthew@planoflagsofhonor.com"
    }],
    doneItems: [],
    loaded: true,
    loadError: "",
    busy: false,
    error: "",
    selectedIds: [],
    onToggle: () => {},
    onToggleAll: () => {},
    onMarkDone: () => {},
    onRestore: () => {},
    onRefresh: () => {},
    onShowOpen: () => {},
    onShowDone: () => {},
    onNavigate: () => {}
  }));

  async function shot(html: string, file: string) {
    await page.evaluate((markup) => {
      document.body.innerHTML = markup;
      document.body.style.background = "#f7f1e4";
      document.body.style.minHeight = "100vh";
    }, `<div class="pageBody" style="padding:24px">${html}</div>`);
    await page.screenshot({ path: `e2e-shots/${file}`, fullPage: true });
  }

  await shot(dialog, "remove-from-grid-dialog.png");
  await shot(`<section class="card" style="max-width:420px;padding:16px"><h2>Ada Mann</h2>${actions}</section>`, "find-admin-actions.png");
  await shot(queue, "removal-queue-restore.png");
});
