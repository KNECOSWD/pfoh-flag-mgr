import { mkdirSync, writeFileSync } from "node:fs";
import { expect, test, type Page, type Route } from "@playwright/test";

const GUID = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";
const CODE = "PFOH-7K3Q9X";
const TOKEN = "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";
const CONNECTION = "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=http://127.0.0.1:4173/ai-ingest/";

function operationName(body: string) {
  const parsed = JSON.parse(body) as Array<{ tags?: Record<string, string> }>;
  return parsed.map((item) => item.tags?.["ai.operation.name"] ?? "");
}

function assertScrubbed(body: string) {
  expect(body.includes(GUID), body).toBe(false);
  expect(body.includes(CODE), body).toBe(false);
  expect(body.includes(TOKEN), body).toBe(false);
  expect(body).not.toMatch(/\/submission\/(?!\{token\})/i);
  expect(body).not.toMatch(/\/reactivate\/(?!\{token\})/i);
  expect(body).not.toMatch(/\/receipt\/(?!\{token\})/i);
  expect(body).not.toMatch(/\/pending\/(?!\{token\})/i);
  expect(body).not.toMatch(/PFOH-[A-Za-z0-9]/);
  const withoutKey = body
    .replace(/"iKey"\s*:\s*"[^"]*"/g, '"iKey":""')
    .replace(/Microsoft\.ApplicationInsights\.[0-9a-f-]{36}/gi, "Microsoft.ApplicationInsights.");
  expect(withoutKey).not.toMatch(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i);
}

async function fulfillApi(route: Route) {
  const path = new URL(route.request().url()).pathname;
  if (path === "/api/version") {
    await route.fulfill({ json: { version: "5.4.0" } });
    return;
  }
  if (path === "/api/telemetry-config") {
    await route.fulfill({ json: { enabled: true, connectionString: CONNECTION } });
    return;
  }
  if (path.startsWith("/api/lookups/") || path === "/api/flag-claims/my") {
    await route.fulfill({ json: [] });
    return;
  }
  await route.fulfill({ json: {} });
}

test("the real SDK payload has no raw token after load, navigation, or an exception", async ({ page }) => {
  mkdirSync("e2e-shots", { recursive: true });
  const payloads: string[] = [];
  const thirdParty: string[] = [];
  page.on("pageerror", () => {});
  page.on("request", (request) => {
    const url = request.url();
    if (/monitor\.azure|visualstudio|applicationinsights\.azure|jsdelivr|unpkg|cdnjs/i.test(url)) {
      thirdParty.push(url);
    }
  });
  await page.route("**/api/**", fulfillApi);
  await page.route("**/ai-ingest/**", async (route) => {
    payloads.push(route.request().postData() || "");
    await route.fulfill({ status: 200, contentType: "application/json", body: "{}" });
  });

  await page.goto(`/submission/${GUID}?code=${CODE}#top`);
  await expect(page.locator("#cookie-note")).toBeVisible();
  expect(payloads).toEqual([]);
  await page.getByRole("button", { name: "OK" }).click();
  await expect.poll(() => payloads.length, { timeout: 15000 }).toBeGreaterThan(0);
  for (const body of payloads) assertScrubbed(body);
  expect(operationName(payloads.join("\n")).some((name) => name === "/submission/{token}")).toBe(true);

  await page.evaluate((message) => {
    setTimeout(() => {
      throw new Error(message);
    }, 0);
  }, `failed /submission/${GUID} /reactivate/${TOKEN} /receipt/${CODE}`);
  await expect.poll(() => payloads.join("\n").includes("Exception"), { timeout: 15000 }).toBe(true);
  for (const body of payloads) assertScrubbed(body);

  const beforeNavigation = payloads.length;
  await page.locator('nav[aria-label="Main navigation"] a[href="/find"]').click();
  await expect(page).toHaveURL(/\/find$/);
  await expect.poll(() => payloads.length, { timeout: 15000 }).toBeGreaterThan(beforeNavigation);
  for (const body of payloads) assertScrubbed(body);
  const names = payloads.slice(beforeNavigation).flatMap((body) => operationName(body));
  expect(names).toContain("/find");
  expect(names.some((name) => name.includes(GUID) || name.includes("/submission/"))).toBe(false);
  expect(thirdParty).toEqual([]);
  const scripts = await page.locator("script[src]").evaluateAll((nodes) => nodes.map((node) => (node as HTMLScriptElement).src));
  expect(scripts.join(" ")).not.toMatch(/monitor\.azure|applicationinsights/i);

  const sample = payloads.find((body) => body.includes("/submission/{token}")) || payloads[0];
  writeFileSync("e2e-shots/scrubbed-telemetry-sample.json", JSON.stringify(JSON.parse(sample), null, 2));
});

function watchTelemetry(page: Page) {
  const hits: string[] = [];
  page.on("request", (request) => {
    const url = request.url();
    if (/\/api\/telemetry-config|ai-ingest|\/v2\/track|monitor\.azure|visualstudio|applicationinsights/i.test(url)) {
      hits.push(url);
    }
  });
  return hits;
}

async function aiCookies(page: Page) {
  const cookies = await page.context().cookies();
  return cookies.filter((cookie) => cookie.name === "ai_user" || cookie.name === "ai_session");
}

test("before choosing there are no telemetry requests and no ai cookies", async ({ page }) => {
  const hits = watchTelemetry(page);
  await page.route("**/api/**", fulfillApi);
  await page.goto("/find");
  await expect(page.locator("#cookie-note")).toBeVisible();
  await page.waitForTimeout(400);
  expect(hits.filter((url) => !url.includes("/api/telemetry-config"))).toEqual([]);
  expect(await aiCookies(page)).toEqual([]);
});

test("No thanks stays off after reload", async ({ page }) => {
  const hits = watchTelemetry(page);
  await page.route("**/api/**", fulfillApi);
  await page.goto("/find");
  await page.getByRole("button", { name: "No thanks" }).click();
  await expect(page.locator("#cookie-note")).toHaveCount(0);
  await page.reload();
  await expect(page.getByRole("searchbox", { name: "Search honorees" })).toBeVisible();
  await expect(page.locator("#cookie-note")).toHaveCount(0);
  expect(hits.filter((url) => !url.includes("/api/telemetry-config"))).toEqual([]);
  expect(await aiCookies(page)).toEqual([]);
  expect(await page.evaluate(() => localStorage.getItem("pfoh.cookieChoice"))).toBe("declined");
});

test("the cookie note stays off admin routes", async ({ page }) => {
  await page.route("**/api/**", fulfillApi);
  await page.goto("/admin/review");
  await expect(page.getByRole("heading", { name: "Administrator access required" })).toBeVisible();
  await expect(page.locator("#cookie-note")).toHaveCount(0);
});
