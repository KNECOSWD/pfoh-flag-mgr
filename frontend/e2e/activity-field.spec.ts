import { mkdirSync } from "node:fs";
import { expect, test, type Page, type Route } from "@playwright/test";

const END = "2026-06-15";
const SPAN = 60;

function isoDays(count: number) {
  const [year, month, day] = END.split("-").map(Number);
  const days: string[] = [];
  for (let index = count - 1; index >= 0; index -= 1) {
    days.push(new Date(Date.UTC(year, month - 1, day - index)).toISOString().slice(0, 10));
  }
  return days;
}

const dates = isoDays(SPAN);

function activity(pageViews?: { total: number; daily: { date: string; pageViews: number }[]; topPages: { name: string; pageViews: number }[] } | null) {
  return {
    asOfUtc: "2026-06-15T15:30:00.0000000Z",
    accounts: { last7Days: 4, last30Days: 11, allTime: 80 },
    changeRequests: {
      last7Days: { total: 6, byStatus: [{ status: "Approved", count: 2 }, { status: "Submitted", count: 4 }] },
      last30Days: { total: 10, byStatus: [{ status: "Approved", count: 3 }, { status: "Submitted", count: 7 }] }
    },
    flagClaims: {
      last7Days: { total: 3, byStatus: [{ status: "Claimed", count: 3 }] },
      last30Days: { total: 5, byStatus: [{ status: "Claimed", count: 5 }] }
    },
    receipts: { last7Days: 4, last30Days: 7 },
    daily: dates.slice(-30).map((date) => ({ date, accounts: date === END ? 2 : 0, changeRequests: 0, flagClaims: 0, receipts: 0 })),
    registrations: dates.map((date) => ({ date, count: date === END ? 2 : 0 })),
    ...(pageViews === undefined ? {} : { pageViews })
  };
}

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

async function install(page: Page, body: unknown) {
  await page.addInitScript((account) => {
    sessionStorage.setItem("pfoh.e2eAccount", account);
  }, e2eAdmin());
  await page.route("**/api/**", async (route: Route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === "/api/version") {
      await route.fulfill({ json: { version: "5.5.0" } });
      return;
    }
    if (path === "/api/telemetry-config") {
      await route.fulfill({ json: { enabled: false } });
      return;
    }
    if (path === "/api/admin/activity") {
      await route.fulfill({ json: body });
      return;
    }
    if (path === "/api/admin/review/pending") {
      await route.fulfill({ json: [], headers: { "X-Open-Duplicate-Groups": "0" } });
      return;
    }
    if (path === "/api/flag-claims/my" || path.startsWith("/api/lookups/") || path === "/api/admin/review/approved-reprint-queue" || path === "/api/admin/review/flag-positions") {
      await route.fulfill({ json: [] });
      return;
    }
    if (path === "/api/admin/removal-queue/count") {
      await route.fulfill({ json: { count: 0 } });
      return;
    }
    await route.fulfill({ json: {} });
  });
}

const days = (page: Page) => page.locator("#activity").getByRole("button", { name: /new,/ });

test.beforeAll(() => {
  mkdirSync("e2e-shots", { recursive: true });
});

test("range switch, keyboard detail, and screenshots", async ({ page }) => {
  await install(page, activity());
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto("/admin/activity");
  const card = page.locator("#activity");
  await expect(card.getByRole("heading", { name: "Activity" })).toBeVisible();
  await expect(days(page)).toHaveCount(42);
  const detail = page.locator("#activity-day-detail");
  const total = (await card.getByText("Accounts in total", { exact: true }).evaluate((node) => node.previousElementSibling?.textContent || "")).trim();
  async function expectRunningTotal() {
    await expect(card.locator("svg")).toContainText(`Running total: ${total}`);
  }
  await expect(detail).toContainText("Jun 15:");
  await expectRunningTotal();

  await card.getByRole("button", { name: "2 weeks", exact: true }).click();
  await expect(days(page)).toHaveCount(14);
  await expect(detail).toContainText("Jun 15:");
  await expectRunningTotal();
  await card.getByRole("button", { name: "All", exact: true }).click();
  await expect(days(page)).toHaveCount(SPAN);
  await expect(detail).toContainText("Jun 15:");
  await expectRunningTotal();
  await card.getByRole("button", { name: "6 weeks", exact: true }).click();
  await expect(days(page)).toHaveCount(42);
  await expectRunningTotal();

  await days(page).last().focus();
  await expect(detail).toContainText("Jun 15: 2 new");
  await page.keyboard.press("ArrowLeft");
  await expect(detail).toContainText("in total");
  await expect(detail).not.toContainText("Jun 15: 2 new");
  await days(page).first().click();
  await expect(detail).toContainText("new,");

  const contrast = await detail.evaluate((node) => {
    const parse = (value: string) => value.match(/rgba?\(([^)]+)\)/)?.[1].split(",").map((part) => Number(part.trim())) ?? null;
    const lin = (channel: number) => {
      const value = channel / 255;
      return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
    };
    const lum = (rgb: number[]) => 0.2126 * lin(rgb[0]) + 0.7152 * lin(rgb[1]) + 0.0722 * lin(rgb[2]);
    let current: Element | null = node;
    const fg = parse(getComputedStyle(node).color);
    let bg: number[] | null = null;
    while (current) {
      const parsed = parse(getComputedStyle(current).backgroundColor);
      if (parsed && parsed[3] !== 0) {
        bg = parsed;
        break;
      }
      current = current.parentElement;
    }
    if (!fg || !bg) return 0;
    const lighter = Math.max(lum(fg), lum(bg));
    const darker = Math.min(lum(fg), lum(bg));
    return (lighter + 0.05) / (darker + 0.05);
  });
  expect(contrast).toBeGreaterThanOrEqual(4.5);
  await expect(card.locator(".btn-primary")).toHaveCount(1);
  await card.getByRole("button", { name: "6 weeks", exact: true }).click();
  await expect(detail).toContainText("Jun 15:");

  for (const width of [390, 1280]) {
    await page.setViewportSize({ width, height: width === 390 ? 844 : 900 });
    const fits = await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1);
    expect(fits, `activity field fits at ${width}px`).toBe(true);
    await page.screenshot({ path: `e2e-shots/activity-field-${width}.png`, fullPage: false });
  }
});

test("the flag field stays readable at 390", async ({ page }) => {
  await install(page, activity());
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/admin/activity");
  await expect(page.locator("#activity .activity-flag").first()).toBeAttached();
  const metrics = await page.locator("#activity").evaluate(() => {
    const label = document.querySelector("#activity svg text");
    const flag = document.querySelector("#activity .activity-flag");
    const day = document.querySelector("#activity .activity-day-hit");
    if (!label || !flag || !day) return null;
    const ctm = (label as SVGGraphicsElement).getScreenCTM();
    const scale = ctm ? Math.hypot(ctm.a, ctm.b) : 1;
    const font = parseFloat(getComputedStyle(label).fontSize) * scale;
    return {
      font,
      day: day.getBoundingClientRect().width,
      flag: flag.getBoundingClientRect().height,
      pageOverflow: document.documentElement.scrollWidth > window.innerWidth + 1
    };
  });
  expect(metrics).not.toBeNull();
  expect(metrics!.font).toBeGreaterThanOrEqual(12);
  expect(metrics!.day).toBeGreaterThanOrEqual(24);
  expect(metrics!.flag).toBeGreaterThanOrEqual(8);
  expect(metrics!.pageOverflow).toBe(false);
});

test("reduced motion keeps the flags still", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await install(page, activity());
  await page.goto("/admin/activity");
  await expect(page.locator(".activity-flag").first()).toBeVisible();
  const animation = await page.locator(".activity-flag").first().evaluate((node) => getComputedStyle(node).animationName);
  expect(animation).toBe("none");
});

test("page views show when sent and stay hidden when absent", async ({ page }) => {
  await install(page, activity({
    total: 12,
    daily: [{ date: "2026-11-04", pageViews: 12 }],
    topPages: [{ name: "/find", pageViews: 9 }, { name: "/flag-map", pageViews: 3 }]
  }));
  await page.goto("/admin/activity");
  const views = page.getByRole("region", { name: "Page views since Nov 4, 2026" });
  await expect(views).toBeVisible();
  await expect(views).toContainText("12");
  await expect(views).toContainText("/find");
  await expect(views).not.toContainText(/visitor|opt-in/i);

  await install(page, activity(null));
  await page.goto("/admin/activity");
  await expect(page.getByRole("region", { name: "Page views since Nov 4, 2026" })).toHaveCount(0);
  await expect(page.locator("#activity")).toContainText("80");
});
