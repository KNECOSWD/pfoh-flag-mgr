import { mkdirSync } from "node:fs";
import { expect, test, type Page, type Route } from "@playwright/test";
import { HONOREE_BIO_MAX_LENGTH, honoreeBioLimitMessage, honoreeBioShortenMessage } from "../src/honoreeBio";

const outDir = "../docs/pr-5.8.1";
const limit = String(HONOREE_BIO_MAX_LENGTH);

const branches = [
  { id: 1, serviceBranchCategoryId: 1, serviceBranchName: "Army", description: "Army" }
];
const categories = [
  { id: 1, serviceBranchCategoryName: "Armed forces", description: "Armed forces" }
];

const shortBio = "She kept every letter from the field.";
const longBio = "W".repeat(HONOREE_BIO_MAX_LENGTH + 80);

function change(partial: Record<string, unknown>) {
  return {
    id: 9,
    flagClaimId: 7,
    flagGridId: 3,
    honoreeId: 12,
    firstName: "Ada",
    lastName: "Lovelace",
    rank: "Captain",
    serviceBranchId: 1,
    serviceBranchCategoryId: 1,
    description: shortBio,
    kia: false,
    requestStatus: "Draft",
    createdUtc: "2026-01-02T00:00:00Z",
    submitterPhoneNumber: "2145550100",
    submitterEmailAddress: "admin@example.test",
    ...partial
  };
}

const updateClaim = {
  id: 7,
  flagGridId: 3,
  flagGridName: "AA-14",
  honoreeId: 12,
  honoreeName: "Ada Lovelace",
  claimStatus: "Claimed",
  externalUserEmail: "admin@example.test",
  externalUserName: "Example Admin",
  createdUtc: "2026-01-02T00:00:00Z",
  submittedUtc: "2026-01-03T00:00:00Z",
  latestChangeRequest: change({})
};

function adminClaim(description: string, name: { first: string; last: string; id: number }) {
  return {
    ...updateClaim,
    id: 80 + name.id,
    honoreeId: name.id,
    honoreeName: `${name.first} ${name.last}`,
    claimStatus: "AdminDirectEdit",
    latestChangeRequest: change({
      id: 90 + name.id,
      flagClaimId: 80 + name.id,
      honoreeId: name.id,
      firstName: name.first,
      lastName: name.last,
      description
    })
  };
}

const shortHonoree = {
  id: 12,
  fullName: "Ada Lovelace",
  firstName: "Ada",
  lastName: "Lovelace",
  rank: "Captain",
  kia: false,
  serviceBranchName: "Army",
  flagGrid: "AA-14",
  sponsorName: "Family",
  isActive: true
};

const longHonoree = {
  ...shortHonoree,
  id: 18,
  fullName: "Grace Hopper",
  firstName: "Grace",
  lastName: "Hopper",
  rank: "Rear Admiral",
  serviceBranchName: "Navy",
  flagGrid: "AA-22"
};

function adminAccount() {
  return JSON.stringify({
    homeAccountId: "admin-1.e2e",
    localAccountId: "admin-1",
    environment: "e2e",
    tenantId: "e2e",
    username: "admin@example.test",
    name: "Example Admin",
    idTokenClaims: { roles: ["PFOH.Admin"] }
  });
}

async function install(page: Page, nominateStatus?: number) {
  await page.addInitScript((raw) => {
    sessionStorage.setItem("pfoh.e2eAccount", raw);
  }, adminAccount());
  await page.route("**/api/**", async (route: Route) => {
    const path = new URL(route.request().url()).pathname;
    const json = (body: unknown, status = 200, headers: Record<string, string> = {}) =>
      route.fulfill({ status, json: body, headers });
    if (path === "/api/version") return json({ version: "5.8.1" });
    if (path === "/api/telemetry-config") return json({ enabled: false });
    if (path === "/api/profile") {
      return json({
        ownerObjectId: "admin-1",
        displayName: "Example Admin",
        email: "admin@example.test",
        hasSavedDisplayName: true,
        phone: "2145550100",
        organization: ""
      });
    }
    if (path === "/api/lookups/service-branches") return json(branches);
    if (path === "/api/lookups/service-branch-categories") return json(categories);
    if (path === "/api/flag-claims/my") return json([updateClaim]);
    if (path === "/api/admin/review/pending") return json([], 200, { "X-Open-Duplicate-Groups": "0" });
    if (path === "/api/admin/review/superseded") return json({ items: [], page: 1, pageSize: 1, total: 0 });
    if (path === "/api/admin/review/approved-reprint-queue" || path === "/api/admin/review/flag-positions") return json([]);
    if (path === "/api/admin/removal-queue/count") return json({ count: 0 });
    if (path === "/api/admin/print/history") return json({ printed: [], removed: [] });
    if (path.startsWith("/api/honorees/search")) {
      const query = new URL(route.request().url()).searchParams.get("q") ?? "";
      return json(query.toLowerCase().includes("grace") ? [longHonoree] : [shortHonoree]);
    }
    if (path === "/api/flag-claims/admin/honoree/12/edit") return json(adminClaim(shortBio, { first: "Ada", last: "Lovelace", id: 12 }));
    if (path === "/api/flag-claims/admin/honoree/18/edit") return json(adminClaim(longBio, { first: "Grace", last: "Hopper", id: 18 }));
    if (path.endsWith("/claimants")) return json([]);
    if (path === "/api/flag-claims/duplicate-check") return json({ enabled: true, matches: [] });
    if (path === "/api/flag-claims/nominate" && nominateStatus) {
      return json({
        title: "One or more validation errors occurred.",
        status: nominateStatus,
        errors: { Description: [honoreeBioLimitMessage()] }
      }, nominateStatus);
    }
    return json({});
  });
}

async function shot(page: Page, name: string) {
  const card = page.locator(".formCard");
  await expect(card).toBeVisible();
  await card.screenshot({ path: `${outDir}/after-${name}.png` });
}

test.beforeAll(() => {
  mkdirSync(outDir, { recursive: true });
});

test.describe("honoree bio length", () => {
  for (const viewport of [
    { width: 1280, height: 1400, name: "desktop" },
    { width: 390, height: 1400, name: "390" }
  ]) {
    test(`nomination counter and max length at ${viewport.name}`, async ({ page }) => {
      await page.setViewportSize(viewport);
      await install(page);
      await page.goto("/honor-a-hero");
      const bio = page.locator("#honoree-bio");
      await expect(bio).toBeVisible();
      await expect(bio).toHaveAttribute("maxlength", limit);
      await expect(bio).toHaveAttribute("aria-describedby", "honoree-bio-count");
      await expect(page.locator("#honoree-bio-count")).toHaveAttribute("aria-live", "polite");
      await expect(page.locator("#honoree-bio-count")).toHaveText(`0 / ${limit} characters`);
      await bio.fill(shortBio);
      await expect(page.locator("#honoree-bio-count")).toHaveText(`${shortBio.length} / ${limit} characters`);
      await bio.fill("W".repeat(HONOREE_BIO_MAX_LENGTH + 40));
      await expect(bio).toHaveValue("W".repeat(HONOREE_BIO_MAX_LENGTH));
      await expect(page.locator("#honoree-bio-count")).toHaveText(`${limit} / ${limit} characters`);
      await expect(page.locator("#honoree-bio-limit")).toHaveCount(0);
      await bio.fill(shortBio);
      await shot(page, `nomination-${viewport.name}`);
    });

    test(`update request counter and max length at ${viewport.name}`, async ({ page }) => {
      await page.setViewportSize(viewport);
      await install(page);
      await page.goto("/my-flags");
      await page.getByRole("button", { name: "Open", exact: true }).click();
      const bio = page.locator("#honoree-bio");
      await expect(page.getByRole("heading", { name: /Suggest a change/ })).toBeVisible();
      await expect(bio).toHaveValue(shortBio);
      await expect(bio).toHaveAttribute("maxlength", limit);
      await expect(bio).toHaveAttribute("aria-describedby", "honoree-bio-count");
      await expect(page.locator("#honoree-bio-count")).toHaveText(`${shortBio.length} / ${limit} characters`);
      await expect(page.locator("#honoree-bio-count")).toHaveAttribute("aria-live", "polite");
      await shot(page, `update-${viewport.name}`);
    });

    test(`admin edit counter, max length, and over-limit bio at ${viewport.name}`, async ({ page }) => {
      await page.setViewportSize(viewport);
      await install(page);
      await page.goto("/find");
      await page.locator("#honoree-search").fill("Ada");
      await page.getByRole("button", { name: "Search" }).click();
      await page.getByRole("heading", { name: "Ada Lovelace" }).waitFor();
      await page.locator("summary", { hasText: "Admin actions" }).click();
      await page.getByRole("button", { name: "Edit + reprint" }).click();
      await page.getByRole("button", { name: "Start edit" }).click();
      const bio = page.locator("#honoree-bio");
      await expect(page.getByRole("heading", { name: /Directly edit Ada Lovelace/ })).toBeVisible();
      await expect(bio).toHaveValue(shortBio);
      await expect(bio).toHaveAttribute("maxlength", limit);
      await expect(page.locator("#honoree-bio-count")).toHaveText(`${shortBio.length} / ${limit} characters`);
      await expect(page.locator("#honoree-bio-count")).toHaveAttribute("aria-live", "polite");
      await shot(page, `admin-edit-${viewport.name}`);

      await page.goto("/find");
      await page.locator("#honoree-search").fill("Grace");
      await page.getByRole("button", { name: "Search" }).click();
      await page.getByRole("heading", { name: "Grace Hopper" }).waitFor();
      await page.locator("summary", { hasText: "Admin actions" }).click();
      await page.getByRole("button", { name: "Edit + reprint" }).click();
      await page.getByRole("button", { name: "Start edit" }).click();
      await expect(page.getByRole("heading", { name: /Directly edit Grace Hopper/ })).toBeVisible();
      await expect(bio).toHaveValue(longBio);
      await expect(bio).not.toHaveAttribute("maxlength", limit);
      await bio.press("End");
      await bio.type("X");
      await expect(bio).toHaveValue(longBio);
      await expect(page.locator("#honoree-bio-count")).toHaveText(`${longBio.length} / ${limit} characters`);
      await expect(page.locator("#honoree-bio-count")).toHaveClass(/text-danger/);
      await expect(page.locator("#honoree-bio-limit")).toHaveText(honoreeBioShortenMessage());
      await expect(bio).toHaveAttribute("aria-describedby", /honoree-bio-limit/);
      await shot(page, `admin-over-limit-${viewport.name}`);
      await page.getByRole("button", { name: "Save and queue reprint" }).click();
      await expect(page.locator(".message.error")).toContainText(honoreeBioShortenMessage());
      await expect(page.locator(".message.error")).not.toContainText("share this message");
      await bio.fill("W".repeat(HONOREE_BIO_MAX_LENGTH));
      await expect(bio).toHaveValue("W".repeat(HONOREE_BIO_MAX_LENGTH));
      await expect(page.locator("#honoree-bio-count")).toHaveText(`${limit} / ${limit} characters`);
      await expect(page.locator("#honoree-bio-limit")).toHaveCount(0);
      await expect(bio).toHaveAttribute("maxlength", limit);
    });
  }

  test("a server bio length error is shown as the validation sentence", async ({ page }) => {
    await install(page, 400);
    await page.goto("/honor-a-hero");
    await page.locator("#honoree-bio").fill("A short tribute.");
    await page.getByLabel("First name").fill("Nora");
    await page.getByLabel("Last name").fill("Limit");
    await page.locator(".formCard select").nth(0).selectOption({ label: "Armed forces" });
    await page.locator(".formCard select").nth(1).selectOption({ label: "Army" });
    await page.getByLabel("Submitter phone").fill("2145550100");
    await page.getByLabel("Submitter email").fill("admin@example.test");
    await page.getByRole("button", { name: "Submit for review" }).click();
    const alert = page.locator(".message.error");
    await expect(alert).toContainText(honoreeBioLimitMessage());
    await expect(alert).not.toContainText("share this message");
    await expect(alert).not.toContainText("Request failed");
  });
});
