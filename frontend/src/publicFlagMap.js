export function publicFlagMapUrls(apiBase = "") {
  const base = String(apiBase || "").replace(/\/$/, "");
  return {
    sections: `${base}/api/public/flag-map/sections`,
    section: (section) => `${base}/api/public/flag-map/sections/${encodeURIComponent(section)}`,
    searchIndex: `${base}/api/public/flag-map/search-index`
  };
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
