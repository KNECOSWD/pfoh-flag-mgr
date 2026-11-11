import { mkdirSync } from "node:fs";
import { expect, test, type Page, type Route } from "@playwright/test";

const rows = [
  {
    honoreeId: 1,
    name: "Ada Lovelace",
    rank: "Captain",
    branch: "Army",
    formerFlagGridId: 11,
    formerFlagGridName: "C-11",
    deactivatedUtc: "2024-05-01T15:00:00.000Z",
    deactivatedBy: "remover@example.test",
    reason: "Moved away",
    slotStatus: "free",
    slotDetail: null,
    canReactivate: true
  },
  {
    honoreeId: 2,
    name: "Grace Hopper",
    rank: "Rear Admiral",
    branch: "Navy",
    formerFlagGridId: 12,
    formerFlagGridName: "B-02",
    deactivatedUtc: "2024-06-02T15:00:00.000Z",
    deactivatedBy: "remover@example.test",
    reason: "Left the field",
    slotStatus: "taken",
    slotDetail: "It is assigned to Alex Kim.",
    canReactivate: true
  },
  {
    honoreeId: 3,
    name: "Alan Turing",
    rank: null,
    branch: "Army",
    formerFlagGridId: null,
    formerFlagGridName: null,
    deactivatedUtc: null,
    deactivatedBy: "Not recorded",
    reason: "Not recorded",
    slotStatus: "none",
    slotDetail: null,
    canReactivate: true
  }
];

function e2eAdmin(roles: string[] = ["PFOH.Admin"]) {
  return JSON.stringify({
    homeAccountId: "admin-1.e2e",
    localAccountId: "admin-1",
    environment: "e2e",
    tenantId: "e2e",
    username: "admin@example.com",
    name: "Example Admin",
    idTokenClaims: { roles }
  });
}

async function install(page: Page, roles: string[] = ["PFOH.Admin"]) {
  await page.addInitScript((account) => {
    sessionStorage.setItem("pfoh.e2eAccount", account);
  }, e2eAdmin(roles));
  await page.route("**/api/**", async (route: Route) => {
    const url = new URL(route.request().url());
    const path = url.pathname;
    const adminDenied = roles.length === 0;
    if (path.startsWith("/api/admin/") && adminDenied) {
      await route.fulfill({ status: 403, json: { message: "Not an administrator." } });
      return;
    }
    if (path === "/api/version") {
      await route.fulfill({ json: { version: "5.7.0" } });
      return;
    }
    if (path === "/api/telemetry-config") {
      await route.fulfill({ json: { enabled: false } });
      return;
    }
    if (path === "/api/admin/inactive-honorees" && route.request().method() === "GET") {
      const query = (url.searchParams.get("q") || "").trim().toLowerCase();
      const matched = rows.filter((row) => {
        if (!query) return true;
        const flag = (row.formerFlagGridName || "").toLowerCase().replace(/[^a-z0-9]/g, "");
        const compact = query.replace(/[^a-z0-9]/g, "");
        return row.name.toLowerCase().includes(query)
          || (row.branch || "").toLowerCase().includes(query)
          || flag === compact
          || (row.formerFlagGridName || "").toLowerCase().includes(query);
      });
      await route.fulfill({
        json: { items: matched, page: 1, pageSize: 25, total: matched.length }
      });
      return;
    }
    if (path === "/api/admin/inactive-honorees/free-slots") {
      await route.fulfill({
        json: {
          items: [
            { id: 20, flagGridName: "D-04" },
            { id: 21, flagGridName: "E-02" }
          ],
          truncated: false
        }
      });
      return;
    }
    if (path === "/api/admin/inactive-honorees/reactivate") {
      await route.fulfill({
        json: {
          results: [
            {
              honoreeId: 1,
              name: "Ada Lovelace",
              outcome: "reactivated",
              code: "ok",
              message: "Ada Lovelace is active on flag C-11.",
              flagGridId: 11,
              flagGridName: "C-11",
              reprintQueued: false,
              removalCanceled: true,
              pdfWarning: null
            }
          ]
        }
      });
      return;
    }
    if (path === "/api/admin/review/pending") {
      await route.fulfill({ json: [], headers: { "X-Open-Duplicate-Groups": "0" } });
      return;
    }
    if (path === "/api/admin/review/superseded") {
      await route.fulfill({ json: { items: [], page: 1, pageSize: 1, total: 0 } });
      return;
    }
    if (path === "/api/admin/removal-queue/count") {
      await route.fulfill({ json: { count: 0 } });
      return;
    }
    if (path === "/api/flag-claims/my" || path.startsWith("/api/lookups/") || path === "/api/admin/review/approved-reprint-queue" || path === "/api/admin/review/flag-positions" || path === "/api/admin/review/print-history") {
      await route.fulfill({ json: [] });
      return;
    }
    await route.fulfill({ json: {} });
  });
}

test.beforeAll(() => {
  mkdirSync("e2e-shots", { recursive: true });
});

test("an administrator can search, confirm, and resolve a flag conflict", async ({ page }) => {
  await install(page);
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto("/admin/inactive");
  const card = page.locator("#inactive-honorees-title");
  await expect(card).toBeVisible();
  await expect(page.getByRole("row", { name: /Ada Lovelace/ })).toBeVisible();
  await expect(page.getByRole("row", { name: /Alan Turing/ })).toContainText("Not recorded");
  await page.screenshot({ path: "e2e-shots/inactive-list.png", fullPage: true });

  await page.getByLabel("Search by flag, name, or branch").fill("C11");
  await page.getByRole("button", { name: "Search" }).click();
  await expect(page.getByRole("row", { name: /Ada Lovelace/ })).toBeVisible();
  await expect(page.getByRole("row", { name: /Grace Hopper/ })).toHaveCount(0);
  await expect(page.getByRole("row", { name: /Alan Turing/ })).toHaveCount(0);
  await page.screenshot({ path: "e2e-shots/inactive-search.png", fullPage: true });

  await page.getByLabel("Search by flag, name, or branch").fill("");
  await page.getByRole("button", { name: "Search" }).click();
  await expect(page.getByRole("row", { name: /Grace Hopper/ })).toBeVisible();

  await page.getByRole("row", { name: /Ada Lovelace/ }).getByRole("button", { name: "Restore" }).click();
  const dialog = page.getByRole("dialog");
  await expect(dialog).toContainText("Restore Ada Lovelace to flag C-11.");
  await page.screenshot({ path: "e2e-shots/inactive-confirm.png", fullPage: true });
  await dialog.getByRole("button", { name: "Cancel" }).click();
  await expect(dialog).toHaveCount(0);

  await page.getByRole("checkbox", { name: "Select Grace Hopper" }).check();
  await expect(page.getByText("Flag B-02 is not open. It is assigned to Alex Kim. Choose an open flag.").first()).toBeVisible();
  await expect(page.getByLabel("Open flag for Grace Hopper")).toBeVisible();
  await page.screenshot({ path: "e2e-shots/inactive-conflict.png", fullPage: true });
  await page.getByLabel("Open flag for Grace Hopper").selectOption("D-04");
  await page.getByRole("button", { name: "Restore selected honoree" }).click();
  await expect(dialog).toContainText("Restore Grace Hopper to flag D-04.");
});

test("a signed-in non-admin does not get the page or the nav entry", async ({ page }) => {
  await install(page, []);
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto("/admin/inactive");
  await expect(page.getByRole("heading", { name: "Administrator access required" })).toBeVisible();
  await expect(page.locator("#inactive-honorees-title")).toHaveCount(0);
  await expect(page.getByRole("link", { name: "Inactive honorees" })).toHaveCount(0);
});
