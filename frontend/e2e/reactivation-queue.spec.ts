import { mkdirSync } from "node:fs";
import { expect, test, type Page, type Route } from "@playwright/test";

const rows = [
  {
    honoreeId: 41,
    name: "Ada Lovelace",
    rank: "Captain",
    branch: "Army",
    legacyKey: 88011,
    deactivatedUtc: "2024-05-01T15:00:00.000Z",
    deactivatedBy: "remover@example.test",
    reason: "Moved away",
    canReactivate: true,
    blockReason: null
  },
  {
    honoreeId: 42,
    name: "Grace Hopper",
    rank: "Rear Admiral",
    branch: "Navy",
    legacyKey: null,
    deactivatedUtc: "2024-06-02T15:00:00.000Z",
    deactivatedBy: "remover@example.test",
    reason: "Left the field",
    canReactivate: true,
    blockReason: null
  },
  {
    honoreeId: 43,
    name: "Alan Turing",
    rank: null,
    branch: "Army",
    legacyKey: null,
    deactivatedUtc: null,
    deactivatedBy: "Not recorded",
    reason: "Not recorded",
    canReactivate: true,
    blockReason: null
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

function matches(query: string) {
  const needle = query.trim().toLowerCase();
  return rows.filter((row) => {
    if (!needle) return true;
    if (row.name.toLowerCase().includes(needle)) return true;
    if ((row.branch || "").toLowerCase().includes(needle)) return true;
    if (String(row.honoreeId) === needle) return true;
    if (row.legacyKey != null && String(row.legacyKey) === needle) return true;
    return false;
  });
}

async function install(page: Page, roles: string[] = ["PFOH.Admin"], posts: string[] = []) {
  await page.addInitScript((account) => {
    sessionStorage.setItem("pfoh.e2eAccount", account);
  }, e2eAdmin(roles));
  await page.route("**/api/**", async (route: Route) => {
    const url = new URL(route.request().url());
    const path = url.pathname;
    if (route.request().method() === "POST") posts.push(path);
    const adminDenied = roles.length === 0;
    if (path.startsWith("/api/admin/") && adminDenied) {
      await route.fulfill({ status: 403, json: { message: "Not an administrator." } });
      return;
    }
    if (path === "/api/version") {
      await route.fulfill({ json: { version: "5.8.1" } });
      return;
    }
    if (path === "/api/telemetry-config") {
      await route.fulfill({ json: { enabled: false } });
      return;
    }
    if (path === "/api/admin/reactivation/eligible" && route.request().method() === "GET") {
      await route.fulfill({
        json: [{
          honoreeId: 7,
          name: "Riley Quinn",
          requesterEmail: "family@example.test",
          activeLinkExpiresUtc: null
        }]
      });
      return;
    }
    if (path === "/api/admin/reactivation/inactive" && route.request().method() === "GET") {
      const matched = matches(url.searchParams.get("q") || "");
      await route.fulfill({
        json: { items: matched, page: 1, pageSize: 25, total: matched.length }
      });
      return;
    }
    if (path === "/api/admin/reactivation/review-queue" && route.request().method() === "POST") {
      await route.fulfill({
        json: {
          results: [
            {
              honoreeId: 41,
              name: "Ada Lovelace",
              outcome: "reactivated",
              code: "ok",
              message: "Ada Lovelace was added to the review queue.",
              changeRequestId: 50
            },
            {
              honoreeId: 42,
              name: "Grace Hopper",
              outcome: "reactivated",
              code: "ok",
              message: "Grace Hopper was added to the review queue.",
              changeRequestId: 51
            },
            {
              honoreeId: 43,
              name: "Alan Turing",
              outcome: "reactivated",
              code: "ok",
              message: "Alan Turing was added to the review queue.",
              changeRequestId: 52
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

test("an administrator can search, multi-select, and confirm the review queue", async ({ page }) => {
  const posts: string[] = [];
  await install(page, ["PFOH.Admin"], posts);
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto("/admin/reactivation");
  await expect(page.getByRole("heading", { name: "Reactivation links" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Generate links" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Revoke selected" })).toBeVisible();
  await expect(page.getByLabel("Optional CSV")).toBeVisible();
  await expect(page.getByRole("row", { name: /Riley Quinn/ })).toBeVisible();

  await page.getByRole("tab", { name: "Review queue" }).click();
  await expect(page.getByRole("heading", { name: "Review queue" })).toBeVisible();
  await expect(page.getByRole("row", { name: /Ada Lovelace/ })).toBeVisible();
  await expect(page.getByRole("row", { name: /Alan Turing/ })).toContainText("Not recorded");
  await expect(page.getByRole("button", { name: "Generate links" })).toHaveCount(0);

  await page.getByLabel("Search by name, branch, or record id").fill("Navy");
  await page.getByRole("button", { name: "Search" }).click();
  await expect(page.getByRole("row", { name: /Grace Hopper/ })).toBeVisible();
  await expect(page.getByRole("row", { name: /Ada Lovelace/ })).toHaveCount(0);
  await expect(page.getByRole("row", { name: /Alan Turing/ })).toHaveCount(0);
  await page.screenshot({ path: "e2e-shots/reactivation-queue-search.png", fullPage: true });

  await page.getByLabel("Search by name, branch, or record id").fill("41");
  await page.getByRole("button", { name: "Search" }).click();
  await expect(page.getByRole("row", { name: /Ada Lovelace/ })).toBeVisible();
  await expect(page.getByRole("row", { name: /Grace Hopper/ })).toHaveCount(0);

  await page.getByLabel("Search by name, branch, or record id").fill("");
  await page.getByRole("button", { name: "Search" }).click();
  await expect(page.getByRole("row", { name: /Grace Hopper/ })).toBeVisible();

  await page.getByRole("checkbox", { name: "Select all visible records" }).check();
  await expect(page.getByRole("checkbox", { name: "Select Ada Lovelace" })).toBeChecked();
  await expect(page.getByRole("checkbox", { name: "Select Grace Hopper" })).toBeChecked();
  await expect(page.getByRole("checkbox", { name: "Select Alan Turing" })).toBeChecked();
  await page.getByRole("button", { name: "Add to review queue" }).click();
  const dialog = page.getByRole("dialog");
  await expect(dialog).toContainText("Add Ada Lovelace, Grace Hopper, and Alan Turing to the review queue?");
  await page.screenshot({ path: "e2e-shots/reactivation-queue-confirm.png", fullPage: true });
  await dialog.getByRole("button", { name: "Add to review queue" }).click();
  await expect(page.getByText("Ada Lovelace was added to the review queue.")).toBeVisible();
  expect(posts).toContain("/api/admin/reactivation/review-queue");
  expect(posts).not.toContain("/api/admin/reactivation/links");
});

test("the review queue fits a 390px screen", async ({ page }) => {
  await install(page);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/admin/reactivation");
  await page.getByRole("tab", { name: "Review queue" }).click();
  await expect(page.getByRole("heading", { name: "Review queue" })).toBeVisible();
  await expect(page.getByText("Ada Lovelace").first()).toBeVisible();
  const search = page.getByRole("button", { name: "Search" });
  const add = page.getByRole("button", { name: "Add to review queue" });
  const linksTab = page.getByRole("tab", { name: "Reactivation links" });
  const queueTab = page.getByRole("tab", { name: "Review queue" });
  await expectNoHorizontalOverflow(page);
  await expectTappable(search);
  await expectTappable(add);
  await expectTappable(linksTab);
  await expectTappable(queueTab);
  await page.screenshot({ path: "e2e-shots/reactivation-queue-390.png", fullPage: true });

  await page.getByRole("checkbox", { name: "Select Ada Lovelace" }).check();
  await add.click();
  const dialog = page.getByRole("dialog");
  await expect(dialog).toContainText("Add Ada Lovelace to the review queue?");
  await expectNoHorizontalOverflow(page);
  await expectTappable(dialog.getByRole("button", { name: "Add to review queue" }));
  await expectTappable(dialog.getByRole("button", { name: "Cancel" }));
  await page.screenshot({ path: "e2e-shots/reactivation-queue-confirm-390.png", fullPage: true });
});

test("a signed-in non-admin does not get the reactivation tools", async ({ page }) => {
  await install(page, []);
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto("/admin/reactivation");
  await expect(page.getByRole("heading", { name: "Administrator access required" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Review queue" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Add to review queue" })).toHaveCount(0);
  await expect(page.getByRole("link", { name: "Reactivation" })).toHaveCount(0);
});

async function expectNoHorizontalOverflow(page: Page) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(1);
}

async function expectTappable(locator: ReturnType<Page["getByRole"]>) {
  const box = await locator.boundingBox();
  expect(box).not.toBeNull();
  expect(box!.width).toBeGreaterThanOrEqual(44);
  expect(box!.height).toBeGreaterThanOrEqual(44);
}
