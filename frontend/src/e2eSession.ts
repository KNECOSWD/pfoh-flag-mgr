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
