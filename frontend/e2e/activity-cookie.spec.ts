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

const CONNECTION = "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=http://127.0.0.1:4173/ai-ingest/";

async function fulfillApi(route: Route, telemetryEnabled = false) {
  const path = new URL(route.request().url()).pathname;
  if (path === "/api/version") {
    await route.fulfill({ json: { version: "5.4.0" } });
    return;
  }
  if (path === "/api/telemetry-config") {
    await route.fulfill({
      json: telemetryEnabled ? { enabled: true, connectionString: CONNECTION } : { enabled: false }
    });
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

async function installApi(page: Page, telemetryEnabled = false) {
  await page.route("**/api/**", (route) => fulfillApi(route, telemetryEnabled));
  await page.route("**/ai-ingest/**", (route) => route.fulfill({ status: 200, contentType: "application/json", body: "{}" }));
}

async function reachAboveNote(page: Page, selector: string) {
  const target = page.locator(selector).first();
  const note = page.locator("#cookie-note");
  const insideMap = await target.evaluate((node) => Boolean(node.closest(".publicFlagMap")));
  if (!insideMap) {
    await target.scrollIntoViewIfNeeded();
    for (let step = 0; step < 6; step += 1) {
      const current = await target.boundingBox();
      const noteBox = await note.boundingBox();
      if (current && noteBox && current.y + current.height <= noteBox.y + 1) break;
      await page.mouse.move(24, 160);
      await page.mouse.wheel(0, 160);
    }
  }
  const placed = { box: await target.boundingBox(), noteBox: await note.boundingBox() };
  expect(placed?.box, selector).not.toBeNull();
  expect(placed?.noteBox, "cookie note").not.toBeNull();
  expect(placed!.box!.y + placed!.box!.height).toBeLessThanOrEqual(placed!.noteBox!.y + 1);
  expect(placed!.box!.y).toBeGreaterThanOrEqual(-1);
  const usable = await target.evaluate((node) => {
    const rect = node.getBoundingClientRect();
    const hit = document.elementFromPoint(rect.left + Math.min(12, rect.width / 2), rect.top + rect.height / 2);
    return Boolean(hit && (hit === node || node.contains(hit)));
  });
  expect(usable, selector).toBe(true);
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
  await installApi(page, true);

  for (const width of [390, 1280]) {
    await page.setViewportSize({ width, height: width === 390 ? 844 : 800 });
    if (width === 390) await page.goto("/find");
    else await page.evaluate(() => window.scrollTo(0, 0));
    const note = page.locator("#cookie-note");
    await expect(note).toBeVisible();
    await expect(note).toContainText("The counts are stored with Microsoft Azure.");
    const ok = note.getByRole("button", { name: "OK" });
    const decline = note.getByRole("button", { name: "No thanks" });
    await expect(decline).toBeVisible();
    await expect(decline).not.toHaveClass(/btn-primary/);
    await expect(ok).toBeVisible();
    await expect(ok).toHaveClass(/btn-outline-secondary/);
    await expect(ok).not.toHaveClass(/btn-primary/);
    await expect(page.locator(".btn-primary")).toHaveCount(1);
    const noteBox = await note.boundingBox();
    expect(noteBox?.height ?? 999, `cookie note stays compact at ${width}px`).toBeLessThan(140);
    await expectWithinViewport(page, "#cookie-note");
    const searchUsable = await page.getByRole("searchbox", { name: "Search honorees" }).evaluate((node) => {
      const rect = node.getBoundingClientRect();
      const hit = document.elementFromPoint(rect.left + 12, rect.top + rect.height / 2);
      return Boolean(hit && (hit === node || node.contains(hit)));
    });
    expect(searchUsable, `search stays usable at ${width}px`).toBe(true);
    const room = await page.evaluate(() => {
      const notice = document.getElementById("cookie-note");
      const spacer = document.getElementById("cookie-note-spacer");
      if (!notice || !spacer) return -1;
      return spacer.getBoundingClientRect().height - notice.getBoundingClientRect().height;
    });
    expect(room, `page keeps room for the notice at ${width}px`).toBeGreaterThanOrEqual(-1);
    expect(room, `spacer matches the notice at ${width}px`).toBeLessThan(2);
    await reachAboveNote(page, "a[href='https://planoflagsofhonor.com/about-pfoh/']");
    const fits = await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1);
    expect(fits, `page fits at ${width}px`).toBe(true);
    await page.screenshot({ path: `e2e-shots/cookie-note-${width}.png`, fullPage: false });
  }

  await page.locator("#cookie-note").getByRole("button", { name: "OK" }).click();
  await expect(page.locator("#cookie-note")).toHaveCount(0);
  await page.reload();
  await expect(page.getByRole("searchbox", { name: "Search honorees" })).toBeVisible();
  await expect(page.locator("#cookie-note")).toHaveCount(0);
  await expect.poll(() => page.evaluate(() => localStorage.getItem("pfoh.cookieChoice"))).toBe("accepted");
});

test("enabled false on a fresh profile shows no note and sends no telemetry", async ({ page }) => {
  const outbound: string[] = [];
  page.on("request", (request) => {
    const url = request.url();
    if (/visualstudio|applicationinsights|monitor\.azure|\/v2\/track|ai-ingest/i.test(url)) outbound.push(url);
  });
  await installApi(page, false);
  const config = page.waitForResponse((response) => response.url().includes("/api/telemetry-config"));
  await page.goto("/find");
  await config;
  await expect(page.getByRole("searchbox", { name: "Search honorees" })).toBeVisible();
  const appeared = await page.locator("#cookie-note").waitFor({ state: "visible", timeout: 1000 }).then(() => true, () => false);
  expect(appeared, "cookie note stays hidden when telemetry is off").toBe(false);
  expect(outbound).toEqual([]);
  const cookies = await page.context().cookies();
  expect(cookies.filter((cookie) => cookie.name.startsWith("ai_"))).toEqual([]);
  expect(await page.evaluate(() => document.cookie)).not.toMatch(/(?:^|;\s*)ai_/);
});

test("the flag map legend stays reachable above the cookie note", async ({ page }) => {
  await installApi(page, true);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/flag-map");
  await expect(page.locator("#cookie-note")).toBeVisible();
  await expect(page.getByRole("list", { name: "Spot key" })).toBeVisible();
  await reachAboveNote(page, ".publicFlagLegend");
  for (const selector of [".publicFlagRowLabel", ".publicFlagColumnLabel"]) {
    if (await page.locator(selector).count() === 0) continue;
    await reachAboveNote(page, selector);
  }
});

test("activity card shows aggregate counts at 390 and 1280", async ({ page }) => {
  await page.addInitScript((account) => {
    sessionStorage.setItem("pfoh.e2eAccount", account);
  }, e2eAdmin());
  await installApi(page);
  await page.goto("/admin/activity");
  await expect(page.locator("#cookie-note")).toHaveCount(0);
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
