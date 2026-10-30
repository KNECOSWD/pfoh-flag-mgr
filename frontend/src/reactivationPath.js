/**
 * The reactivation path keeps its token in the address so sign-in can return here.
 * @param {string | null | undefined} pathname
 */
export function reactivationTokenFromPath(pathname) {
  const path = String(pathname || "");
  const match = path.match(/^\/reactivate\/([^/]+)\/?$/);
  if (!match) return null;
  let token = match[1];
  try {
    token = decodeURIComponent(token);
  } catch {
    return null;
  }
  if (!/^[A-Za-z0-9_-]{43}$/.test(token)) return null;
  return token;
}

/**
 * Sign-in and account creation must come back to the page that started them.
 * @param {string} href
 */
export function signInReturnHref(href) {
  return href;
}
