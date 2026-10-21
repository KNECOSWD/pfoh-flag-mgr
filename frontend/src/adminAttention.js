/**
 * Admin bar prompts. A count of 0 is left out, matching pending review and
 * the reprint queue. Callers pass counts they already loaded.
 */
export function adminAttentionItems({
  pendingReview = 0,
  readyToPrint = 0,
  duplicatesForReview = 0,
  removalQueue = 0,
  multipleClaimAlerts = 0
} = {}) {
  return [
    { key: "pending-review", label: "Pending review", count: pendingReview, route: "/admin/review" },
    { key: "ready-to-print", label: "Ready to print", count: readyToPrint, route: "/admin/printing" },
    { key: "duplicates-for-review", label: "Duplicates", count: duplicatesForReview, route: "/admin/duplicates" },
    { key: "removal-queue", label: "Removal queue", count: removalQueue, route: "/admin/removal-queue" },
    { key: "multiple-claim-alerts", label: "Multiple-claim alerts", count: multipleClaimAlerts, route: "/admin/review" }
  ].filter((item) => item.count > 0);
}

export function showAdminAttention(isAdmin, items) {
  return Boolean(isAdmin) && (items ?? []).length > 0;
}
