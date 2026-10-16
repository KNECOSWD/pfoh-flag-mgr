export function publicFlagMapUrls(apiBase = "") {
  const base = String(apiBase || "").replace(/\/$/, "");
  return {
    sections: `${base}/api/public/flag-map/sections`,
    section: (section) => `${base}/api/public/flag-map/sections/${encodeURIComponent(section)}`,
    searchIndex: `${base}/api/public/flag-map/search-index`,
    bySubmitter: (first, last) => {
      const params = new URLSearchParams();
      params.set("first", first ?? "");
      params.set("last", last ?? "");
      return `${base}/api/public/flag-map/by-submitter?${params.toString()}`;
    }
  };
}

export async function lookupSubmittedFlags(first, last, fetchImpl, apiBase = "") {
  const send = fetchImpl ?? ((url, init) => fetch(url, init));
  const response = await send(publicFlagMapUrls(apiBase).bySubmitter(first, last), {
    cache: "no-store",
    headers: { Accept: "application/json" }
  });
  let body = null;
  try {
    body = await response.json();
  } catch {
    body = null;
  }
  return { status: response.status, body };
}

export function withApiBase(apiBase, path) {
  if (!path) return "";
  if (/^https?:\/\//i.test(path)) return path;
  const base = String(apiBase || "").replace(/\/$/, "");
  return `${base}${path.startsWith("/") ? path : `/${path}`}`;
}

export function filterFlagMapSuggestions(index, query, limit = 8) {
  const needle = String(query || "").trim().toLowerCase();
  if (!needle || !Array.isArray(index)) return [];

  return index
    .filter((item) => {
      const haystack = [item?.displayName, item?.firstName, item?.lastName, item?.nickname]
        .filter(Boolean)
        .join(" ")
        .toLowerCase();
      return haystack.includes(needle);
    })
    .slice(0, limit);
}

export function flagYears(seat) {
  const written = seat?.datesUserEntry?.trim();
  if (written) return written;
  if (seat?.startYear && seat?.endYear) return `${seat.startYear}–${seat.endYear}`;
  if (seat?.startYear) return String(seat.startYear);
  if (seat?.endYear) return String(seat.endYear);
  return "";
}

export const FLAG_MAP_ZOOM = {
  minScale: 0.05,
  maxScale: 12,
  zoomStep: 1.2,
  panStep: 72,
  tapSlop: 8,
  sectionFadeStart: 1.2,
  sectionFadeEnd: 1.9,
  sealFadeStart: 1.2,
  sealFadeEnd: 1.9,
  nameFadeStart: 2.2,
  nameFadeEnd: 2.8,
  rankFadeStart: 3.15,
  rankFadeEnd: 4
};

export const KIOSK_IDLE_MS = 60_000;
export const WHEEL_ZOOM_SENSITIVITY = 0.0015;
export const WHEEL_SETTLE_MS = 120;

const FLAG_WIDTH = 148;
const FLAG_HEIGHT = 108;
const FLAG_GAP = 12;
const SECTION_PAD = 28;
const SECTION_GAP = 56;

export function clamp(value, min, max) {
  return Math.min(max, Math.max(min, value));
}

function smoothstep(amount) {
  const t = clamp(amount, 0, 1);
  return t * t * (3 - 2 * t);
}

function fadeIn(relative, start, end) {
  if (relative <= start) return 0;
  if (relative >= end) return 1;
  return smoothstep((relative - start) / (end - start));
}

export function relativeFlagScale(scale, fitScale) {
  const base = fitScale > 0 ? fitScale : 1;
  return scale / base;
}

export function flagMapLayerOpacities(scale, fitScale) {
  const relative = relativeFlagScale(scale, fitScale);
  const seal = fadeIn(relative, FLAG_MAP_ZOOM.sealFadeStart, FLAG_MAP_ZOOM.sealFadeEnd);
  return {
    relative,
    section: 1 - fadeIn(relative, FLAG_MAP_ZOOM.sectionFadeStart, FLAG_MAP_ZOOM.sectionFadeEnd),
    seal,
    name: fadeIn(relative, FLAG_MAP_ZOOM.nameFadeStart, FLAG_MAP_ZOOM.nameFadeEnd),
    rank: fadeIn(relative, FLAG_MAP_ZOOM.rankFadeStart, FLAG_MAP_ZOOM.rankFadeEnd)
  };
}

export function flagMapDetailGates(scale, fitScale) {
  const relative = relativeFlagScale(scale, fitScale);
  const opacity = flagMapLayerOpacities(scale, fitScale);
  return {
    seals: relative >= FLAG_MAP_ZOOM.sealFadeStart - 0.02,
    names: relative >= FLAG_MAP_ZOOM.nameFadeStart - 0.02,
    ranks: relative >= FLAG_MAP_ZOOM.rankFadeStart - 0.02,
    sectionsInteractive: opacity.section > 0.2,
    flagsInteractive: opacity.seal > 0.35
  };
}

export function zoomAt(view, originX, originY, nextScale) {
  const scale = clamp(nextScale, FLAG_MAP_ZOOM.minScale, FLAG_MAP_ZOOM.maxScale);
  const worldX = (originX - view.x) / view.scale;
  const worldY = (originY - view.y) / view.scale;
  return {
    x: originX - worldX * scale,
    y: originY - worldY * scale,
    scale
  };
}

export function panView(view, dx, dy) {
  return { x: view.x + dx, y: view.y + dy, scale: view.scale };
}

export function fitView(bounds, viewport, padding = 36) {
  const innerWidth = Math.max(1, viewport.width - padding * 2);
  const innerHeight = Math.max(1, viewport.height - padding * 2);
  const scale = clamp(
    Math.min(innerWidth / Math.max(bounds.width, 1), innerHeight / Math.max(bounds.height, 1)),
    FLAG_MAP_ZOOM.minScale,
    FLAG_MAP_ZOOM.maxScale
  );
  return {
    scale,
    x: (viewport.width - bounds.width * scale) / 2 - bounds.x * scale,
    y: (viewport.height - bounds.height * scale) / 2 - bounds.y * scale
  };
}

export function viewportWorldRect(view, viewport, pad = 0) {
  return {
    x: (0 - view.x) / view.scale - pad,
    y: (0 - view.y) / view.scale - pad,
    width: viewport.width / view.scale + pad * 2,
    height: viewport.height / view.scale + pad * 2
  };
}

export function rectsIntersect(a, b) {
  return a.x < b.x + b.width && a.x + a.width > b.x && a.y < b.y + b.height && a.y + a.height > b.y;
}

export function cullRects(items, viewRect) {
  return items.filter((item) => rectsIntersect(item, viewRect));
}

export function pointerIsTap(start, end, slop = FLAG_MAP_ZOOM.tapSlop) {
  if (!start || !end) return false;
  return Math.hypot(end.x - start.x, end.y - start.y) <= slop;
}

export function layoutFlagField(sections) {
  const columns = Math.max(1, Math.ceil(Math.sqrt(sections?.length || 1)));
  const placedSections = [];
  const placedFlags = [];
  let cursorX = 0;
  let cursorY = 0;
  let rowHeight = 0;
  let columnIndex = 0;

  for (const section of sections || []) {
    const seats = Array.isArray(section.seats) ? section.seats : [];
    const count = Math.max(seats.length, section.flagCount || 0, 1);
    const flagColumns = Math.max(1, Math.ceil(Math.sqrt(count)));
    const flagRows = Math.ceil(count / flagColumns);
    const width = SECTION_PAD * 2 + flagColumns * FLAG_WIDTH + Math.max(0, flagColumns - 1) * FLAG_GAP;
    const height = SECTION_PAD * 2 + flagRows * FLAG_HEIGHT + Math.max(0, flagRows - 1) * FLAG_GAP;

    if (columnIndex >= columns) {
      columnIndex = 0;
      cursorX = 0;
      cursorY += rowHeight + SECTION_GAP;
      rowHeight = 0;
    }

    placedSections.push({
      section: section.section,
      flagCount: section.flagCount ?? seats.length,
      occupiedCount: section.occupiedCount ?? seats.filter((seat) => seat.status === "Occupied").length,
      x: cursorX,
      y: cursorY,
      width,
      height
    });

    seats.forEach((seat, index) => {
      const column = index % flagColumns;
      const row = Math.floor(index / flagColumns);
      placedFlags.push({
        ...seat,
        x: cursorX + SECTION_PAD + column * (FLAG_WIDTH + FLAG_GAP),
        y: cursorY + SECTION_PAD + row * (FLAG_HEIGHT + FLAG_GAP),
        width: FLAG_WIDTH,
        height: FLAG_HEIGHT
      });
    });

    rowHeight = Math.max(rowHeight, height);
    cursorX += width + SECTION_GAP;
    columnIndex += 1;
  }

  return {
    sections: placedSections,
    flags: placedFlags,
    worldWidth: Math.max(1, ...placedSections.map((section) => section.x + section.width), 1),
    worldHeight: Math.max(1, ...placedSections.map((section) => section.y + section.height), 1)
  };
}

export function readFlagMapLocation(search = "") {
  const params = new URLSearchParams(String(search).replace(/^\?/, ""));
  const kioskValue = (params.get("kiosk") || "").trim().toLowerCase();
  const flagRaw = (params.get("flag") || "").trim();
  const flagValid = /^[0-9]+$/.test(flagRaw);
  return {
    kiosk: kioskValue === "1" || kioskValue === "true" || kioskValue === "yes",
    flagId: flagValid ? Number(flagRaw) : null,
    flagRejected: params.has("flag") && flagRaw !== "" && !flagValid
  };
}

export function flagDeepLink(href, flagId) {
  const current = new URL(href, "http://localhost");
  const link = new URL("/flag-map", current.origin);
  link.searchParams.set("flag", String(flagId));
  return link.toString();
}

export function createFlagMapLoader(fetchImpl) {
  const send = fetchImpl ?? ((url, init) => fetch(url, init));
  const etags = new Map();
  const bodies = new Map();
  const inflight = new Map();
  return function loadFlagMapJson(url) {
    const pending = inflight.get(url);
    if (pending) return pending;
    const promise = read(url).finally(() => {
      if (inflight.get(url) === promise) inflight.delete(url);
    });
    inflight.set(url, promise);
    return promise;
  };

  async function read(url) {
    const headers = { Accept: "application/json" };
    const known = etags.get(url);
    if (known) headers["If-None-Match"] = known;
    const response = await send(url, { cache: "no-store", headers });
    if (response.status === 304) {
      if (!bodies.has(url)) throw new Error("The flag map could not be loaded (304).");
      return bodies.get(url);
    }
    if (!response.ok) throw new Error(`The flag map could not be loaded (${response.status}).`);
    const etag = response.headers?.get?.("ETag");
    if (etag) etags.set(url, etag);
    const body = await response.json();
    bodies.set(url, body);
    return body;
  }
}

export function springEase(amount) {
  const t = clamp(amount, 0, 1);
  if (t >= 1) return 1;
  const decay = Math.exp(-5.4 * t);
  const wave = Math.cos(t * Math.PI * 3);
  const value = 1 - decay * wave;
  if (t > 0.9) return value + (1 - value) * ((t - 0.9) / 0.1);
  return value;
}

export function applyKioskIdleReset(state = {}) {
  const fit = state.fitView ?? { x: 0, y: 0, scale: 1 };
  return {
    query: "",
    open: false,
    active: 0,
    selected: null,
    highlightId: null,
    revealId: null,
    history: [],
    searchTab: "honoree",
    submitterFirst: "",
    submitterLast: "",
    submitterNote: "",
    submitterHits: [],
    view: { x: fit.x, y: fit.y, scale: fit.scale }
  };
}

export function attractView(fitted, elapsedMs, viewport, reducedMotion = false) {
  if (reducedMotion) return { x: fitted.x, y: fitted.y, scale: fitted.scale };
  const ampX = Math.min(Math.max(viewport?.width || 1, 1) * 0.16, 160);
  const ampY = Math.min(Math.max(viewport?.height || 1, 1) * 0.1, 96);
  const t = elapsedMs / 1000;
  return {
    x: fitted.x + Math.sin(t * 0.21) * ampX,
    y: fitted.y + Math.cos(t * 0.13) * ampY,
    scale: fitted.scale
  };
}

export function pointerDragCapture(start, point, alreadyCaptured = false) {
  if (alreadyCaptured) return true;
  return !pointerIsTap(start, point);
}

export function pointerShouldOpen(start, point) {
  return pointerIsTap(start, point);
}

export function createWheelBatch() {
  let deltaY = 0;
  let point = null;
  let dirty = false;
  return {
    push(delta, origin) {
      deltaY += delta;
      point = origin;
      dirty = true;
    },
    flush() {
      if (!dirty || !point) return null;
      const batch = { deltaY, point };
      deltaY = 0;
      point = null;
      dirty = false;
      return batch;
    }
  };
}

export function wheelScaleFactor(deltaY) {
  return Math.exp(-deltaY * WHEEL_ZOOM_SENSITIVITY);
}

export function wheelFrameShouldCull(previousGates, nextGates, gestureEnded) {
  if (gestureEnded) return true;
  if (!previousGates || !nextGates) return true;
  return previousGates.seals !== nextGates.seals
    || previousGates.names !== nextGates.names
    || previousGates.ranks !== nextGates.ranks
    || previousGates.sectionsInteractive !== nextGates.sectionsInteractive
    || previousGates.flagsInteractive !== nextGates.flagsInteractive;
}
