import { mkdirSync } from "node:fs";
import { expect, test, type Locator, type Page, type Route } from "@playwright/test";

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

const flagPosition = {
  flagGridId: 1401,
  flagGridName: "A-14",
  rowLabel: "A",
  columnNumber: 14,
  isOpen: false,
  isReserved: false,
  honoreeId: 42,
  honoreeName: "Ada Mann",
  rank: "Captain",
  serviceBranchName: "Army"
};

const searchHit = {
  id: 42,
  fullName: "Ada Mann",
  firstName: "Ada",
  lastName: "Mann",
  rank: "Captain",
  kia: false,
  serviceBranchName: "Army",
  flagGrid: "A-14",
  sponsorName: "Example Family",
  isActive: true
};

const removalRow = {
  id: 9,
  honoreeId: 42,
  honoreeName: "Ada Mann",
  flagGridId: 1401,
  flagGridName: "A-14",
  source: "Manual",
  sourceDetail: "The family asked to take this flag down",
  createdUtc: "2026-11-01T21:00:00.0000000Z",
  createdBy: "matthew@planoflagsofhonor.com",
  loggedAlreadyDone: false
};

const printCard = {
  changeRequestId: 21,
  claimId: 31,
  honoreeId: 42,
  honoreeName: "Ada Mann",
  flagGridName: "A-14",
  serviceBranchName: "Army"
};

function e2eAdmin() {
  return JSON.stringify({
    homeAccountId: "admin-1.e2e",
    localAccountId: "admin-1",
    environment: "e2e",
    tenantId: "e2e",
    username: "admin@example.com",
    name: "Example Admin",
    idTokenClaims: { roles: ["PFOH.Admin"] }
  });
}

async function fulfillApi(route: Route) {
  const url = new URL(route.request().url());
  const path = url.pathname;
  if (path.includes("/flag-map/sections")) {
    await route.fulfill({ json: [{ section: "A", flagCount: 40, occupiedCount: 1 }] });
    return;
  }
  if (path.includes("/flag-map/search-index")) {
    await route.fulfill({ json: [mann] });
    return;
  }
  if (path.includes("/flag-map/field")) {
    await route.fulfill({ json: field });
    return;
  }
  if (path.includes("/flag-map/") && route.request().method() === "POST") {
    await route.fulfill({ json: { suggestions: [] } });
    return;
  }
  if (path === "/api/version") {
    await route.fulfill({ json: { version: "5.5.0" } });
    return;
  }
  if (path === "/api/flag-claims/my") {
    await route.fulfill({ json: [] });
    return;
  }
  if (path.startsWith("/api/lookups/")) {
    await route.fulfill({ json: [] });
    return;
  }
  if (path === "/api/admin/review/pending") {
    await route.fulfill({ json: [], headers: { "X-Open-Duplicate-Groups": "0" } });
    return;
  }
  if (path === "/api/admin/review/approved-reprint-queue") {
    await route.fulfill({ json: [printCard] });
    return;
  }
  if (path === "/api/admin/review/flag-positions") {
    await route.fulfill({ json: [flagPosition] });
    return;
  }
  if (path === "/api/admin/removal-queue/count") {
    await route.fulfill({ json: { count: 1 } });
    return;
  }
  if (path === "/api/admin/removal-queue") {
    const status = url.searchParams.get("status");
    await route.fulfill({ json: status === "done" ? [] : [removalRow] });
    return;
  }
  if (path === "/api/admin/print/history") {
    await route.fulfill({ json: { printed: [], removed: [] } });
    return;
  }
  if (path === "/api/admin/print/duplicate-reviews") {
    await route.fulfill({ json: [] });
    return;
  }
  if (path === "/api/honorees/search") {
    await route.fulfill({ json: [searchHit] });
    return;
  }
  if (path.includes("/removal-preview")) {
    await route.fulfill({
      json: {
        honoreeId: 42,
        honoreeName: "Ada Mann",
        flagGridId: 1401,
        flagGridName: "A-14",
        duplicates: [{
          honoreeId: 77,
          name: "Ada Mann",
          flagGridName: "B-04",
          rank: "Captain",
          serviceBranchName: "Army",
          reasons: "same full name and same service branch"
        }]
      }
    });
    return;
  }
  await route.fulfill({ json: {} });
}

async function installApi(page: Page) {
  await page.route("**/api/**", fulfillApi);
}

const CRIMSON = "rgb(155, 28, 49)";

async function expectOutlineDangerIsCrimson(button: Locator) {
  await expect.poll(() => button.evaluate((node) => getComputedStyle(node).color)).toBe(CRIMSON);
}

async function expectCancelDiffersFromRedDanger(page: Page, cancelName: string, dangerName: string) {
  const colors = await page.evaluate(({ cancelName, dangerName }) => {
    const buttons = [...document.querySelectorAll(".appConfirmModal .modalActions button")];
    const cancel = buttons.find((button) => button.textContent?.trim() === cancelName);
    const danger = buttons.find((button) => button.textContent?.trim() === dangerName);
    if (!cancel || !danger) return null;
    return {
      cancelBg: getComputedStyle(cancel).backgroundColor,
      dangerBg: getComputedStyle(danger).backgroundColor
    };
  }, { cancelName, dangerName });
  expect(colors, `${cancelName} and ${dangerName} are both in the dialog`).not.toBeNull();
  expect(colors!.cancelBg).not.toBe(colors!.dangerBg);
  const [red, green, blue] = (colors!.dangerBg.match(/\d+/g) ?? []).map(Number);
  expect(red, colors!.dangerBg).toBeGreaterThan(120);
  expect(green, colors!.dangerBg).toBeLessThan(60);
  expect(blue, colors!.dangerBg).toBeLessThan(80);
}

test("typing Mann shows a clickable suggestion above the map and focuses that flag", async ({ page }) => {
  await installApi(page);
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
  await page.screenshot({ path: "e2e-shots/flag-map-typeahead.png", fullPage: false });

  await list.getByRole("option").first().click();
  await expect(page).toHaveURL(/flag=1401/);
  await expect(page.locator(".publicFlagMarker.isHighlighted")).toBeVisible();
  await expect(page.getByRole("heading", { name: "Ada Mann" })).toBeVisible();
});

test.describe("administrator screens", () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript((account) => {
      sessionStorage.setItem("pfoh.e2eAccount", account);
    }, e2eAdmin());
    await installApi(page);
  });

  test("Flag Manager remove dialog is on top and accepts a reason at phone, tablet, and desktop widths", async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto("/admin/flag-map");
    await page.getByRole("gridcell", { name: /A-14 occupied by Ada Mann/ }).click();
    await page.getByRole("button", { name: "Remove from flag grid" }).click();
    const reason = page.locator("#remove-grid-reason");
    await expect(reason).toBeVisible();
    await expect(page.getByText("Other active honorees have a similar name or details on a different grid.")).toBeVisible();

    for (const width of [390, 768, 1280]) {
      await page.setViewportSize({ width, height: 900 });
      await reason.click();
      await reason.fill("Family request");
      await expect(reason).toHaveValue("Family request");
      const usable = await reason.evaluate((node) => {
        const rect = node.getBoundingClientRect();
        const hit = document.elementFromPoint(rect.left + rect.width / 2, rect.top + Math.min(24, rect.height / 2));
        return Boolean(hit && (hit === node || node.contains(hit)));
      });
      expect(usable, `reason box is covered at ${width}px`).toBe(true);
      if (width === 390 || width === 1280) {
        await expectCancelDiffersFromRedDanger(page, "Cancel", "Not a duplicate");
      }
    }

    await page.screenshot({ path: "e2e-shots/remove-from-grid-dialog.png", fullPage: false });
  });

  test("Search shows the admin actions menu and the removal queue shows Restore", async ({ page }) => {
    mkdirSync("e2e-shots", { recursive: true });
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto("/find");
    await page.getByRole("searchbox", { name: "Search honorees" }).fill("Mann");
    await page.getByRole("button", { name: "Search", exact: true }).click();
    await page.getByText("Admin actions", { exact: true }).click();
    const removeAction = page.getByRole("button", { name: "Remove from flag grid" });
    await expect(removeAction).toBeVisible();
    await expectOutlineDangerIsCrimson(removeAction);
    await removeAction.scrollIntoViewIfNeeded();
    await page.screenshot({ path: "e2e-shots/find-admin-actions.png", fullPage: true });

    await page.goto("/admin/removal-queue");
    await expect(page.getByRole("button", { name: "Restore" })).toBeVisible();
    await page.screenshot({ path: "e2e-shots/removal-queue-restore.png", fullPage: false });

    await page.goto("/admin/printing");
    await page.locator("#reprint-queue tbody input[type=checkbox]").check();
    const removeSelected = page.getByRole("button", { name: "Remove selected" });
    await expect(removeSelected).toBeEnabled();
    await expectOutlineDangerIsCrimson(removeSelected);
    await removeSelected.click();
    await expect(page.locator("#reprint-remove-reason")).toBeVisible();
    await page.locator("#reprint-remove-reason").fill("Already handed to the family");
    await expect(page.getByRole("button", { name: "Remove from queue" })).toBeEnabled();
    for (const width of [390, 1280]) {
      await page.setViewportSize({ width, height: 900 });
      await expectCancelDiffersFromRedDanger(page, "Cancel", "Remove from queue");
    }
  });
});
