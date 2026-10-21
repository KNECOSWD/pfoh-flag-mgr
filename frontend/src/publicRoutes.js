export function isPublicPage(route) {
  const path = String(route || "").replace(/\/$/, "") || "/find";
  return path === "/find" || path === "/flag-map";
}
