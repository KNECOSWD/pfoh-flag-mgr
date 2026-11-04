const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * Public submission pages use a guid token. A sequential id does not match.
 * @param {string | null | undefined} pathname
 */
export function submissionTokenFromPath(pathname) {
  const match = String(pathname || "").match(/^\/submission\/([^/]+)\/?$/);
  if (!match) return null;
  let token = match[1];
  try {
    token = decodeURIComponent(token);
  } catch {
    return null;
  }

  if (!guidPattern.test(token)) return null;
  return token.toLowerCase();
}
