export const COOKIE_NOTE_TEXT = "We use cookies to count visits and see which pages are used, so we can improve this site. They are not used for ads, and we don't sell your information.";
export const COOKIE_NOTE_BUTTON = "OK";
export const COOKIE_NOTE_STORAGE_KEY = "pfoh.cookieNoteDismissed";

export function cookieNoteIsDismissed(storage) {
  try {
    return Boolean(storage && storage.getItem(COOKIE_NOTE_STORAGE_KEY) === "1");
  } catch {
    return false;
  }
}

export function dismissCookieNote(storage) {
  try {
    if (!storage) return false;
    storage.setItem(COOKIE_NOTE_STORAGE_KEY, "1");
    return true;
  } catch {
    return false;
  }
}

export function browserStorage() {
  try {
    if (typeof localStorage === "undefined") return null;
    return localStorage;
  } catch {
    return null;
  }
}
