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

export const SUBMITTER_SUGGESTION_DEBOUNCE_MS = 150;
export const MAP_FULLSCREEN_CLASS = "isMapFullscreen";

// Zoom thresholds are the on-screen width of one spot in CSS pixels, so every
// window size reaches the same level when a spot is the same size on screen.
export const FLAG_MAP_ZOOM = {
  minScale: 0.02,
  maxScale: 4,
  zoomStep: 1.25,
  panStep: 72,
  tapSlop: 8,
  spotsFadeStart: 30,
  spotsFadeEnd: 42,
  namesAt: 100,
  closestAt: 180
};

export const FLAG_MAP_LEVELS = Object.freeze(["Sections", "Spots", "Names", "Closest"]);

export const KIOSK_IDLE_MS = 60_000;
export const WHEEL_ZOOM_SENSITIVITY = 0.0015;
export const WHEEL_SETTLE_MS = 120;

const FLAG_WIDTH = 148;
const FLAG_HEIGHT = 108;
const FLAG_GAP = 12;
const SECTION_PAD = 28;
const SECTION_GAP = 56;
export const SPOT_SIZE = Object.freeze({ width: FLAG_WIDTH, height: FLAG_HEIGHT, gap: FLAG_GAP });
export const FIELD_AISLE_AFTER_ROW = 20;
export const FIT_PADDING = 12;
const ROW_LABEL_WIDTH = 160;
const LABEL_BAND = 96;
export const PAVILION_SIZE = Object.freeze({ width: (FLAG_WIDTH * 3 + FLAG_GAP * 2) * 4, height: 84 * 2 });
const CHAR_EM = 0.62;
const NUMBER_EM = 0.72;
const LINE_HEIGHT = 1.15;

function seatRow(seat) {
  if (Number.isFinite(seat?.columnNumber) && seat.columnNumber > 0) return seat.columnNumber;
  const digits = String(seat?.flagGridName || "").match(/(\d+)/);
  return digits ? Number(digits[1]) : null;
}

function rowTop(row, maxRow, aisle) {
  const fromTop = Math.max(0, maxRow - row);
  return SECTION_PAD + fromTop * (FLAG_HEIGHT + FLAG_GAP) + (row <= FIELD_AISLE_AFTER_ROW ? aisle : 0);
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

export function spotScreenWidth(scale) {
  return FLAG_WIDTH * (Number(scale) > 0 ? Number(scale) : 0);
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

export function flagMapLayerOpacities(scale) {
  const spotPx = spotScreenWidth(scale);
  return {
    spotPx,
    section: 1 - fadeIn(spotPx, FLAG_MAP_ZOOM.spotsFadeStart, FLAG_MAP_ZOOM.spotsFadeEnd)
  };
}

export function flagMapDetailGates(scale, flagsOnScreen = Number.POSITIVE_INFINITY) {
  const opacity = flagMapLayerOpacities(scale);
  const spotsVisible = opacity.section <= 0.5;
  const names = spotsVisible && opacity.spotPx >= FLAG_MAP_ZOOM.namesAt;
  const seals = names && opacity.spotPx >= FLAG_MAP_ZOOM.closestAt && sealImagesAllowed(flagsOnScreen);
  return {
    level: seals ? "Closest" : names ? "Names" : spotsVisible ? "Spots" : "Sections",
    numbers: spotsVisible,
    names,
    seals,
    ranks: seals,
    sectionsInteractive: !spotsVisible,
    flagsInteractive: spotsVisible
  };
}

export function closestViewScale(viewport) {
  const width = Math.max(1, Number(viewport?.width) || 1);
  const height = Math.max(1, Number(viewport?.height) || 1);
  const across = 2.6;
  const scale = Math.max(
    width / ((FLAG_WIDTH + FLAG_GAP) * across),
    height / ((FLAG_HEIGHT + FLAG_GAP) * across),
    (FLAG_MAP_ZOOM.closestAt + 1) / FLAG_WIDTH
  );
  return clamp(scale, FLAG_MAP_ZOOM.minScale, FLAG_MAP_ZOOM.maxScale);
}

export function spotsViewScale() {
  return (FLAG_MAP_ZOOM.spotsFadeEnd + 2) / FLAG_WIDTH;
}

export function clampView(view, viewport, world, options = {}) {
  const minScale = Number(options.minScale) > 0 ? Number(options.minScale) : FLAG_MAP_ZOOM.minScale;
  const maxScale = Math.max(minScale, Number(options.maxScale) || FLAG_MAP_ZOOM.maxScale);
  const pad = Number.isFinite(options.pad) ? options.pad : FIT_PADDING;
  const width = Math.max(1, viewport.width);
  const height = Math.max(1, viewport.height);
  let { x, y } = view;
  const scale = clamp(view.scale, minScale, maxScale);
  if (scale !== view.scale && view.scale > 0) {
    x = width / 2 - ((width / 2 - view.x) / view.scale) * scale;
    y = height / 2 - ((height / 2 - view.y) / view.scale) * scale;
  }
  const axis = (position, size, start, length) => {
    const content = length * scale;
    if (content + pad * 2 <= size + 0.5) return (size - content) / 2 - start * scale;
    return clamp(position, size - pad - (start + length) * scale, pad - start * scale);
  };
  return {
    x: axis(x, width, world.x, world.width),
    y: axis(y, height, world.y, world.height),
    scale
  };
}

// Each spot is either fully inside (with room) or under `hidden` of it on
// screen. The shifts that allow that are a union of intervals per spot, so
// the smallest allowed shift is 0 or one of their endpoints. A settle never
// moves more than a spot, so it cannot jump to a far part of the field.
function axisShift(offset, size, starts, span, room, hidden, limits = [-Infinity, Infinity]) {
  if (span > size - room * 2) return null;
  const allowed = [[limits], ...starts.map((start) => {
    const from = offset + start;
    return [
      [-Infinity, span * hidden - (from + span)],
      [room - from, size - room - (from + span)],
      [size - span * hidden - from, Infinity]
    ];
  })];
  const candidates = [0, ...allowed.flat(2).filter((shift) => Math.abs(shift) <= span + room)]
    .sort((left, right) => Math.abs(left) - Math.abs(right));
  const fits = (shift) => allowed.every((ranges) => ranges.some(([low, high]) => shift >= low - 1e-6 && shift <= high + 1e-6));
  const shift = candidates.find(fits);
  return shift === undefined ? null : shift;
}

const SNAP_SCALE_STEPS = Object.freeze([1, 1.01, 0.99, 1.02, 0.98, 1.03, 0.97, 1.04, 0.96, 1.05, 0.95, 1.06, 0.94]);

// On a phone the closest zoom stops where one spot still fits with padding,
// but never before the Closest level.
export function spotFitMaxScale(viewport, pad = FIT_PADDING) {
  const fit = Math.min((viewport.width - pad * 2) / FLAG_WIDTH, (viewport.height - pad * 2) / FLAG_HEIGHT);
  return clamp(fit, (FLAG_MAP_ZOOM.closestAt + 1) / FLAG_WIDTH, FLAG_MAP_ZOOM.maxScale);
}

// From the names zoom in, a view that settles leaves every spot that is more
// than half on screen fully inside with `pad` around it. When no pan can do
// that at the requested scale, the scale moves up to 6% within the same level.
export function snapEdgeSpots(view, viewport, cells, pad = FIT_PADDING, options = {}) {
  const spotPx = spotScreenWidth(view.scale);
  if (spotPx < FLAG_MAP_ZOOM.namesAt) return view;
  const width = Math.max(1, viewport.width);
  const height = Math.max(1, viewport.height);
  const spots = (Array.isArray(cells) ? cells : []).filter((cell) => cell.serviceFlag || cell.status !== "Reserved");
  const closest = spotPx >= FLAG_MAP_ZOOM.closestAt;
  const low = Math.max(Number(options.minScale) || 0, (closest ? FLAG_MAP_ZOOM.closestAt : FLAG_MAP_ZOOM.namesAt) / FLAG_WIDTH);
  const high = Math.min(Number(options.maxScale) || FLAG_MAP_ZOOM.maxScale, closest ? Infinity : (FLAG_MAP_ZOOM.closestAt - 0.01) / FLAG_WIDTH);
  const unique = (values) => [...new Set(values)];
  const world = options.world;
  const clampSize = options.clampViewport ?? { width, height };
  // The shifts clampView would keep, so a settle is never undone by the clamp.
  const limits = (position, size, start, length, scale) => {
    if (!world) return [-Infinity, Infinity];
    if (length * scale + pad * 2 <= size + 0.5) return [0, 0];
    return [size - pad - (start + length) * scale - position, pad - start * scale - position];
  };
  const attempt = (scale, room, hidden) => {
    const at = scale === view.scale ? view : zoomAt(view, width / 2, height / 2, scale);
    const seen = viewportWorldRect(at, { width, height }, 0);
    const columns = unique(spots.filter((cell) => cell.y < seen.y + seen.height && cell.y + cell.height > seen.y).map((cell) => cell.x * at.scale));
    const rows = unique(spots.filter((cell) => cell.x < seen.x + seen.width && cell.x + cell.width > seen.x).map((cell) => cell.y * at.scale));
    if (!columns.length || !rows.length) return null;
    const dx = axisShift(at.x, width, columns, FLAG_WIDTH * at.scale, room, hidden,
      limits(at.x, clampSize.width, world?.x ?? 0, world?.width ?? 0, at.scale));
    const dy = dx === null ? null : axisShift(at.y, height, rows, FLAG_HEIGHT * at.scale, room, hidden,
      limits(at.y, clampSize.height, world?.y ?? 0, world?.height ?? 0, at.scale));
    return dy === null ? null : { x: at.x + dx, y: at.y + dy, scale: at.scale };
  };
  for (const hidden of [0.45, 0.5]) {
    for (const step of SNAP_SCALE_STEPS) {
      const scale = view.scale * step;
      if (step !== 1 && (scale < low || scale > high)) continue;
      const settled = attempt(scale, pad, hidden);
      if (settled) return settled;
    }
  }
  for (let room = pad - 1; room >= 0; room -= 1) {
    const settled = attempt(view.scale, room, 0.5);
    if (settled) return settled;
  }
  return view;
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

export function fitWholeField(bounds, viewport, padding = FIT_PADDING) {
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

// `legend` is the Occupied/Open key under the bottom-right corner of the field,
// so the key never covers a spot.
export function flagMapPageBands(size, { kiosk = false } = {}) {
  if (kiosk) {
    return { header: 0, tools: 196, bottom: 16, side: 22, mapPadTop: 18, mapPadBottom: 0, legend: 30 };
  }
  const narrow = size.width <= 760;
  const short = size.height <= 640 || size.width <= 360;
  if (short) {
    return { header: 104, tools: 96, bottom: 24, side: 8, mapPadTop: 4, mapPadBottom: 4, legend: 20 };
  }
  if (narrow) {
    return { header: 120, tools: 108, bottom: 28, side: 12, mapPadTop: 6, mapPadBottom: 4, legend: 20 };
  }
  return { header: 92, tools: 104, bottom: 28, side: 16, mapPadTop: 6, mapPadBottom: 4, legend: 20 };
}

function zoneOrder(left, right) {
  return left.x - right.x || left.y - right.y;
}

// Service Flags are Z 1–10, so they sort as Z 1, ahead of Z 11–20.
function inViewSortKey(item) {
  if (item?.kind === "service") return { letters: SERVICE_FLAGS_SECTION, number: 1 };
  const name = item?.kind === "spot" ? item.name ?? item.label : item?.label;
  const match = /^([A-Za-z]+)[\s-]*(\d+)?/.exec(String(name ?? ""));
  return match ? { letters: match[1].toUpperCase(), number: Number(match[2] ?? 0) } : { letters: String(name ?? ""), number: 0 };
}

// A, AA, B ... YY, Z by letter, then by the first flag number, so A-2 comes
// before A-10 and C 1–20 before C 21–40.
export function compareInViewItems(left, right) {
  const a = inViewSortKey(left);
  const b = inViewSortKey(right);
  if (a.letters !== b.letters) return a.letters < b.letters ? -1 : 1;
  return a.number - b.number;
}

// What the "in view" list offers: the section blocks while zoomed out, and the
// flag spots once the zoom shows flag numbers. Anything with part of its box
// inside the map window counts.
export function flagMapItemsInView(field, view, viewport, gates = {}) {
  if (!field || !(view?.scale > 0)) return { kind: "sections", items: [] };
  const seen = viewportWorldRect(view, viewport, 0);
  const items = [];
  const service = field.serviceFlags && rectsIntersect(field.serviceFlags, seen)
    ? { key: "service", kind: "service", label: `${field.serviceFlags.label}, ${SERVICE_FLAGS_SECTION} 1\u2013${FIELD_MAP_Z_ROWS}`, box: field.serviceFlags }
    : null;
  if (!gates.numbers) {
    for (const zone of [...cullRects(field.zones, seen)].sort(zoneOrder)) {
      items.push({ key: `zone:${zone.label}`, kind: "zone", label: zone.label, box: zone });
    }
    if (service) items.push(service);
    return { kind: "sections", items: items.sort(compareInViewItems) };
  }
  const spots = cullRects(field.cells, seen)
    .filter((cell) => !cell.serviceFlag && !cell.template && cell.flagGridId > 0 && cell.status !== "Reserved")
    .sort(zoneOrder);
  for (const cell of spots) {
    const caption = cell.status === "Occupied" ? (cell.displayName || "Occupied") : "Open";
    items.push({ key: `spot:${cell.flagGridId}`, kind: "spot", name: cell.flagGridName, label: `${cell.flagGridName} \u00b7 ${caption}`, box: cell, flagGridId: cell.flagGridId });
  }
  if (service) items.push(service);
  return { kind: "spots", items: items.sort(compareInViewItems) };
}

// The closed list reads like the old level label, such as "Sections (56)".
export function flagMapItemsSummary(list) {
  const count = list?.items?.length ?? 0;
  return `${list?.kind === "sections" ? "Sections" : "Spots"} (${count})`;
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
    height: Math.max(1, windowSize.height - bands.header - bands.mapPadTop - bands.tools - (bands.legend ?? 0) - bands.mapPadBottom - bands.bottom)
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
export const FIELD_MAP_Z_ROWS = 10;
export const SERVICE_FLAGS_SECTION = "Z";
export const SERVICE_FLAGS_LABEL = "Service Flags";

export function isServiceFlagSeat(seat) {
  const row = Number(seat?.columnNumber);
  return String(seat?.section || "").toUpperCase() === SERVICE_FLAGS_SECTION
    && Number.isFinite(row) && row >= 1 && row <= FIELD_MAP_Z_ROWS;
}

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
  const aisleGap = hasLow && hasHigh ? SECTION_GAP - FLAG_GAP : 0;
  const fieldHeight = rowTop(1, maxRow, aisleGap) + FLAG_HEIGHT;
  const gridLeft = SECTION_PAD + ROW_LABEL_WIDTH + FLAG_GAP;
  const labelTop = fieldHeight + FLAG_GAP;
  const placedSections = [];
  const placedCells = [];
  const placedFlags = [];
  const rows = new Set();

  let nextX = gridLeft;
  ordered.forEach((section, index) => {
    const seats = Array.isArray(section.seats) ? section.seats : [];
    // Z sits right against N; the only aisle between columns is Z to M.
    const afterZ = ordered[index - 1]?.section === "Z";
    const x = index === 0 ? nextX : nextX + (afterZ ? SECTION_GAP : FLAG_GAP);
    nextX = x + FLAG_WIDTH;
    placedSections.push({
      section: section.section,
      flagCount: section.flagCount ?? seats.length,
      occupiedCount: section.occupiedCount ?? seats.filter((seat) => seat.status === "Occupied").length,
      x,
      y: 0,
      width: FLAG_WIDTH,
      height: labelTop + LABEL_BAND
    });
    for (const seat of seats) {
      const row = seatRow(seat);
      if (!row) continue;
      rows.add(row);
      const serviceFlag = isServiceFlagSeat({ section: section.section, columnNumber: row });
      const placed = {
        ...seat,
        serviceFlag,
        x,
        y: rowTop(row, maxRow, aisleGap),
        width: FLAG_WIDTH,
        height: FLAG_HEIGHT
      };
      placedCells.push(placed);
      if (!seat.template && !serviceFlag) placedFlags.push(placed);
    }
  });

  const contentRight = Math.max(gridLeft + 1, ...placedSections.map((section) => section.x + section.width));
  const zSection = placedSections.find((section) => section.section === "Z") ?? null;
  const pavilion = zSection
    ? {
        x: zSection.x + (zSection.width - PAVILION_SIZE.width) / 2,
        y: labelTop + LABEL_BAND + FLAG_GAP,
        width: PAVILION_SIZE.width,
        height: PAVILION_SIZE.height,
        label: "Pavilion"
      }
    : null;
  const rightLabelX = contentRight + FLAG_GAP;
  const rowLabels = [...rows].sort((left, right) => right - left).flatMap((row) => ["left", "right"].map((side) => ({
    row,
    side,
    label: String(row),
    x: side === "left" ? SECTION_PAD : rightLabelX,
    y: rowTop(row, maxRow, aisleGap),
    width: ROW_LABEL_WIDTH,
    height: FLAG_HEIGHT
  })));
  const columnLabels = placedSections.map((section) => ({
    section: section.section,
    label: section.section,
    x: section.x,
    y: labelTop,
    width: section.width,
    height: LABEL_BAND
  }));
  const aisle = aisleGap
    ? {
        x: gridLeft,
        y: rowTop(FIELD_AISLE_AFTER_ROW + 1, maxRow, aisleGap) + FLAG_HEIGHT,
        width: Math.max(1, contentRight - gridLeft),
        height: SECTION_GAP
      }
    : null;
  const bottom = pavilion ? pavilion.y + pavilion.height : labelTop + LABEL_BAND;
  const serviceBox = boundsOf(placedCells.filter((cell) => cell.serviceFlag));
  const serviceFlags = serviceBox ? { ...serviceBox, label: SERVICE_FLAGS_LABEL } : null;

  return {
    sections: placedSections,
    cells: placedCells,
    flags: placedFlags,
    rowLabels,
    columnLabels,
    aisle,
    pavilion,
    serviceFlags,
    zones: flagZones(placedCells),
    worldWidth: rightLabelX + ROW_LABEL_WIDTH + SECTION_PAD,
    worldHeight: Math.max(bottom + SECTION_PAD, 1)
  };
}

export function flagZones(cells) {
  const groups = new Map();
  for (const cell of Array.isArray(cells) ? cells : []) {
    const section = String(cell?.section || "");
    const row = Number(cell?.columnNumber);
    if (!section || !Number.isFinite(row) || row <= 0) continue;
    if (isServiceFlagSeat(cell)) continue;
    const list = groups.get(section) ?? [];
    list.push(cell);
    groups.set(section, list);
  }

  const zones = [];
  for (const [section, seats] of groups) {
    seats.sort((left, right) => left.columnNumber - right.columnNumber || left.flagGridId - right.flagGridId);
    let bucket = [];
    const flush = () => {
      if (bucket.length === 0) return;
      if (bucket.every((seat) => seat.status === "Reserved")) {
        bucket = [];
        return;
      }
      const rows = bucket.map((seat) => seat.columnNumber);
      const min = Math.min(...rows);
      const max = Math.max(...rows);
      const box = boundsOf(bucket);
      zones.push({
        section,
        min,
        max,
        label: min === max ? `${section} ${min}` : `${section} ${min}\u2013${max}`,
        occupiedCount: bucket.filter((seat) => seat.status === "Occupied").length,
        flagCount: bucket.length,
        x: box.x,
        y: box.y,
        width: box.width,
        height: box.height
      });
      bucket = [];
    };
    for (const seat of seats) {
      const previous = bucket[bucket.length - 1];
      if (previous) {
        const aisle = previous.columnNumber <= FIELD_AISLE_AFTER_ROW && seat.columnNumber > FIELD_AISLE_AFTER_ROW;
        const reservedBreak = previous.status === "Reserved" && seat.status !== "Reserved";
        if (aisle || reservedBreak) flush();
      }
      bucket.push(seat);
    }
    flush();
  }

  zones.sort((left, right) => left.x - right.x || left.y - right.y || left.label.localeCompare(right.label));
  return zones;
}

export function spotTextThatFits(text, width, fontSize, pad = 3) {
  const label = String(text || "").replace(/\s+/g, " ").trim();
  const size = Number(fontSize);
  if (!label || !Number.isFinite(size) || size < 6) return "";
  const inner = Number(width) - pad * 2;
  if (!(inner > 0)) return "";
  const maxChars = Math.floor(inner / (size * CHAR_EM));
  if (maxChars < 1) return "";
  if (label.length <= maxChars) return label;
  if (maxChars < 2) return "";
  return `${label.slice(0, maxChars - 1).trimEnd()}\u2026`;
}

export function spotLinesThatFit(text, width, fontSize, maxLines = 2) {
  const label = String(text || "").replace(/\s+/g, " ").trim();
  const size = Number(fontSize);
  const lines = Math.floor(Number(maxLines));
  if (!label || !(size > 0) || !(lines >= 1)) return [];
  const maxChars = Math.floor(Number(width) / (size * CHAR_EM));
  if (maxChars < 3) return [];
  const pieces = [];
  for (const word of label.split(" ")) {
    const parts = word.split(/(?<=-)/);
    parts.forEach((part, index) => pieces.push({ text: part, space: index === 0 && pieces.length > 0 }));
  }
  const join = (line, piece) => (piece.space ? `${line} ${piece.text}` : `${line}${piece.text}`);
  const out = [];
  let index = 0;
  while (index < pieces.length && out.length < lines) {
    let line = pieces[index].text;
    index += 1;
    while (index < pieces.length && join(line, pieces[index]).length <= maxChars) {
      line = join(line, pieces[index]);
      index += 1;
    }
    out.push(line);
  }
  while (index < pieces.length) {
    out[out.length - 1] = join(out[out.length - 1], pieces[index]);
    index += 1;
  }
  return out.map((line) => (line.length <= maxChars ? line : `${line.slice(0, maxChars - 1).trimEnd()}\u2026`));
}

function truncated(lines) {
  return lines.some((line) => line.endsWith("\u2026"));
}

export const SPOT_TEXT_PX = Object.freeze({
  number: { min: 9, max: 13 },
  name: { min: 10, max: 15 },
  closestName: { min: 12, max: 20 },
  rank: { min: 10, max: 13 },
  seal: { min: 18, max: 88 }
});

// Everything is measured in screen pixels and returned in field units, so the
// text keeps one readable size on screen at any zoom and never leaves its spot.
export function spotContents(cell, gates = {}, scale = 1) {
  const s = Number(scale) > 0 ? Number(scale) : 1;
  const width = (Number(cell?.width) || 0) * s;
  const height = (Number(cell?.height) || 0) * s;
  const status = cell?.status || "Open";
  const occupied = status === "Occupied";
  const out = { seal: 0, number: "", numberSize: 0, lines: [], nameSize: 0, rank: "", rankSize: 0, pad: 0, status };
  if (!gates.numbers && !gates.names) return out;
  const pad = clamp(Math.min(width, height) * 0.08, 2, 12);
  const innerW = width - pad * 2;
  const innerH = height - pad * 2;
  out.pad = pad / s;
  if (status === "Reserved") return out;
  if (innerW < 12 || innerH < SPOT_TEXT_PX.number.min * LINE_HEIGHT) return out;
  const number = String(cell?.flagGridName || "").trim();

  if (!gates.names) {
    const size = Math.min(SPOT_TEXT_PX.number.max, innerH / LINE_HEIGHT, innerW / (Math.max(number.length, 1) * NUMBER_EM));
    if (number && size >= SPOT_TEXT_PX.number.min) {
      out.number = number;
      out.numberSize = size / s;
    }
    return out;
  }

  let room = innerH;
  const closest = Boolean(gates.seals);
  if (closest && occupied && cell?.logoUrl) {
    const seal = Math.floor(Math.min(innerH * 0.38, innerW * 0.4, SPOT_TEXT_PX.seal.max));
    if (seal >= SPOT_TEXT_PX.seal.min) {
      out.seal = seal / s;
      room -= seal * 1.08;
    }
  }

  const numberSize = clamp(innerH * 0.12, SPOT_TEXT_PX.number.min, closest ? 12 : 11);
  const nameRange = closest ? SPOT_TEXT_PX.closestName : SPOT_TEXT_PX.name;
  const caption = occupied ? (cell?.displayName || "") : "Open";
  if (number && room >= (numberSize + nameRange.min) * LINE_HEIGHT && number.length * numberSize * NUMBER_EM <= innerW) {
    out.number = number;
    out.numberSize = numberSize / s;
    room -= numberSize * LINE_HEIGHT;
  }

  const startSize = Math.min(nameRange.max, Math.max(nameRange.min, innerH * (closest ? 0.13 : 0.24)));
  let nameSize = nameRange.min;
  let lines = [];
  for (let size = startSize; size >= nameRange.min - 0.001; size -= 1) {
    const maxLines = Math.min(2, Math.floor(room / (size * LINE_HEIGHT)));
    if (maxLines < 1) continue;
    const attempt = spotLinesThatFit(caption, innerW, size, maxLines);
    if (!attempt.length) continue;
    lines = attempt;
    nameSize = size;
    if (!truncated(attempt)) break;
  }
  if (truncated(lines)) {
    const maxLines = Math.min(2, Math.floor(room / (nameRange.min * LINE_HEIGHT)));
    const smallest = maxLines >= 1 ? spotLinesThatFit(caption, innerW, nameRange.min, maxLines) : [];
    if (smallest.length) {
      lines = smallest;
      nameSize = nameRange.min;
    }
  }
  if (lines.length) {
    out.lines = lines;
    out.nameSize = nameSize / s;
    room -= lines.length * nameSize * LINE_HEIGHT;
  }

  if (closest && occupied && cell?.rank) {
    const rankSize = Math.min(SPOT_TEXT_PX.rank.max, Math.max(SPOT_TEXT_PX.rank.min, innerH * 0.09), room / LINE_HEIGHT);
    const rank = rankSize >= SPOT_TEXT_PX.rank.min ? spotTextThatFits(cell.rank, innerW, rankSize, 0) : "";
    if (rank) {
      out.rank = rank;
      out.rankSize = rankSize / s;
    }
  }
  return out;
}

export function labelFontSize(text, boxWidth, boxHeight, scale, options = {}) {
  const s = Number(scale) > 0 ? Number(scale) : 1;
  const min = options.min ?? 5;
  const max = options.max ?? 13;
  const em = options.em ?? CHAR_EM;
  const length = Math.max(1, String(text || "").length);
  const px = Math.min(max, boxHeight * s * 0.72, (boxWidth * s - 2) / (length * em));
  return px >= min ? px / s : 0;
}

// Bold capitals and digits are measured wide so a narrow block never spills a
// glyph past its edge in any of the fonts in the stack.
const GLYPH_EM = Object.freeze({ W: 1.04, M: 0.94, I: 0.32, J: 0.62 });
const CAPITAL_EM = 0.78;
const DIGIT_EM = 0.7;
const UPRIGHT_ADVANCE = 1.25;
const UPRIGHT_LINE = 0.8;
export const NARROW_BLOCK_PX = 10;
export const BLOCK_EDGE_PX = Object.freeze({ normal: 1.5, narrow: 0.5 });
export const ZONE_TEXT_PX = Object.freeze({ letter: 5, stackedRange: 6, stackedRangeMax: 9 });
export const SERVICE_FLAGS_SHORT_LABEL = "Service";

function glyphEm(char) {
  return GLYPH_EM[char] ?? (/[0-9\u2013-]/.test(char) ? DIGIT_EM : CAPITAL_EM);
}

export function blockEdgePx(widthPx) {
  return widthPx < NARROW_BLOCK_PX ? BLOCK_EDGE_PX.narrow : BLOCK_EDGE_PX.normal;
}

function blockInnerPx(widthPx) {
  return Math.max(0, widthPx - blockEdgePx(widthPx) * 2 - 0.2);
}

// A turned line (the Service Flags label) across a block: narrow blocks set it at
// line-height 1 inside their 0.5px stroke.
function turnedLinePx(widthPx) {
  return widthPx < NARROW_BLOCK_PX ? Math.max(0, widthPx - 1) : blockInnerPx(widthPx);
}

// Section labels share one layout per zoom: the same letter size, range size, weight,
// and top offset in every block, sized so the widest name fits the narrowest column.
export const SECTION_LABEL_PX = Object.freeze({ letterMax: 22, acrossLetterMin: 7, rangeMax: 13, uprightRangeMax: 9, rangeToLetter: 0.8 });

function zoneRange(zone) {
  return zone.min === zone.max ? String(zone.min) : `${zone.min}\u2013${zone.max}`;
}

function textEm(text) {
  return [...String(text || "")].reduce((sum, char) => sum + glyphEm(char), 0);
}

export function sectionLabelSizes(zones, scale) {
  const s = Number(scale) > 0 ? Number(scale) : 1;
  const list = (zones ?? []).filter((zone) => zone && zone.width > 0 && zone.height > 0);
  if (!list.length) return null;
  const widthPx = Math.min(...list.map((zone) => zone.width)) * s;
  const narrow = widthPx < NARROW_BLOCK_PX;
  const inner = blockInnerPx(widthPx);
  const ranges = list.map(zoneRange);
  const widestName = Math.max(...list.map((zone) => textEm(zone.section)), CAPITAL_EM);
  const widestGlyph = Math.max(UPRIGHT_LINE, CAPITAL_EM, ...list.flatMap((zone) => [...String(zone.section || "")].map(glyphEm)));
  const widestRange = Math.max(...ranges.map(textEm));
  const widestEnd = Math.max(...list.map((zone) => Math.max(textEm(String(zone.min)), textEm(String(zone.max)))));
  const longestRange = Math.max(...ranges.map((range) => [...range].length));
  const top = (letterPx) => Math.max(blockEdgePx(widthPx) + 1, letterPx * 0.35);
  const longestName = Math.max(...list.map((zone) => [...String(zone.section || "")].length), 1);
  const need = (zone, layout, letterPx, rangePx) => {
    // Upright letters reserve the longest name's lines in every block so the ranges line up.
    const chars = [...String(zone.section || "")].length;
    const letters = layout.letters === "across" ? letterPx : Math.max(chars, longestName) * UPRIGHT_ADVANCE * letterPx;
    const rangeChars = [...zoneRange(zone)].length;
    const range = !rangePx ? 0
      : layout.range === "across" ? rangePx * LINE_HEIGHT
        : layout.range === "lines" ? 3 * rangePx * LINE_HEIGHT
          : rangeChars * rangePx * UPRIGHT_ADVANCE;
    return top(letterPx) + letters + (rangePx ? letterPx * 0.3 : 0) + range;
  };
  const fits = (layout, letterPx, rangePx) => list.every((zone) => need(zone, layout, letterPx, rangePx) <= zone.height * s * 0.96);

  let layout;
  let letterPx;
  let rangePx;
  const across = Math.min(SECTION_LABEL_PX.letterMax, inner / widestName);
  if (across >= SECTION_LABEL_PX.acrossLetterMin) {
    letterPx = across;
    // Never below the range floor while the letter is across: the column has room for it.
    const target = Math.min(SECTION_LABEL_PX.rangeMax, Math.max(ZONE_TEXT_PX.stackedRange, letterPx * SECTION_LABEL_PX.rangeToLetter));
    layout = target * widestRange <= inner ? { letters: "across", range: "across" } : { letters: "across", range: "lines" };
    rangePx = layout.range === "across" ? target : Math.min(target, inner / widestEnd);
  } else {
    // Narrow columns (the 320 fallback): one glyph per line for the letter and the range.
    layout = { letters: "upright", range: "upright" };
    letterPx = Math.min(SECTION_LABEL_PX.letterMax, inner / widestGlyph);
    // Capped at the turned Service Flags line so that label can share the size.
    rangePx = Math.min(SECTION_LABEL_PX.uprightRangeMax, inner / UPRIGHT_LINE, turnedLinePx(widthPx));
  }
  while (rangePx - 0.1 >= ZONE_TEXT_PX.stackedRange && !fits(layout, letterPx, rangePx)) rangePx -= 0.1;
  while (letterPx - 0.1 >= ZONE_TEXT_PX.letter && rangePx >= ZONE_TEXT_PX.stackedRange && !fits(layout, letterPx, rangePx)) letterPx -= 0.1;
  if (rangePx < ZONE_TEXT_PX.stackedRange || !fits(layout, letterPx, rangePx)) rangePx = 0;
  let tiny = false;
  if (!rangePx) {
    // Letter only, still one size for every block.
    layout = { ...layout, range: "none" };
    const tall = Math.min(...list.map((zone) => (zone.height * s * 0.8) / (UPRIGHT_ADVANCE * (layout.letters === "across" ? 1 : [...String(zone.section || "")].length))));
    letterPx = Math.min(letterPx || SECTION_LABEL_PX.letterMax, tall);
    if (letterPx < ZONE_TEXT_PX.letter) {
      // Below the floor the letter still draws, edge to edge over an inset stroke (.isTiny).
      layout = { letters: "upright", range: "none" };
      const full = Math.max(0, widthPx - 0.2);
      letterPx = Math.min(SECTION_LABEL_PX.letterMax, full / widestGlyph, ...list.map((zone) => (zone.height * s * 0.8) / (UPRIGHT_ADVANCE * Math.max(1, [...String(zone.section || "")].length))));
      tiny = true;
    }
  }
  return {
    scale: s,
    narrow,
    tiny,
    letters: layout.letters,
    letterLines: layout.letters === "across" ? 1 : longestName,
    rangeLayout: layout.range,
    letterPx: Math.max(0, letterPx),
    rangePx: Math.max(0, rangePx),
    topPx: top(letterPx)
  };
}

export function zoneLabelSizes(zone, scale, uniform = sectionLabelSizes([zone], scale)) {
  const s = uniform?.scale ?? (Number(scale) > 0 ? Number(scale) : 1);
  const range = zoneRange(zone);
  const chars = [...String(zone.section || "")];
  const letter = uniform && uniform.letterPx > 0 ? uniform.letterPx / s : 0;
  const rangeSize = uniform && uniform.rangePx > 0 ? uniform.rangePx / s : 0;
  return {
    range,
    rangeParts: uniform?.rangeLayout === "lines" ? [String(zone.min), "\u2013", String(zone.max)].filter((part, index) => zone.min !== zone.max || index === 0) : null,
    letter,
    letterStacked: uniform?.letters === "upright" && chars.length > 1,
    letterBlock: uniform?.letters === "upright" && uniform.rangePx > 0 ? (uniform.letterLines * UPRIGHT_ADVANCE * uniform.letterPx) / s : 0,
    narrow: Boolean(uniform?.narrow),
    tiny: Boolean(uniform?.tiny),
    rangeSize,
    vertical: false,
    stacked: uniform?.rangeLayout === "upright" && rangeSize > 0,
    top: uniform ? uniform.topPx / s : 0
  };
}

export function serviceFlagsLabelBox(box, seen) {
  if (!box) return null;
  const visible = seen ?? box;
  const left = Math.max(box.x, visible.x);
  const top = Math.max(box.y, visible.y);
  const right = Math.min(box.x + box.width, visible.x + visible.width);
  const bottom = Math.min(box.y + box.height, visible.y + visible.height);
  if (!(right > left && bottom > top)) return null;
  return { x: left - box.x, y: top - box.y, width: right - left, height: bottom - top, label: box.label };
}

// The Service Flags label uses the section range size (the letter size when ranges are
// off), so it reads as one of the section labels.
export function serviceFlagsLabelSize(box, scale, uniform) {
  const s = Number(scale) > 0 ? Number(scale) : 1;
  const words = String(box?.label || SERVICE_FLAGS_LABEL).split(" ");
  const label = words.join(" ");
  const width = (Number(box?.width) || 0) * s;
  const height = (Number(box?.height) || 0) * s;
  const narrow = width < NARROW_BLOCK_PX;
  const shared = uniform ?? sectionLabelSizes([{ section: SERVICE_FLAGS_SECTION, min: 1, max: FIELD_MAP_Z_ROWS, width: Number(box?.width) || 0, height: Number(box?.height) || 0 }], s);
  const empty = { size: 0, vertical: true, text: "", narrow, top: 0 };
  if (!shared || !(width > 0 && height > 0)) return empty;
  const base = Math.min(shared.rangePx > 0 ? shared.rangePx : shared.letterPx, 20);
  // Section blocks draw a border (none when tiny) and this block an inset shadow, so add it to line up with the letters.
  const top = shared.topPx + (shared.tiny ? 0 : blockEdgePx(width));
  const room = Math.min((height - top) * 0.9, height * 0.84);
  const inner = blockInnerPx(width);
  const longest = Math.max(...words.map((word) => word.length));
  if (base > 0 && longest * base * CHAR_EM <= inner && words.length * base * LINE_HEIGHT <= room) {
    return { size: base / s, vertical: false, text: label, narrow, top: top / s };
  }
  const edge = shared.tiny ? Math.max(0, width - 0.2) : turnedLinePx(width);
  for (const text of [label, SERVICE_FLAGS_SHORT_LABEL]) {
    const size = Math.min(base, edge);
    if (size > 0 && text.length * size * CHAR_EM <= room) return { size: size / s, vertical: true, text, narrow, top: top / s };
  }
  const size = Math.min(base, edge, room / (SERVICE_FLAGS_SHORT_LABEL.length * CHAR_EM));
  return size > 0 ? { size: size / s, vertical: true, text: SERVICE_FLAGS_SHORT_LABEL, narrow, top: top / s } : empty;
}

// The public page opens in the app's full-screen view (header and footer hidden);
// kiosk mode keeps its own browser fullscreen and lockdown.
export function initialMapFullScreen(kiosk) {
  return !kiosk;
}

// Each Esc closes only the topmost open thing; full screen exits last.
export function flagMapEscapeTarget({ cardOpen, listOpen, mapFull }) {
  if (cardOpen) return "card";
  if (listOpen) return "list";
  if (mapFull) return "fullscreen";
  return null;
}

export function mapFullscreenSupported(element) {
  if (!element) return false;
  return typeof element.requestFullscreen === "function" || typeof element.webkitRequestFullscreen === "function";
}

export function requestElementFullscreen(element) {
  if (!element) return Promise.resolve(false);
  const doc = element.ownerDocument;
  if (doc && (doc.fullscreenElement || doc.webkitFullscreenElement)) return Promise.resolve(true);
  const request = element.requestFullscreen?.bind(element) || element.webkitRequestFullscreen?.bind(element);
  if (!request) return Promise.resolve(false);
  try {
    const pending = request();
    if (pending && typeof pending.then === "function") return pending.then(() => true).catch(() => false);
    return Promise.resolve(true);
  } catch {
    return Promise.resolve(false);
  }
}

export function exitDocumentFullscreen(doc) {
  if (!doc) return Promise.resolve(false);
  const active = doc.fullscreenElement || doc.webkitFullscreenElement;
  if (!active) return Promise.resolve(false);
  const exit = doc.exitFullscreen?.bind(doc) || doc.webkitExitFullscreen?.bind(doc);
  if (!exit) return Promise.resolve(false);
  try {
    const pending = exit();
    if (pending && typeof pending.then === "function") return pending.then(() => true).catch(() => false);
    return Promise.resolve(true);
  } catch {
    return Promise.resolve(false);
  }
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
    || previousGates.numbers !== nextGates.numbers
    || previousGates.names !== nextGates.names
    || previousGates.ranks !== nextGates.ranks
    || previousGates.sectionsInteractive !== nextGates.sectionsInteractive
    || previousGates.flagsInteractive !== nextGates.flagsInteractive;
}
