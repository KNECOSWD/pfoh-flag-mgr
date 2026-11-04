const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const PUBLIC_CODE = /^PFOH-[A-Za-z0-9]{4,12}$/i;
const OPAQUE = /^[A-Za-z0-9_-]{20,}$/;
const TOKEN_ROUTES = new Set(["reactivate", "submission", "receipt", "pending"]);
const URL_FIELDS = ["uri", "name", "url", "refUri"];

let client = null;
let ensurePromise = null;
let queued = null;
let lastTracked = "";
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
    loggingLevelConsole: 0
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

export function redactTelemetryText(value) {
  const raw = String(value ?? "");
  if (!raw) return raw;
  if (raw.startsWith("/") || /^https?:\/\//i.test(raw) || raw.includes("?") || raw.includes("#")) {
    return redactTelemetryTarget(raw);
  }
  return raw;
}

export function redactTelemetryItem(item) {
  if (!item || typeof item !== "object") return false;
  const baseType = String(item.baseType || "");
  if (baseType.includes("RemoteDependency") || baseType === "Ajax") return false;

  const data = item.baseData;
  if (data && typeof data === "object") {
    for (const key of URL_FIELDS) {
      if (typeof data[key] === "string") data[key] = redactTelemetryText(data[key]);
    }
  }
  if (item.tags && typeof item.tags === "object") {
    for (const key of Object.keys(item.tags)) {
      if (typeof item.tags[key] === "string") item.tags[key] = redactTelemetryText(item.tags[key]);
    }
  }

  const candidate = data && (data.uri || data.name);
  if (typeof candidate === "string" && (candidate.startsWith("/") || /^https?:\/\//i.test(candidate)) && !shouldTrackPagePath(candidate)) {
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

function track(view) {
  if (!view || lastTracked === view.name) return;
  lastTracked = view.name;
  if (!view.track || !client) return;
  client.trackPageView({ name: view.name, uri: view.uri });
}

export function noteBrowserRoute(pathname) {
  const view = pageViewFromLocation(pathname);
  if (!client) {
    queued = view;
    return;
  }
  queued = null;
  track(view);
}

export async function startBrowserTelemetry(options = {}) {
  const fetchConfig = options.fetchConfig
    ?? (() => loadTelemetryConfig(options.fetchImpl ?? globalThis.fetch, options.apiBase ?? ""));
  const config = await fetchConfig();
  if (!config?.enabled || !config.connectionString) {
    setTelemetryEnabled(false);
    return { enabled: false };
  }

  try {
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
  if (!ensurePromise) ensurePromise = startBrowserTelemetry(options);
  return ensurePromise;
}
