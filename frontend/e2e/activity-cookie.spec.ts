import { mkdirSync } from "node:fs";
import { expect, test, type Page, type Route } from "@playwright/test";

const activity = {
  asOfUtc: "2026-06-15T15:30:00.0000000Z",
  accounts: { last7Days: 4, last30Days: 11, allTime: 80 },
  changeRequests: {
    last7Days: { total: 6, byStatus: [{ status: "Approved", count: 2 }, { status: "Submitted", count: 4 }] },
    last30Days: {
      total: 10,
      byStatus: [
        { status: "Approved", count: 3 },
        { status: "Rejected", count: 2 },
        { status: "Submitted", count: 5 }
      ]
    }
  },
  flagClaims: {
    last7Days: { total: 3, byStatus: [{ status: "Claimed", count: 2 }, { status: "Submitted", count: 1 }] },
    last30Days: {
      total: 5,
      byStatus: [
        { status: "Approved", count: 2 },
        { status: "Claimed", count: 2 },
        { status: "Submitted", count: 1 }
      ]
    }
  },
  receipts: { last7Days: 4, last30Days: 7 },
  daily: Array.from({ length: 30 }, (_, index) => {
    const day = new Date(Date.UTC(2026, 4, 17 + index));
    const date = day.toISOString().slice(0, 10);
    return {
      date,
      accounts: date === "2026-06-15" ? 2 : 0,
      changeRequests: date === "2026-06-15" ? 3 : 0,
      flagClaims: date === "2026-06-14" ? 1 : 0,
      receipts: date === "2026-06-15" ? 1 : 0
    };
  })
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
  const path = new URL(route.request().url()).pathname;
  if (path === "/api/version") {
    await route.fulfill({ json: { version: "5.4.0" } });
    return;
  }
  if (path === "/api/telemetry-config") {
    await route.fulfill({ json: { enabled: false } });
    return;
  }
  if (path === "/api/flag-claims/my" || path.startsWith("/api/lookups/")) {
    await route.fulfill({ json: [] });
    return;
  }
  if (path === "/api/admin/review/pending") {
    await route.fulfill({ json: [], headers: { "X-Open-Duplicate-Groups": "0" } });
    return;
  }
  if (path === "/api/admin/review/approved-reprint-queue" || path === "/api/admin/review/flag-positions") {
    await route.fulfill({ json: [] });
    return;
  }
  if (path === "/api/admin/removal-queue/count") {
    await route.fulfill({ json: { count: 0 } });
    return;
  }
  if (path === "/api/admin/activity") {
    await route.fulfill({ json: activity });
    return;
  }
  await route.fulfill({ json: {} });
}

async function installApi(page: Page) {
  await page.route("**/api/**", fulfillApi);
}

async function expectWithinViewport(page: Page, selector: string) {
  const box = await page.locator(selector).boundingBox();
  expect(box, selector).not.toBeNull();
  const viewport = page.viewportSize();
  expect(viewport).not.toBeNull();
  expect(box!.x).toBeGreaterThanOrEqual(-1);
  expect(box!.y).toBeGreaterThanOrEqual(-1);
  expect(box!.x + box!.width).toBeLessThanOrEqual((viewport?.width ?? 0) + 1);
  expect(box!.y + box!.height).toBeLessThanOrEqual((viewport?.height ?? 0) + 1);
}

test.beforeAll(() => {
  mkdirSync("e2e-shots", { recursive: true });
});

test("cookie note shows once at 390 and 1280 and stays dismissed", async ({ page }) => {
  await installApi(page);
  await page.route("**/api/telemetry-config", (route) => route.fulfill({
    json: { enabled: true, connectionString: "InstrumentationKey=00000000-0000-0000-0000-000000000000" }
  }));

  for (const width of [390, 1280]) {
    await page.setViewportSize({ width, height: width === 390 ? 844 : 800 });
    if (width === 390) await page.goto("/find");
    const note = page.locator("#cookie-note");
    await expect(note).toBeVisible();
    await expect(note).toContainText("We use cookies to count visits and see which pages are used, so we can improve this site. They are not used for ads, and we don't sell your information.");
    const ok = note.getByRole("button", { name: "OK" });
    await expect(ok).toBeVisible();
    await expectWithinViewport(page, "#cookie-note");
    const searchUsable = await page.getByRole("searchbox", { name: "Search honorees" }).evaluate((node) => {
      const rect = node.getBoundingClientRect();
      const hit = document.elementFromPoint(rect.left + 12, rect.top + rect.height / 2);
      return Boolean(hit && (hit === node || node.contains(hit)));
    });
    expect(searchUsable, `search stays usable at ${width}px`).toBe(true);
    const fits = await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1);
    expect(fits, `page fits at ${width}px`).toBe(true);
    await page.screenshot({ path: `e2e-shots/cookie-note-${width}.png`, fullPage: false });
  }

  await page.locator("#cookie-note").getByRole("button", { name: "OK" }).click();
  await expect(page.locator("#cookie-note")).toHaveCount(0);
  await page.reload();
  await expect(page.getByRole("searchbox", { name: "Search honorees" })).toBeVisible();
  await expect(page.locator("#cookie-note")).toHaveCount(0);
  await expect.poll(() => page.evaluate(() => localStorage.getItem("pfoh.cookieNoteDismissed"))).toBe("1");
});

test("telemetry stays off when the connection string is missing", async ({ page }) => {
  const outbound: string[] = [];
  page.on("request", (request) => {
    if (/visualstudio|applicationinsights|monitor\.azure/i.test(request.url())) outbound.push(request.url());
  });
  await installApi(page);
  await page.goto("/find");
  await expect(page.getByRole("searchbox", { name: "Search honorees" })).toBeVisible();
  await expect(page.locator("#cookie-note")).toHaveCount(0);
  expect(outbound).toEqual([]);
});

test("activity card shows aggregate counts at 390 and 1280", async ({ page }) => {
  await page.addInitScript((account) => {
    sessionStorage.setItem("pfoh.e2eAccount", account);
  }, e2eAdmin());
  await installApi(page);
  await page.goto("/admin/activity");
  const card = page.locator("#activity");
  await expect(card.getByRole("heading", { name: "Activity" })).toBeVisible();
  await expect(card).toContainText("Counts use UTC days.");
  await expect(card.getByRole("region", { name: "New accounts" })).toContainText("80");
  const requests = card.getByRole("region", { name: "Change requests" });
  await expect(requests).toContainText("Last 7 days total 6");
  await expect(requests).toContainText("Last 30 days total 10");
  await expect(requests.getByRole("row", { name: "Submitted 4" })).toBeVisible();
  await expect(requests.getByRole("row", { name: "Rejected 2" })).toBeVisible();
  await expect(card.getByRole("region", { name: "Flag claims" })).toContainText("Last 30 days total 5");
  await expect(card.getByRole("region", { name: "Submission receipts" })).toContainText("7");
  await expect(card.getByRole("table", { name: "Last 30 UTC days" }).locator("tbody tr")).toHaveCount(30);
  await expect(page.getByRole("link", { name: /azure portal|application insights/i })).toHaveCount(0);

  for (const width of [390, 1280]) {
    await page.setViewportSize({ width, height: width === 390 ? 844 : 900 });
    await card.scrollIntoViewIfNeeded();
    const fits = await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1);
    expect(fits, `activity page fits at ${width}px`).toBe(true);
    await page.screenshot({ path: `e2e-shots/activity-card-${width}.png`, fullPage: false });
  }
});
