const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const PUBLIC_CODE = /^PFOH-[A-Za-z0-9]{4,12}$/i;
const OPAQUE = /^[A-Za-z0-9_-]{20,}$/;
const TOKEN_ROUTES = new Set(["reactivate", "submission", "receipt", "pending"]);
const GUID_IN_TEXT = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/gi;
const CODE_IN_TEXT = /PFOH-[A-Za-z0-9]{4,12}/gi;
const ROUTE_TOKEN_IN_TEXT = /(\/(?:reactivate|submission|receipt|pending)\/)[^/?#\s"'<>]+/gi;
const OPAQUE_IN_TEXT = /\/[A-Za-z0-9_-]{20,}/g;
const URL_IN_TEXT = /https?:\/\/[^\s"'<>]+/gi;

export const LEGACY_COOKIE_CHOICE_KEY = "pfoh.cookieChoice";

export function isAdminPath(pathname) {
  const path = String(pathname || "/").split("?")[0].replace(/\/$/, "") || "/";
  return path === "/admin" || path.toLowerCase().startsWith("/admin/");
}

export function clearLegacyCookieChoice(storage) {
  try {
    if (storage && typeof storage.removeItem === "function") storage.removeItem(LEGACY_COOKIE_CHOICE_KEY);
  } catch {
    /* storage can be blocked */
  }
}

let client = null;
let ensurePromise = null;
let queued = null;
let lastTracked = "";
let currentOperationName = "/";
let instrumentationKey = "";
let telemetryEnabled = false;
const listeners = new Set();

export function browserTelemetrySettings(connectionString) {
  return {
    connectionString,
    samplingPercentage: 100,
    disableAjaxTracking: true,
    disableFetchTracking: true,
    enableAjaxPerfTracking: false,
    enableRequestHeaderTracking: false,
    enableResponseHeaderTracking: false,
    enableAjaxErrorStatusText: false,
    enableAutoRouteTracking: false,
    autoTrackPageVisitTime: false,
    disableCorrelationHeaders: true,
    enableCorsCorrelation: false,
    disableCookiesUsage: true,
    isStorageUseDisabled: true,
    enableSessionStorageBuffer: false,
    loggingLevelConsole: 0,
    maxBatchInterval: 250,
    extensionConfig: {
      AppInsightsCfgSyncPlugin: {
        cfgUrl: "",
        blkCdnCfg: true
      }
    }
  };
}

export function redactTelemetryTarget(value) {
  const raw = String(value ?? "");
  if (!raw) return raw;

  let origin = "";
  let path = raw;
  if (/^https?:\/\//i.test(raw)) {
    try {
      const url = new URL(raw);
      origin = url.origin;
      path = url.pathname;
    } catch {
      path = raw.split(/[?#]/)[0];
    }
  } else {
    path = raw.split(/[?#]/)[0];
  }

  const parts = path.split("/");
  for (let index = 0; index < parts.length; index += 1) {
    const segment = parts[index];
    if (!segment) continue;
    let decoded = segment;
    try {
      decoded = decodeURIComponent(segment);
    } catch {
      decoded = segment;
    }
    const previous = index > 0 ? String(parts[index - 1] || "").toLowerCase() : "";
    if (isTokenSegment(decoded, previous)) parts[index] = "{token}";
  }

  const redacted = parts.join("/");
  return origin ? `${origin}${redacted}` : redacted;
}

function isTokenSegment(segment, previous) {
  if (TOKEN_ROUTES.has(previous)) return true;
  if (GUID.test(segment)) return true;
  if (PUBLIC_CODE.test(segment)) return true;
  if (OPAQUE.test(segment)) return true;
  return false;
}

export function shouldTrackPagePath(value) {
  const redacted = redactTelemetryTarget(value);
  let pathname = redacted;
  if (/^https?:\/\//i.test(redacted)) {
    try {
      pathname = new URL(redacted).pathname;
    } catch {
      pathname = redacted;
    }
  }
  const path = String(pathname || "/").replace(/\/$/, "") || "/";
  const lowered = path.toLowerCase();
  return lowered !== "/admin" && !lowered.startsWith("/admin/");
}

export function scrubTelemetryString(value) {
  let text = String(value ?? "");
  if (!text) return text;
  if (text.startsWith("/") || /^https?:\/\//i.test(text)) {
    text = redactTelemetryTarget(text);
  }
  text = text.replace(URL_IN_TEXT, (url) => redactTelemetryTarget(url));
  text = text.replace(ROUTE_TOKEN_IN_TEXT, "$1{token}");
  text = text.replace(GUID_IN_TEXT, (guid) => (
    instrumentationKey && guid.toLowerCase() === instrumentationKey.toLowerCase() ? guid : "{token}"
  ));
  text = text.replace(CODE_IN_TEXT, "{token}");
  text = text.replace(OPAQUE_IN_TEXT, "/{token}");
  return text;
}

function scrubNode(value, seen) {
  if (typeof value === "string") return scrubTelemetryString(value);
  if (!value || typeof value !== "object") return value;
  if (typeof value === "function") return value;
  if (seen.has(value)) return value;
  seen.add(value);
  if (Array.isArray(value)) {
    for (let index = 0; index < value.length; index += 1) {
      value[index] = scrubNode(value[index], seen);
    }
    return value;
  }
  for (const key of Object.keys(value)) {
    value[key] = scrubNode(value[key], seen);
  }
  return value;
}

function pathBased(value) {
  return typeof value === "string" && (value.startsWith("/") || /^https?:\/\//i.test(value) || value.includes("?") || value.includes("#"));
}

function applyCurrentOperation(item) {
  const name = currentOperationName || "/";
  if (!item.tags || typeof item.tags !== "object") item.tags = {};
  item.tags["ai.operation.name"] = name;
  if (pathBased(item.tags["ai.operation.id"])) {
    item.tags["ai.operation.id"] = scrubTelemetryString(item.tags["ai.operation.id"]);
  }
  if (!item.ext || typeof item.ext !== "object") item.ext = {};
  if (!item.ext.trace || typeof item.ext.trace !== "object") item.ext.trace = {};
  item.ext.trace.name = name;
}

export function redactTelemetryItem(item) {
  if (!item || typeof item !== "object") return false;
  const baseType = String(item.baseType || "");
  if (baseType.includes("RemoteDependency") || baseType === "Ajax") return false;

  scrubNode(item, new Set());
  applyCurrentOperation(item);

  const data = item.baseData;
  const candidate = data && (data.uri || data.name);
  if (!baseType.includes("Exception") && typeof candidate === "string" && (candidate.startsWith("/") || /^https?:\/\//i.test(candidate)) && !shouldTrackPagePath(candidate)) {
    return false;
  }
  return true;
}

export function pageViewFromLocation(pathname) {
  const name = pageName(pathname);
  return {
    name,
    uri: name,
    track: shouldTrackPagePath(name)
  };
}

function pageName(pathname) {
  const redacted = redactTelemetryTarget(String(pathname || "/"));
  let path = redacted;
  if (/^https?:\/\//i.test(redacted)) {
    try {
      path = new URL(redacted).pathname;
    } catch {
      path = redacted;
    }
  }
  if (!path.startsWith("/")) path = `/${path}`;
  return path.replace(/\/$/, "") || "/";
}

export function telemetryIsEnabled() {
  return telemetryEnabled;
}

export function subscribeTelemetryEnabled(listener) {
  listeners.add(listener);
  listener(telemetryEnabled);
  return () => {
    listeners.delete(listener);
  };
}

function setTelemetryEnabled(enabled) {
  telemetryEnabled = Boolean(enabled);
  for (const listener of listeners) listener(telemetryEnabled);
}

export function resetBrowserTelemetryState() {
  client = null;
  ensurePromise = null;
  queued = null;
  lastTracked = "";
  currentOperationName = "/";
  instrumentationKey = "";
  setTelemetryEnabled(false);
}

export async function loadTelemetryConfig(fetchImpl, apiBase = "") {
  const fetchFn = fetchImpl ?? globalThis.fetch;
  if (typeof fetchFn !== "function") return { enabled: false };
  const base = String(apiBase || "").replace(/\/$/, "");
  try {
    const response = await fetchFn(`${base}/api/telemetry-config`, {
      cache: "no-store",
      headers: { Accept: "application/json" }
    });
    if (!response.ok) return { enabled: false };
    const body = await response.json();
    const connectionString = typeof body?.connectionString === "string" ? body.connectionString.trim() : "";
    if (!body?.enabled || !connectionString) return { enabled: false };
    return { enabled: true, connectionString };
  } catch {
    return { enabled: false };
  }
}

const routeListeners = new Set();

export function subscribeBrowserRoute(listener) {
  listener(typeof window === "undefined" ? currentOperationName : window.location.pathname);
  routeListeners.add(listener);
  return () => {
    routeListeners.delete(listener);
  };
}

function syncOperationName(name) {
  currentOperationName = name || "/";
  const trace = client && typeof client.getTraceCtx === "function" ? client.getTraceCtx() : null;
  if (trace && typeof trace.setName === "function") trace.setName(currentOperationName);
  const path = typeof window === "undefined" ? currentOperationName : window.location.pathname;
  for (const listener of routeListeners) listener(path);
}

function track(view) {
  if (!view) return;
  syncOperationName(view.name);
  if (lastTracked === view.name) return;
  lastTracked = view.name;
  if (!view.track || !client) return;
  client.trackPageView({ name: view.name, uri: view.uri });
}

export function noteBrowserRoute(pathname) {
  const view = pageViewFromLocation(pathname);
  syncOperationName(view.name);
  if (!client) {
    queued = view;
    return;
  }
  queued = null;
  track(view);
}

export async function startBrowserTelemetry(options = {}) {
  const path = options.pathname ?? (typeof window === "undefined" ? "/" : window.location.pathname);
  if (isAdminPath(path)) return { enabled: false };

  const fetchConfig = options.fetchConfig
    ?? (() => loadTelemetryConfig(options.fetchImpl ?? globalThis.fetch, options.apiBase ?? ""));
  const config = await fetchConfig();
  if (!config?.enabled || !config.connectionString) {
    setTelemetryEnabled(false);
    return { enabled: false };
  }

  try {
    const keyMatch = /InstrumentationKey=([^;]+)/i.exec(config.connectionString);
    instrumentationKey = keyMatch ? keyMatch[1].trim() : "";
    const sdk = await (options.loadSdk
      ? options.loadSdk()
      : import("@microsoft/applicationinsights-web"));
    const ApplicationInsights = sdk.ApplicationInsights;
    const appInsights = new ApplicationInsights({
      config: browserTelemetrySettings(config.connectionString)
    });
    appInsights.loadAppInsights();
    appInsights.addTelemetryInitializer(redactTelemetryItem);
    client = appInsights;
    setTelemetryEnabled(true);
    if (queued) {
      const pending = queued;
      queued = null;
      track(pending);
    }
    return { enabled: true };
  } catch {
    setTelemetryEnabled(false);
    return { enabled: false };
  }
}

export function ensureBrowserTelemetry(options) {
  const path = options?.pathname ?? (typeof window === "undefined" ? "/" : window.location.pathname);
  if (isAdminPath(path)) return Promise.resolve({ enabled: false });
  if (!ensurePromise) ensurePromise = startBrowserTelemetry(options);
  return ensurePromise;
}
