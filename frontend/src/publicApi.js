const publicPaths = new Set([
  "/api/honorees/search",
  "/api/lookups/service-branches",
  "/api/lookups/service-branch-categories"
]);

export function isPublicApiPath(url) {
  const path = String(url || "").split("?")[0];
  return publicPaths.has(path);
}
