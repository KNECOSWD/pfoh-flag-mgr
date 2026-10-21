export const KIOSK_SESSION_KEY = "pfohKiosk";
export const KIOSK_EXIT_HOLD_MS = 3000;

export const KIOSK_GUARDED_ROUTES = [
  "/admin/review",
  "/admin/duplicates",
  "/admin/printing",
  "/admin/flag-map",
  "/find",
  "/my-flags"
];

function kioskQueryRequested(search) {
  const params = new URLSearchParams(String(search || "").replace(/^\?/, ""));
  const value = (params.get("kiosk") || "").trim().toLowerCase();
  return value === "1" || value === "true" || value === "yes";
}

export function rememberKioskSession(storage, search) {
  if (kioskQueryRequested(search)) storage.setItem(KIOSK_SESSION_KEY, "1");
  return storage.getItem(KIOSK_SESSION_KEY) === "1";
}

export function kioskSessionOn(storage) {
  return storage.getItem(KIOSK_SESSION_KEY) === "1";
}

export function clearKioskSession(storage) {
  storage.removeItem(KIOSK_SESSION_KEY);
}

export function routeWhileKiosk(pathname, kioskOn) {
  const path = String(pathname || "").replace(/\/$/, "") || "/find";
  if (!kioskOn) return path;
  return path === "/flag-map" ? "/flag-map" : "/flag-map";
}

export function kioskBackTarget(pathname, kioskOn) {
  if (!kioskOn) return null;
  return "/flag-map";
}

export function kioskStayHref(href) {
  const url = new URL(href, "http://localhost");
  const path = url.pathname.replace(/\/$/, "") || "/find";
  if (path === "/flag-map") return `${url.pathname}${url.search}`;
  return "/flag-map";
}

export function kioskChrome(kiosk, isAdmin) {
  if (kiosk) {
    return {
      shell: "map",
      header: false,
      nav: false,
      auth: false,
      drawer: false,
      footer: false,
      adminBanner: false,
      adminLinks: false,
      loadSignedInData: false
    };
  }

  return {
    shell: "site",
    header: true,
    nav: true,
    auth: true,
    drawer: true,
    footer: true,
    adminBanner: Boolean(isAdmin),
    adminLinks: Boolean(isAdmin),
    loadSignedInData: true
  };
}

export function kioskBlocksNavigationKey(key, altKey, typing) {
  if (altKey && key === "ArrowLeft") return true;
  if (!typing && key === "Backspace") return true;
  return false;
}

export function kioskLinkLeavesMap(href, pageUrl) {
  if (!href || href === "#") return false;
  let url;
  try {
    url = new URL(href, pageUrl);
  } catch {
    return true;
  }

  if (url.protocol === "blob:" || url.protocol === "data:") return false;
  const page = new URL(pageUrl, "http://localhost");
  if (url.origin !== page.origin) return true;
  const path = url.pathname.replace(/\/$/, "") || "/";
  return path !== "/flag-map";
}

export async function requestKioskExit(pin, fetchImpl) {
  const send = fetchImpl ?? ((url, init) => fetch(url, init));
  const response = await send("/api/public/flag-map/kiosk-exit", {
    method: "POST",
    headers: {
      Accept: "application/json",
      "Content-Type": "application/json"
    },
    body: JSON.stringify({ pin }),
    cache: "no-store"
  });
  return response.status;
}
