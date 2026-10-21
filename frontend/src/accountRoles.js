const ADMIN_ROLES = new Set(["PFOH.Admin", "Admin"]);
export const ADMIN_STICKY_PREFIX = "pfoh.adminRole.";
// CacheLookupPolicy.AccessToken. Cache only: no refresh, no iframe, no network.
export const CACHE_ONLY_ACCESS_TOKEN = 1;

export function adminStickyKey(homeAccountId) {
  return `${ADMIN_STICKY_PREFIX}${homeAccountId}`;
}

export function readStickyAdmin(storage, homeAccountId) {
  if (!storage || !homeAccountId) return false;
  try {
    return storage.getItem(adminStickyKey(homeAccountId)) === "1";
  } catch {
    return false;
  }
}

export function writeStickyAdmin(storage, homeAccountId) {
  if (!storage || !homeAccountId) return;
  try {
    storage.setItem(adminStickyKey(homeAccountId), "1");
  } catch {
    // The in-memory flag still covers this page.
  }
}

export function clearStickyAdmin(storage, homeAccountId) {
  if (!storage || !homeAccountId) return;
  try {
    storage.removeItem(adminStickyKey(homeAccountId));
  } catch {
    // Sign-out still clears the in-memory flag.
  }
}

export function noteServerAdmin(storage, homeAccountId, isAdmin) {
  if (isAdmin) writeStickyAdmin(storage, homeAccountId);
  else clearStickyAdmin(storage, homeAccountId);
}

function roleList(roles) {
  if (Array.isArray(roles)) return roles;
  if (roles == null || roles === "") return [];
  return [roles];
}

export function rolesIncludeAdmin(roles) {
  return roleList(roles).some((role) => ADMIN_ROLES.has(String(role).trim()));
}

export function accountIsAdmin(account) {
  return rolesIncludeAdmin(account?.idTokenClaims?.roles);
}

export function decodeJwtPayload(token) {
  const segment = String(token || "").split(".")[1];
  if (!segment) return null;
  try {
    const padded = segment.replace(/-/g, "+").replace(/_/g, "/");
    const base64 = padded + "=".repeat((4 - (padded.length % 4)) % 4);
    const json = typeof atob === "function"
      ? atob(base64)
      : Buffer.from(base64, "base64").toString("utf8");
    return JSON.parse(json);
  } catch {
    return null;
  }
}

export function accessTokenHasAdminRole(accessToken) {
  return rolesIncludeAdmin(decodeJwtPayload(accessToken)?.roles);
}

export function resolvePublicAdmin({ account, sticky, cachedApiAdmin }) {
  const isAdmin = Boolean(sticky) || accountIsAdmin(account) || Boolean(cachedApiAdmin);
  return {
    isAdmin,
    showAdminMenu: isAdmin,
    showAdminActions: isAdmin
  };
}

export async function lookupCachedAdminRole({ account, acquireTokenSilent, cacheLookupPolicy, scopes }) {
  if (!account) {
    return { isAdmin: false, called: false, swallowed: false };
  }

  try {
    const result = await acquireTokenSilent({
      account,
      scopes,
      cacheLookupPolicy
    });
    return {
      isAdmin: accessTokenHasAdminRole(result?.accessToken),
      called: true,
      swallowed: false
    };
  } catch {
    return { isAdmin: false, called: true, swallowed: true };
  }
}
