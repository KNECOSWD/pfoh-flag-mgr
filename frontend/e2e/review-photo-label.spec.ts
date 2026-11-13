import { mkdirSync } from "node:fs";
import { expect, test, type Page, type Route } from "@playwright/test";

const png = Buffer.from(
  "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==",
  "base64"
);

function reviewItem(partial: Record<string, unknown>) {
  return {
    changeRequestId: 555,
    claimId: 1,
    flagGridId: 26,
    flagGridName: "M-26",
    honoreeId: 1527,
    honoreeName: "Pat Example",
    rank: "Sergeant",
    serviceBranchName: "Army",
    claimantEmail: "family@example.test",
    claimantName: "Family Member",
    requestStatus: "Submitted",
    createdUtc: "2026-03-01T12:00:00Z",
    submittedUtc: "2026-03-01T12:00:00Z",
    requiresCardReprint: false,
    photoFileName: null,
    currentPhotoAvailable: false,
    currentPhotoUrl: null,
    description: "A fixture tribute for the follow-up request.",
    awards: "",
    conflictsServed: "",
    submissionKind: "Change",
    fieldChanges: [],
    ...partial
  };
}

async function install(page: Page) {
  await page.addInitScript(() => {
    sessionStorage.setItem("pfoh.e2eAccount", JSON.stringify({
      homeAccountId: "admin-1.e2e",
      localAccountId: "admin-1",
      environment: "e2e",
      tenantId: "e2e",
      username: "admin@example.test",
      name: "Example Admin",
      idTokenClaims: { roles: ["PFOH.Admin"] }
    }));
  });
  await page.route("**/api/**", async (route: Route) => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith("/photo")) {
      await route.fulfill({ body: png, contentType: "image/png" });
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
    if (path === "/api/profile") {
      await route.fulfill({
        json: {
          ownerObjectId: "admin-1",
          displayName: "Example Admin",
          email: "admin@example.test",
          hasSavedDisplayName: true,
          phone: "2145550100",
          organization: ""
        }
      });
      return;
    }
    if (path === "/api/admin/review/pending") {
      await route.fulfill({
        json: [
          reviewItem({ changeRequestId: 555, photoFileName: "pending/555.jpeg" }),
          reviewItem({
            changeRequestId: 556,
            claimId: 2,
            photoFileName: null,
            currentPhotoAvailable: true,
            currentPhotoUrl: "/api/admin/review/honorees/1527/photo"
          }),
          reviewItem({
            changeRequestId: 557,
            claimId: 3,
            flagGridId: 28,
            flagGridName: "M-28",
            honoreeId: 1529,
            honoreeName: "No Photo Example",
            photoFileName: null,
            currentPhotoAvailable: false,
            currentPhotoUrl: null
          })
        ],
        headers: { "X-Open-Duplicate-Groups": "0" }
      });
      return;
    }
    if (path === "/api/admin/review/superseded") {
      await route.fulfill({ json: { items: [], page: 1, pageSize: 1, total: 0 } });
      return;
    }
    if (path === "/api/admin/review/555/card-preview" || path === "/api/admin/review/556/card-preview" || path === "/api/admin/review/557/card-preview") {
      await route.fulfill({ body: "%PDF-1.4 fixture", contentType: "application/pdf" });
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
    if (path === "/api/admin/print/history") {
      await route.fulfill({ json: { printed: [], removed: [] } });
      return;
    }
    await route.fulfill({ json: {} });
  });
}

test("review labels a new photo and a current photo", async ({ page }) => {
  mkdirSync("../docs/pr-5.8.1", { recursive: true });
  await install(page);
  for (const viewport of [
    { width: 1280, height: 900, name: "desktop" },
    { width: 390, height: 900, name: "390" }
  ]) {
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    await page.goto("/admin/review");
    await expect(page.getByText("New photo")).toBeVisible();
    await expect(page.getByText("Current photo")).toBeVisible();
    await expect(page.getByRole("img", { name: "Pat Example new photo" })).toBeVisible();
    await expect(page.getByRole("img", { name: "Pat Example current photo" })).toBeVisible();
    const neither = page.locator(".reviewPendingCard", { hasText: "No Photo Example" });
    await expect(neither.getByText("New photo")).toHaveCount(0);
    await expect(neither.getByText("Current photo")).toHaveCount(0);
    await expect(neither.locator("img")).toHaveCount(0);
    const list = page.locator(".reviewPendingList");
    await list.screenshot({ path: `../docs/pr-5.8.1/review-photo-labels-${viewport.name}.png` });
  }

  await page.getByRole("button", { name: "Honoree Card" }).nth(1).click();
  await expect(page.locator(".cardPreviewModal").getByText("Current photo")).toBeVisible();
  await page.getByRole("button", { name: "Close" }).click();
  await page.getByRole("button", { name: "Honoree Card" }).first().click();
  await expect(page.locator(".cardPreviewModal").getByText("New photo")).toBeVisible();
  await page.getByRole("button", { name: "Close" }).click();
  await page.getByRole("button", { name: "Honoree Card" }).nth(2).click();
  await expect(page.locator(".cardPreviewModal").getByText("New photo")).toHaveCount(0);
  await expect(page.locator(".cardPreviewModal").getByText("Current photo")).toHaveCount(0);
});
