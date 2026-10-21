import { isPublicPage } from "./publicRoutes.js";

const ADMIN_ROLES = new Set(["PFOH.Admin", "Admin"]);

export function accountIsAdmin(account) {
  const roles = account?.idTokenClaims?.roles;
  const values = Array.isArray(roles) ? roles : roles == null || roles === "" ? [] : [roles];
  return values.some((role) => ADMIN_ROLES.has(String(role).trim()));
}

export function publicPageAdminState(route, account) {
  if (!isPublicPage(route)) return null;
  const isAdmin = accountIsAdmin(account);
  return {
    isAdmin,
    showAdminMenu: isAdmin,
    showAdminActions: isAdmin,
    tokenCalls: 0
  };
}
