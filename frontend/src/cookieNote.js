export const COOKIE_NOTE_TEXT = "We use cookies to count visits and see which pages are used, so we can improve this site. They are not used for ads, and we don't sell your information. The counts are stored with Microsoft Azure.";
export const COOKIE_NOTE_ACCEPT = "OK";
export const COOKIE_NOTE_DECLINE = "No thanks";
export const COOKIE_NOTE_STORAGE_KEY = "pfoh.cookieChoice";

export function cookieNoteChoice(storage) {
  try {
    const value = storage ? storage.getItem(COOKIE_NOTE_STORAGE_KEY) : "";
    return value === "accepted" || value === "declined" ? value : "";
  } catch {
    return "";
  }
}

export function acceptCookieNote(storage) {
  try {
    if (!storage) return false;
    storage.setItem(COOKIE_NOTE_STORAGE_KEY, "accepted");
    return true;
  } catch {
    return false;
  }
}

export function declineCookieNote(storage) {
  try {
    if (!storage) return false;
    storage.setItem(COOKIE_NOTE_STORAGE_KEY, "declined");
    return true;
  } catch {
    return false;
  }
}

export function isAdminPath(pathname) {
  const path = String(pathname || "/").split("?")[0].replace(/\/$/, "") || "/";
  return path === "/admin" || path.toLowerCase().startsWith("/admin/");
}

export function cookieNoteVisible(choice, pathname, telemetryEnabled) {
  if (!telemetryEnabled) return false;
  if (choice === "accepted" || choice === "declined") return false;
  return !isAdminPath(pathname);
}

export function browserStorage() {
  try {
    if (typeof localStorage === "undefined") return null;
    return localStorage;
  } catch {
    return null;
  }
}
