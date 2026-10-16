export function publicFlagMapUrls(apiBase = "") {
  const base = String(apiBase || "").replace(/\/$/, "");
  return {
    sections: `${base}/api/public/flag-map/sections`,
    field: `${base}/api/public/flag-map/field`,
    section: (section) => `${base}/api/public/flag-map/sections/${encodeURIComponent(section)}`,
    searchIndex: `${base}/api/public/flag-map/search-index`,
    bySubmitter: () => `${base}/api/public/flag-map/by-submitter`,
    submitterSuggestions: () => `${base}/api/public/flag-map/submitter-suggestions`,
    submitterFlags: () => `${base}/api/public/flag-map/submitter-flags`
  };
}

export async function lookupSubmittedFlags(first, last, fetchImpl, apiBase = "") {
  const send = fetchImpl ?? ((url, init) => fetch(url, init));
  const response = await send(publicFlagMapUrls(apiBase).bySubmitter(), {
    method: "POST",
    cache: "no-store",
    headers: {
      Accept: "application/json",
      "Content-Type": "application/json"
    },
    body: JSON.stringify({ first: first ?? "", last: last ?? "" })
  });
  let body = null;
  try {
    body = await response.json();
  } catch {
    body = null;
  }
  return { status: response.status, body };
}

async function postFlagMapJson(url, payload, fetchImpl) {
  const send = fetchImpl ?? ((target, init) => fetch(target, init));
  const response = await send(url, {
    method: "POST",
    cache: "no-store",
    headers: {
      Accept: "application/json",
      "Content-Type": "application/json"
    },
    body: JSON.stringify(payload)
  });
  let body = null;
  try {
    body = await response.json();
  } catch {
    body = null;
  }
  return { status: response.status, body };
}

export function lookupSubmitterSuggestions(query, fetchImpl, apiBase = "") {
  return postFlagMapJson(publicFlagMapUrls(apiBase).submitterSuggestions(), { query: query ?? "" }, fetchImpl);
}

export function resolveSubmitterFlags(token, fetchImpl, apiBase = "") {
  return postFlagMapJson(publicFlagMapUrls(apiBase).submitterFlags(), { token: token ?? "" }, fetchImpl);
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
      const haystack = [item?.displayName, item?.firstName, item?.middleName, item?.lastName, item?.suffix, item?.nickname]
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

const FLAG_WIDTH = 52;
const FLAG_HEIGHT = 28;
const FLAG_GAP = 3;
const SECTION_GAP = 8;
const LABEL_BAND = 36;
const FIELD_AISLE_AFTER_ROW = 20;
const FIELD_AISLE = 22;

function seatRow(seat) {
  if (Number.isFinite(seat?.columnNumber) && seat.columnNumber > 0) return seat.columnNumber;
  const digits = String(seat?.flagGridName || "").match(/(\d+)/);
  return digits ? Number(digits[1]) : null;
}

function rowTop(row, maxRow, aisle) {
  const fromTop = Math.max(0, maxRow - row);
  return fromTop * (FLAG_HEIGHT + FLAG_GAP) + (row <= FIELD_AISLE_AFTER_ROW ? aisle : 0);
}

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

export const SEAL_ON_SCREEN_LIMIT = 10;

export const PAVILION_COLUMNS_LEFT_TO_RIGHT = Object.freeze([
  "YY", "Y", "X", "W", "V", "U", "T", "S", "R", "Q", "P", "O", "N",
  "Z",
  "M", "L", "K", "J", "I", "H", "G", "F", "E", "D", "C", "B", "A", "AA"
]);

export function sealImagesAllowed(flagsOnScreen) {
  return Number(flagsOnScreen) < SEAL_ON_SCREEN_LIMIT;
}

export function pavilionColumnIndex(section) {
  const key = String(section || "").trim().toUpperCase();
  const known = PAVILION_COLUMNS_LEFT_TO_RIGHT.indexOf(key);
  return known === -1 ? PAVILION_COLUMNS_LEFT_TO_RIGHT.length : known;
}

export function branchDotColor(branchName, status) {
  if (status === "Reserved") return "#e1b84a";
  if (status !== "Occupied") return "#f4efe4";
  const value = String(branchName || "").toLowerCase();
  if (value.includes("air force")) return "#00308f";
  if (value.includes("navy")) return "#0a3161";
  if (value.includes("marine")) return "#ba0c2f";
  if (value.includes("coast")) return "#f26522";
  if (value.includes("space")) return "#5b7c99";
  if (value.includes("army")) return "#4b5320";
  return "#8a6a12";
}

export function flagMapLayerOpacities(scale, fitScale) {
  const relative = relativeFlagScale(scale, fitScale);
  return {
    relative,
    section: 1 - fadeIn(relative, FLAG_MAP_ZOOM.sectionFadeStart, FLAG_MAP_ZOOM.sectionFadeEnd),
    seal: 0,
    name: fadeIn(relative, FLAG_MAP_ZOOM.nameFadeStart, FLAG_MAP_ZOOM.nameFadeEnd),
    rank: fadeIn(relative, FLAG_MAP_ZOOM.rankFadeStart, FLAG_MAP_ZOOM.rankFadeEnd)
  };
}

export function flagMapDetailGates(scale, fitScale, flagsOnScreen = Number.POSITIVE_INFINITY) {
  const relative = relativeFlagScale(scale, fitScale);
  const opacity = flagMapLayerOpacities(scale, fitScale);
  const seals = sealImagesAllowed(flagsOnScreen);
  return {
    seals,
    names: seals && relative >= FLAG_MAP_ZOOM.nameFadeStart - 0.02,
    ranks: seals && relative >= FLAG_MAP_ZOOM.rankFadeStart - 0.02,
    sectionsInteractive: opacity.section > 0.2 && !seals,
    flagsInteractive: seals
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

export function fitWholeField(bounds, viewport, padding = 36) {
  const innerWidth = Math.max(1, viewport.width - padding * 2);
  const innerHeight = Math.max(1, viewport.height - padding * 2);
  const scale = Math.min(
    FLAG_MAP_ZOOM.maxScale,
    innerWidth / Math.max(bounds.width, 1),
    innerHeight / Math.max(bounds.height, 1)
  );
  return {
    scale,
    x: (viewport.width - bounds.width * scale) / 2 - bounds.x * scale,
    y: (viewport.height - bounds.height * scale) / 2 - bounds.y * scale
  };
}

export function flagMapPageBands(size, { kiosk = false } = {}) {
  if (kiosk) {
    return { header: 0, tools: 196, bottom: 16, side: 22, mapPadTop: 18, mapPadBottom: 0 };
  }
  const narrow = size.width <= 760;
  const short = size.height <= 640 || size.width <= 360;
  if (short) {
    return { header: 104, tools: 96, bottom: 24, side: 8, mapPadTop: 4, mapPadBottom: 4 };
  }
  if (narrow) {
    return { header: 120, tools: 108, bottom: 28, side: 12, mapPadTop: 6, mapPadBottom: 4 };
  }
  return { header: 92, tools: 104, bottom: 28, side: 16, mapPadTop: 6, mapPadBottom: 4 };
}

export function projectFieldBox(box, view, frame) {
  const left = frame.x + view.x + box.x * view.scale;
  const top = frame.y + view.y + box.y * view.scale;
  const width = box.width * view.scale;
  const height = box.height * view.scale;
  return { left, top, width, height, right: left + width, bottom: top + height };
}

export function boxInsideViewport(box, viewport, slack = 0.5) {
  return Boolean(box)
    && box.left >= -slack
    && box.top >= -slack
    && box.right <= viewport.width + slack
    && box.bottom <= viewport.height + slack;
}

export function initialOverviewPlacement(field, windowSize, options = {}) {
  const bands = options.bands ?? flagMapPageBands(windowSize, options);
  const frame = {
    x: bands.side,
    y: bands.header + bands.mapPadTop + bands.tools,
    width: Math.max(1, windowSize.width - bands.side * 2),
    height: Math.max(1, windowSize.height - bands.header - bands.mapPadTop - bands.tools - bands.mapPadBottom - bands.bottom)
  };
  frame.left = frame.x;
  frame.top = frame.y;
  frame.right = frame.x + frame.width;
  frame.bottom = frame.y + frame.height;
  const view = fitWholeField(
    { x: 0, y: 0, width: field.worldWidth, height: field.worldHeight },
    { width: frame.width, height: frame.height }
  );
  const row1 = boundsOf([
    ...field.cells.filter((cell) => cell.columnNumber === 1),
    ...field.rowLabels.filter((label) => label.row === 1)
  ]);
  const row40 = boundsOf([
    ...field.cells.filter((cell) => cell.columnNumber === 40),
    ...field.rowLabels.filter((label) => label.row === 40)
  ]);
  return {
    bands,
    frame,
    view,
    row1: row1 ? projectFieldBox(row1, view, frame) : null,
    row40: row40 ? projectFieldBox(row40, view, frame) : null,
    pavilion: field.pavilion ? projectFieldBox(field.pavilion, view, frame) : null
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

export function countFlagsOnScreen(flags, view, viewport) {
  return cullRects(flags, viewportWorldRect(view, viewport, 0)).length;
}

export function pointerIsTap(start, end, slop = FLAG_MAP_ZOOM.tapSlop) {
  if (!start || !end) return false;
  return Math.hypot(end.x - start.x, end.y - start.y) <= slop;
}

export const FIELD_MAP_ROWS = 40;
export const FIELD_MAP_Z_ROWS = 9;
const ROW_LABEL_WIDTH = 28;
const PAVILION_BAND = 22;

function templateFlagId(section, row) {
  return -((pavilionColumnIndex(section) + 1) * 1000 + row);
}

function mergeStadiumSections(sections) {
  const buckets = new Map();
  for (const section of PAVILION_COLUMNS_LEFT_TO_RIGHT) {
    const rows = section === "Z" ? FIELD_MAP_Z_ROWS : FIELD_MAP_ROWS;
    const seats = [];
    for (let row = 1; row <= rows; row += 1) {
      seats.push({
        flagGridId: templateFlagId(section, row),
        flagGridName: `${section}-${String(row).padStart(2, "0")}`,
        columnNumber: row,
        section,
        status: section === "Z" ? "Reserved" : "Open",
        template: true
      });
    }
    buckets.set(section, { section, seats, onFieldMap: true });
  }

  for (const section of Array.isArray(sections) ? sections : []) {
    const key = String(section?.section || "").trim().toUpperCase();
    if (!key) continue;
    if (!buckets.has(key)) {
      buckets.set(key, { section: key, seats: [], onFieldMap: false });
    }
    const bucket = buckets.get(key);
    const byRow = new Map(bucket.seats.map((seat) => [seatRow(seat), seat]));
    for (const seat of Array.isArray(section.seats) ? section.seats : []) {
      const row = seatRow(seat);
      if (!row) continue;
      byRow.set(row, {
        ...seat,
        section: key,
        columnNumber: Number.isFinite(seat.columnNumber) ? seat.columnNumber : row,
        template: false
      });
    }
    bucket.seats = bucket.onFieldMap
      ? [...byRow.values()]
      : [...byRow.values()].filter((seat) => !seat.template);
  }

  return [...buckets.values()].map((bucket) => ({
    section: bucket.section,
    flagCount: bucket.seats.length,
    occupiedCount: bucket.seats.filter((seat) => seat.status === "Occupied").length,
    seats: bucket.seats
  }));
}

export function boundsOf(items) {
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  for (const item of Array.isArray(items) ? items : []) {
    minX = Math.min(minX, item.x);
    minY = Math.min(minY, item.y);
    maxX = Math.max(maxX, item.x + item.width);
    maxY = Math.max(maxY, item.y + item.height);
  }
  if (!Number.isFinite(minX)) return null;
  return { x: minX, y: minY, width: Math.max(1, maxX - minX), height: Math.max(1, maxY - minY) };
}

export function layoutFlagField(sections) {
  const source = mergeStadiumSections(sections);
  const ordered = source
    .map((section, index) => ({ section, index }))
    .sort((left, right) => {
      const order = pavilionColumnIndex(left.section.section) - pavilionColumnIndex(right.section.section);
      if (order !== 0) return order;
      const name = String(left.section.section || "").localeCompare(String(right.section.section || ""));
      return name !== 0 ? name : left.index - right.index;
    })
    .map((entry) => entry.section);

  let maxRow = 1;
  let hasLow = false;
  let hasHigh = false;
  for (const section of ordered) {
    const seats = Array.isArray(section.seats) ? section.seats : [];
    if (seats.length === 0) maxRow = Math.max(maxRow, section.flagCount || 1);
    for (const seat of seats) {
      const row = seatRow(seat);
      if (!row) continue;
      maxRow = Math.max(maxRow, row);
      if (row <= FIELD_AISLE_AFTER_ROW) hasLow = true;
      if (row > FIELD_AISLE_AFTER_ROW) hasHigh = true;
    }
  }
  const aisleGap = hasLow && hasHigh ? FIELD_AISLE : 0;
  const fieldHeight = rowTop(1, maxRow, aisleGap) + FLAG_HEIGHT;
  const placedSections = [];
  const placedCells = [];
  const placedFlags = [];
  const rows = new Set();

  ordered.forEach((section, index) => {
    const seats = Array.isArray(section.seats) ? section.seats : [];
    const x = ROW_LABEL_WIDTH + index * (FLAG_WIDTH + SECTION_GAP);
    placedSections.push({
      section: section.section,
      flagCount: section.flagCount ?? seats.length,
      occupiedCount: section.occupiedCount ?? seats.filter((seat) => seat.status === "Occupied").length,
      x,
      y: 0,
      width: FLAG_WIDTH,
      height: fieldHeight + LABEL_BAND
    });
    for (const seat of seats) {
      const row = seatRow(seat);
      if (!row) continue;
      rows.add(row);
      const placed = {
        ...seat,
        x,
        y: rowTop(row, maxRow, aisleGap),
        width: FLAG_WIDTH,
        height: FLAG_HEIGHT
      };
      placedCells.push(placed);
      if (!seat.template) placedFlags.push(placed);
    }
  });

  const contentRight = Math.max(ROW_LABEL_WIDTH + 1, ...placedSections.map((section) => section.x + section.width));
  const zSection = placedSections.find((section) => section.section === "Z") ?? null;
  const pavilion = zSection
    ? { x: zSection.x, y: fieldHeight + LABEL_BAND, width: zSection.width, height: PAVILION_BAND, label: "Pavilion" }
    : null;
  const rowLabels = [...rows].sort((left, right) => right - left).map((row) => ({
    row,
    label: String(row),
    x: 0,
    y: rowTop(row, maxRow, aisleGap),
    width: ROW_LABEL_WIDTH - 4,
    height: FLAG_HEIGHT
  }));
  const columnLabels = placedSections.map((section) => ({
    section: section.section,
    label: section.section,
    x: section.x,
    y: fieldHeight,
    width: section.width,
    height: LABEL_BAND
  }));
  const aisle = aisleGap
    ? {
        x: ROW_LABEL_WIDTH,
        y: rowTop(FIELD_AISLE_AFTER_ROW + 1, maxRow, aisleGap) + FLAG_HEIGHT,
        width: Math.max(1, contentRight - ROW_LABEL_WIDTH),
        height: aisleGap
      }
    : null;

  return {
    sections: placedSections,
    cells: placedCells,
    flags: placedFlags,
    rowLabels,
    columnLabels,
    aisle,
    pavilion,
    worldWidth: contentRight,
    worldHeight: Math.max(fieldHeight + LABEL_BAND, pavilion ? pavilion.y + pavilion.height : 0, 1)
  };
}

export function gestureTransform(view) {
  return `translate(${view.x}px, ${view.y}px) scale(${view.scale})`;
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

export function createPinchBatch() {
  let next = null;
  return {
    push(view) {
      next = view;
    },
    flush() {
      if (!next) return null;
      const view = next;
      next = null;
      return view;
    }
  };
}

export function pinchView(origin, startMid, distance, startDistance, mid) {
  const scale = origin.scale * (distance / startDistance);
  const zoomed = zoomAt(origin, startMid.x, startMid.y, scale);
  return panView(zoomed, mid.x - startMid.x, mid.y - startMid.y);
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
