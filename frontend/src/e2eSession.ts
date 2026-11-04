import type { AccountInfo } from "@azure/msal-browser";

// Playwright sets VITE_E2E. Production builds leave it unset, so this returns null.
export function e2eAccount(): AccountInfo | null {
  if (import.meta.env.VITE_E2E !== "1" || typeof sessionStorage === "undefined") {
    return null;
  }

  const raw = sessionStorage.getItem("pfoh.e2eAccount");
  if (!raw) return null;
  try {
    return JSON.parse(raw) as AccountInfo;
  } catch {
    return null;
  }
}

export function e2eToken(): string | null {
  return import.meta.env.VITE_E2E === "1" && e2eAccount() ? "e2e" : null;
}

// Local screenshot and Playwright runs send the test auth headers.
// Production builds leave VITE_E2E unset, so this returns nothing.
export function e2eAuthHeaders(): Record<string, string> {
  const account = e2eAccount();
  if (!account) return {};
  const claims = account.idTokenClaims as { roles?: string[] } | undefined;
  const roles = Array.isArray(claims?.roles) ? claims.roles.join(",") : "";
  return {
    "X-Test-Oid": account.localAccountId || "e2e-user",
    "X-Test-Email": account.username || "",
    "X-Test-Name": account.name || "Supporter",
    ...(roles ? { "X-Test-Roles": roles } : {})
  };
}
