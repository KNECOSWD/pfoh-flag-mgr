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
      await route.fulfill({ json: { version: "5.7.0" } });
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
    if (path === "/api/admin/review/superseded") {
      await route.fulfill({ json: { items: [], page: 1, pageSize: 1, total: 0 } });
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

async function expectChartFills(page: Page, dayCount: number) {
  await expect.poll(async () => page.locator("#activity-chart").evaluate((node, count) => {
    const svg = node.querySelector("svg");
    const hits = node.querySelectorAll(".activity-day-hit");
    const hit = hits[0];
    if (!svg || !hit) return false;
    const host = node.getBoundingClientRect().width;
    const svgWidth = svg.getBoundingClientRect().width;
    const hitWidth = hit.getBoundingClientRect().width;
    const coverage = host > 0 ? (hitWidth * hits.length) / host : 0;
    return svgWidth > host * 0.95
      && hits.length === count
      && coverage > 0.55
      && node.scrollWidth - node.clientWidth <= 1
      && document.documentElement.scrollWidth <= window.innerWidth + 1;
  }, dayCount)).toBe(true);
}

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

  await expect(card.getByRole("slider")).toHaveCount(2);
  await card.getByRole("button", { name: "2 weeks", exact: true }).click();
  await expect(days(page)).toHaveCount(14);
  await expect(detail).toContainText("Jun 15:");
  await expectRunningTotal();
  await expectChartFills(page, 14);
  const twoWeekColumn = await page.locator("#activity-chart .activity-day-hit").first().evaluate((node) => node.getBoundingClientRect().width);
  expect(twoWeekColumn).toBeGreaterThan(40);
  await card.getByRole("button", { name: "All", exact: true }).click();
  await expect(days(page)).toHaveCount(SPAN);
  await expect(detail).toContainText("Jun 15:");
  await expectRunningTotal();
  await expectChartFills(page, SPAN);
  await card.getByRole("button", { name: "6 weeks", exact: true }).click();
  await expect(days(page)).toHaveCount(42);
  await expectRunningTotal();
  await expectChartFills(page, 42);

  await days(page).last().focus();
  await expect(detail).toContainText("Jun 15: 2 new");
  await page.keyboard.press("ArrowLeft");
  await expect(detail).toContainText("in total");
  await expect(detail).not.toContainText("Jun 15: 2 new");
  await days(page).first().click();
  await expect(page.getByRole("dialog")).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(page.getByRole("dialog")).toHaveCount(0);
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

test("the chart fills the card at 390", async ({ page }) => {
  await install(page, activity());
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/admin/activity");
  await expect(days(page)).toHaveCount(42);
  await expectChartFills(page, 42);
  const font = await page.locator("#activity-chart svg text").first().evaluate((node) => {
    const ctm = (node as SVGGraphicsElement).getScreenCTM();
    const scale = ctm ? Math.hypot(ctm.a, ctm.b) : 1;
    return parseFloat(getComputedStyle(node).fontSize) * scale;
  });
  expect(font).toBeGreaterThanOrEqual(12);
  await page.getByRole("button", { name: "2 weeks", exact: true }).click();
  await expectChartFills(page, 14);
  await page.getByRole("button", { name: "All", exact: true }).click();
  await expectChartFills(page, SPAN);
});

test("reduced motion keeps the chart still", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await install(page, activity());
  await page.goto("/admin/activity");
  const chart = page.locator("#activity-chart");
  await expect(chart).toHaveAttribute("data-chart-motion", "off");
  await expect(days(page).last()).toBeVisible();
  const animated = await chart.evaluate((node) => Array.from(node.querySelectorAll("*")).some((el) => {
    const style = getComputedStyle(el);
    const duration = style.transitionDuration.split(",")[0]?.trim() ?? "0s";
    return style.animationName !== "none" || (duration !== "0s" && style.transitionProperty !== "none");
  }));
  expect(animated).toBe(false);
  await days(page).last().click();
  const dialog = page.getByRole("dialog");
  await expect(dialog).toBeVisible();
  await expect(dialog).toHaveCSS("transition-property", "none");
});

test("the range handles update counts and a day opens the side panel", async ({ page }) => {
  await install(page, activity({
    total: 12,
    daily: [{ date: END, pageViews: 5 }],
    topPages: [{ name: "/find", pageViews: 5 }]
  }));
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto("/admin/activity");
  const card = page.locator("#activity");
  const detail = page.locator("#activity-day-detail");
  const label = page.locator("#activity-range-label");
  await expect(days(page)).toHaveCount(42);
  await expect(label).toContainText("Jun 15");
  await expect(card.getByText("New accounts in range", { exact: true })).toBeVisible();

  const accountsTotal = () => card.getByText("Accounts in total", { exact: true }).evaluate((node) => (node.previousElementSibling?.textContent || "").trim());
  expect(await accountsTotal()).toBe("80");
  await page.getByRole("slider", { name: "To" }).focus();
  await expect(page.getByRole("slider", { name: "To" })).toHaveAttribute("aria-valuetext", /Jun 15/);
  await page.keyboard.press("ArrowLeft");
  await expect(label).toContainText("Jun 14");
  await expect(label).not.toContainText("Jun 15");
  await expect.poll(accountsTotal).toBe("78");
  await expect(card.locator("#activity-chart svg")).toContainText("Running total: 78");
  await expect(detail).toContainText("Jun 14:");
  const newAccounts = () => card.getByText("New accounts in range", { exact: true }).evaluate((node) => (node.previousElementSibling?.textContent || "").trim());
  await expect.poll(newAccounts).toBe("0");
  await expectChartFills(page, 41);
  await expect(card.locator(".btn-primary")).toHaveCount(0);

  const from = page.getByRole("slider", { name: "From" });
  const fromBox = await from.boundingBox();
  expect(fromBox).not.toBeNull();
  await page.mouse.move(fromBox!.x + fromBox!.width / 2, fromBox!.y + fromBox!.height / 2);
  await page.mouse.down();
  await page.mouse.move(fromBox!.x + fromBox!.width / 2 + 120, fromBox!.y + fromBox!.height / 2);
  await page.mouse.up();
  await expect.poll(() => days(page).count()).toBeLessThan(41);
  await expectChartFills(page, await days(page).count());

  await card.getByRole("button", { name: "6 weeks", exact: true }).click();
  await expect(days(page)).toHaveCount(42);
  await expectChartFills(page, 42);
  await expect(card.locator(".btn-primary")).toHaveCount(1);

  await days(page).last().click();
  const panel = page.getByRole("dialog", { name: "Jun 15" });
  await expect(panel).toBeVisible();
  await expect(panel).toContainText("2 new accounts");
  await expect(panel).toContainText("Change requests");
  await expect(panel).toContainText("Flag claims");
  await expect(panel).toContainText("Receipts");
  await expect(panel).toContainText("Page views");
  await expect(panel).toContainText("5");
  await expect(panel).not.toContainText(/visitor/i);
  await expect(panel.getByRole("link", { name: "Review" })).toHaveAttribute("href", "/admin/review");
  await expect(panel.getByRole("link", { name: "Flag Manager" })).toHaveAttribute("href", "/admin/flag-map");
  await panel.getByRole("link", { name: "Flag Manager" }).click();
  await expect(page).toHaveURL(/\/admin\/flag-map$/);
  await expect(page.locator("#flag-position-manager")).toBeVisible();
  await page.goto("/admin/activity");
  await days(page).last().click();
  await page.getByRole("dialog").getByRole("link", { name: "Review" }).click();
  await expect(page).toHaveURL(/\/admin\/review$/);
  await expect(page.getByRole("heading", { name: "Review submitted changes" })).toBeVisible();
});

test("keyboard home wins over the hovered day at 1280", async ({ page }) => {
  await install(page, activity());
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto("/admin/activity");
  const detail = page.locator("#activity-day-detail");
  await expect(days(page)).toHaveCount(42);
  const target = days(page).nth(10);
  await target.hover();
  const hovered = (await target.getAttribute("aria-label")) ?? "";
  const hoveredDay = hovered.split(":")[0];
  await expect(detail).toContainText(`${hoveredDay}:`);
  await target.focus();
  await page.keyboard.press("Home");
  const first = (await days(page).first().getAttribute("aria-label")) ?? "";
  const firstDay = first.split(":")[0];
  await expect(detail).toContainText(`${firstDay}:`);
  expect(firstDay).not.toBe(hoveredDay);
  await expect(detail).not.toContainText(`${hoveredDay}:`);
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
