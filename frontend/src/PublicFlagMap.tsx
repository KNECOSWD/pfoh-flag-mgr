import { useEffect, useId, useLayoutEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { qrModules, qrPath } from "./qrCode.js";
import {
  FLAG_MAP_ZOOM,
  KIOSK_IDLE_MS,
  MAP_FULLSCREEN_CLASS,
  initialMapFullScreen,
  SUBMITTER_SUGGESTION_DEBOUNCE_MS,
  WHEEL_SETTLE_MS,
  applyKioskIdleReset,
  attractView,
  clampView,
  closestViewScale,
  createPinchBatch,
  createWheelBatch,
  boundsOf,
  countFlagsOnScreen,
  createFlagMapLoader,
  cullRects,
  exitDocumentFullscreen,
  filterFlagMapSuggestions,
  FIT_PADDING,
  fitView,
  fitWholeField,
  flagMapPageBands,
  flagDeepLink,
  flagMapDetailGates,
  flagMapItemsInView,
  flagMapItemsSummary,
  flagMapLayerOpacities,
  lookupSubmitterSuggestions,
  resolveSubmitterFlags,
  flagYears,
  gestureTransform,
  labelFontSize,
  FIELD_MAP_Z_ROWS,
  layoutFlagField,
  panView,
  pinchView,
  pointerDragCapture,
  pointerShouldOpen,
  publicFlagMapUrls,
  readFlagMapLocation,
  requestElementFullscreen,
  SERVICE_FLAGS_SECTION,
  serviceFlagsLabelBox,
  serviceFlagsLabelSize,
  snapEdgeSpots,
  spotFitMaxScale,
  spotContents,
  spotsViewScale,
  springEase,
  viewportWorldRect,
  wheelFrameShouldCull,
  wheelScaleFactor,
  withApiBase,
  zoneLabelSizes,
  sectionLabelSizes,
  zoomAt,
  flagMapEscapeTarget,
  pinnedAxisLabels
} from "./publicFlagMap.js";
import { KIOSK_EXIT_HOLD_MS, kioskSessionOn, requestKioskExit } from "./kioskSession.js";

type SectionSummary = {
  section: string;
  flagCount: number;
  occupiedCount: number;
};

type PublicSeat = {
  flagGridId: number;
  flagGridName: string;
  section: string;
  status: string;
  honoreeId?: number | null;
  displayName?: string | null;
  rank?: string | null;
  serviceBranchName?: string | null;
  serviceBranchCategoryName?: string | null;
  logoUrl?: string | null;
  startYear?: number | null;
  endYear?: number | null;
  datesUserEntry?: string | null;
  kia?: boolean | null;
  description?: string | null;
  conflictsServed?: string | null;
  awards?: string | null;
  photoUrl?: string | null;
};

type SearchHit = {
  honoreeId: number;
  flagGridId: number;
  flagGridName: string;
  section: string;
  displayName: string;
  firstName?: string | null;
  lastName?: string | null;
  nickname?: string | null;
  rank?: string | null;
  serviceBranchName?: string | null;
};

type View = { x: number; y: number; scale: number };
type Box = { x: number; y: number; width: number; height: number; section: string; flagCount: number; occupiedCount: number };
type FlagBox = PublicSeat & Box;
type Gates = ReturnType<typeof flagMapDetailGates>;

const apiBase = import.meta.env.VITE_API_BASE_URL ?? "";
const urls = publicFlagMapUrls(apiBase);
const EMPTY_GATES: Gates = {
  level: "Sections",
  numbers: false,
  seals: false,
  names: false,
  ranks: false,
  sectionsInteractive: true,
  flagsInteractive: false
};

const loadJson = createFlagMapLoader() as <T>(url: string) => Promise<T>;

function sealUrl(logoUrl: string, edge: number, webp: boolean) {
  const url = withApiBase(apiBase, logoUrl);
  const join = url.includes("?") ? "&" : "?";
  return `${url}${join}w=${edge}${webp ? "&fmt=webp" : ""}`;
}

function sameIds(left: number[], right: number[]) {
  if (left.length !== right.length) return false;
  for (let index = 0; index < left.length; index += 1) {
    if (left[index] !== right[index]) return false;
  }
  return true;
}

function sameGates(left: Gates, right: Gates) {
  return left.seals === right.seals
    && left.numbers === right.numbers
    && left.names === right.names
    && left.ranks === right.ranks
    && left.sectionsInteractive === right.sectionsInteractive
    && left.flagsInteractive === right.flagsInteractive;
}

function requestKioskFullscreen() {
  const root = document.documentElement as HTMLElement & { webkitRequestFullscreen?: () => void };
  if (document.fullscreenElement) return;
  const request = root.requestFullscreen?.bind(root) ?? root.webkitRequestFullscreen?.bind(root);
  if (!request) return;
  try {
    const pending = request() as void | Promise<void>;
    if (pending && typeof (pending as Promise<void>).catch === "function") {
      (pending as Promise<void>).catch(() => {});
    }
  } catch {
    // The layout already fills the screen when fullscreen is declined.
  }
}

function FlagQr({ value }: { value: string }) {
  const drawn = useMemo(() => qrPath(qrModules(value)), [value]);
  return (
    <svg className="publicFlagQr" viewBox={`0 0 ${drawn.size} ${drawn.size}`} role="img" aria-label="QR code for this flag">
      <rect width={drawn.size} height={drawn.size} fill="#ffffff" />
      <path d={drawn.d} fill="#0b1f3a" />
    </svg>
  );
}

function FlagDetail({
  flag,
  revealed,
  shareUrl,
  onPreview,
  onClose,
  showSeal
}: {
  flag: FlagBox;
  revealed: boolean;
  shareUrl: string;
  onPreview: (honoreeId: number, title: string) => void;
  onClose: () => void;
  showSeal: boolean;
}) {
  const [showPhoto, setShowPhoto] = useState(Boolean(flag.photoUrl));
  useEffect(() => {
    setShowPhoto(Boolean(flag.photoUrl));
  }, [flag.flagGridId, flag.photoUrl]);
  const years = flagYears(flag);

  return (
    <aside className={revealed ? "publicFlagDetail isRevealed" : "publicFlagDetail"} aria-label="Flag detail">
      {showPhoto && flag.photoUrl ? (
        <div className="publicFlagPhotoFrame">
          <img
            className="publicFlagPhoto"
            src={withApiBase(apiBase, flag.photoUrl)}
            alt=""
            draggable={false}
            onError={() => setShowPhoto(false)}
          />
        </div>
      ) : null}
      <div className="publicFlagDetailBody">
        {flag.displayName ? <h3>{flag.displayName}</h3> : null}
        {flag.rank ? <p className="publicFlagRank">{flag.rank}</p> : null}
        {flag.serviceBranchName ? (
          <p className="publicFlagBranch">
            {showSeal && flag.logoUrl ? <img className="publicFlagSeal" src={sealUrl(flag.logoUrl, 256, false)} alt="" width={64} height={64} loading="lazy" decoding="async" draggable={false} /> : null}
            <span>
              {flag.serviceBranchName}
              {flag.serviceBranchCategoryName ? ` · ${flag.serviceBranchCategoryName}` : ""}
            </span>
          </p>
        ) : null}
        <p className="publicFlagSpot">{flag.flagGridName} · Section {flag.section}</p>
        {years ? <p>{years}</p> : null}
        {flag.kia ? <p>Killed in action</p> : null}
        {flag.description ? <p className="publicFlagBio">{flag.description}</p> : null}
        {flag.conflictsServed ? <p>Conflicts served: {flag.conflictsServed}</p> : null}
        {flag.awards ? <p>Awards: {flag.awards}</p> : null}
        <div className="publicFlagQrBlock">
          <FlagQr value={shareUrl} />
          <p>Scan to open this flag</p>
        </div>
        {flag.honoreeId ? (
          <button type="button" onClick={() => onPreview(flag.honoreeId!, flag.displayName || "Honoree")}>
            Preview card
          </button>
        ) : null}
        <button type="button" className="secondary" onClick={onClose}>Close</button>
      </div>
    </aside>
  );
}

const ICON_PATHS = {
  zoomOut: ["M5 12h14"],
  zoomIn: ["M12 5v14", "M5 12h14"],
  fullScreen: ["M4 9V4h5", "M15 4h5v5", "M20 15v5h-5", "M9 20H4v-5"],
  exitFullScreen: ["M9 4v5H4", "M20 9h-5V4", "M15 20v-5h5", "M4 15h5v5"],
  reset: ["M4.5 12a7.5 7.5 0 1 0 2.2-5.3", "M4 4.5v4h4"]
} as const;

function ToolIcon({ name }: { name: keyof typeof ICON_PATHS }) {
  return (
    <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true" focusable="false" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
      {ICON_PATHS[name].map((d) => <path key={d} d={d} />)}
    </svg>
  );
}

type InViewItem = ReturnType<typeof flagMapItemsInView>["items"][number];
type InViewList = { kind: string; items: InViewItem[] };

type SubmitterSuggestion = { label: string; token: string };
type SearchRow =
  | { kind: "honoree"; hit: SearchHit }
  | { kind: "submitter"; label: string; token: string };

export default function PublicFlagMap({
  onPreviewCard,
  kiosk: kioskLocked = false,
  onExitKiosk
}: {
  onPreviewCard: (honoreeId: number, title: string) => void;
  kiosk?: boolean;
  onExitKiosk?: () => void;
}) {
  const listId = useId();
  const helpId = useId();
  const jumpId = useId();
  const location = useMemo(() => readFlagMapLocation(window.location.search), []);
  const kiosk = kioskLocked || location.kiosk || kioskSessionOn(window.sessionStorage);
  const viewportRef = useRef<HTMLDivElement | null>(null);
  const layerRef = useRef<HTMLDivElement | null>(null);
  const axisRef = useRef<HTMLDivElement | null>(null);
  const viewRef = useRef<View>({ x: 0, y: 0, scale: 1 });
  const fitScaleRef = useRef(1);
  const userAdjustedRef = useRef(false);
  const animRef = useRef(0);
  const cullFrameRef = useRef(0);
  const attractFrameRef = useRef(0);
  const wheelFrameRef = useRef(0);
  const wheelSettleRef = useRef(0);
  const pinchFrameRef = useRef(0);
  const pinchSettleRef = useRef(0);
  const idleTimerRef = useRef(0);
  const exitHoldRef = useRef(0);
  const suppressClickRef = useRef(false);
  const attractingRef = useRef(false);
  const fullscreenTriedRef = useRef(false);
  // Opening in the app's view state, not the browser Fullscreen API, which needs a user gesture.
  const mapFullRef = useRef(initialMapFullScreen(kiosk));
  const cssFallbackRef = useRef(initialMapFullScreen(kiosk));
  const reducedMotionRef = useRef(false);
  const kioskRef = useRef(kiosk);
  const gatesRef = useRef<Gates>(EMPTY_GATES);
  const levelRef = useRef("Sections");
  const pendingFlagRef = useRef<number | null>(location.flagId);
  const historyRef = useRef<View[]>([]);
  const wheelBatchRef = useRef(createWheelBatch());
  const pinchBatchRef = useRef(createPinchBatch());
  const boxRef = useRef({ left: 0, top: 0, width: 1, height: 1 });
  const opacityCacheRef = useRef({ section: "" });
  const layoutRef = useRef<ReturnType<typeof layoutFlagField> | null>(null);
  const summariesRef = useRef<SectionSummary[]>([]);
  const fieldTaskRef = useRef<Promise<void> | null>(null);
  const [field, setField] = useState<ReturnType<typeof layoutFlagField> | null>(null);
  const [index, setIndex] = useState<SearchHit[]>([]);
  const [query, setQuery] = useState("");
  const [submitterNote, setSubmitterNote] = useState("");
  const [submitterSuggestions, setSubmitterSuggestions] = useState<SubmitterSuggestion[]>([]);
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const [error, setError] = useState(location.flagRejected ? "That flag is not on the map." : "");
  const [loading, setLoading] = useState(true);
  const [visibleZoneKeys, setVisibleZoneKeys] = useState<string[]>([]);
  const [mapFull, setMapFull] = useState(initialMapFullScreen(kiosk));
  const [visibleCellIds, setVisibleCellIds] = useState<number[]>([]);
  const [gates, setGates] = useState<Gates>(EMPTY_GATES);
  const [renderScale, setRenderScale] = useState(1);
  const [serviceLabelBox, setServiceLabelBox] = useState<ReturnType<typeof serviceFlagsLabelBox>>(null);
  const [levelText, setLevelText] = useState("Sections");
  const [inView, setInView] = useState<InViewList>({ kind: "sections", items: [] });
  const [jumpPending, setJumpPending] = useState("");
  const inViewTimerRef = useRef(0);
  const jumpByArrowRef = useRef(false);
  const toolbarRef = useRef<HTMLDivElement | null>(null);
  const [toolIndex, setToolIndex] = useState(0);
  const [selected, setSelected] = useState<FlagBox | null>(null);
  const [highlightId, setHighlightId] = useState<number | null>(null);
  const [revealId, setRevealId] = useState<number | null>(null);
  const [waveIds, setWaveIds] = useState<number[]>([]);
  const [attracting, setAttracting] = useState(false);
  const [pinOpen, setPinOpen] = useState(false);
  const [pinDigits, setPinDigits] = useState("");
  const [pinMessage, setPinMessage] = useState("");
  const [pinBusy, setPinBusy] = useState(false);
  const suggestions = filterFlagMapSuggestions(index, query) as SearchHit[];
  const searchRows: SearchRow[] = [
    ...suggestions.map((hit) => ({ kind: "honoree" as const, hit })),
    ...submitterSuggestions.map((item) => ({ kind: "submitter" as const, label: item.label, token: item.token }))
  ];
  const escapeStateRef = useRef({ cardOpen: false, listOpen: false });
  escapeStateRef.current = { cardOpen: Boolean(selected), listOpen: (open && searchRows.length > 0) || Boolean(jumpPending) };
  const activeRow = searchRows[active];
  const activeDescendant = !open || !activeRow
    ? undefined
    : activeRow.kind === "honoree"
      ? `${listId}-${activeRow.hit.honoreeId}`
      : `${listId}-submitter-${activeRow.token}`;
  const shareUrl = selected ? flagDeepLink(window.location.href, selected.flagGridId) : "";

  const sectionLabels = useMemo(() => (field ? sectionLabelSizes(field.zones, renderScale) : null), [field, renderScale]);
  const visibleZones = useMemo(() => {
    if (!field) return [];
    const keys = new Set(visibleZoneKeys);
    return field.zones.filter((zone) => keys.has(zone.label));
  }, [field, visibleZoneKeys]);

  const visibleCells = useMemo(() => {
    if (!field) return [];
    const ids = new Set(visibleCellIds);
    return field.cells.filter((cell) => !cell.serviceFlag && ids.has(cell.flagGridId));
  }, [field, visibleCellIds]);

  function syncViewportBox() {
    const rect = viewportRef.current?.getBoundingClientRect();
    if (!rect) return boxRef.current;
    const box = { left: rect.left, top: rect.top, width: rect.width || 1, height: rect.height || 1 };
    boxRef.current = box;
    return box;
  }

  function viewportSize() {
    const box = boxRef.current.width > 1 ? boxRef.current : syncViewportBox();
    return { width: box.width || 1, height: box.height || 1 };
  }

  function localPoint(event: { clientX: number; clientY: number }) {
    const box = boxRef.current;
    return { x: event.clientX - box.left, y: event.clientY - box.top };
  }

  function worldBounds() {
    return { x: 0, y: 0, width: layoutRef.current?.worldWidth || 1, height: layoutRef.current?.worldHeight || 1 };
  }

  function visibleSize() {
    const node = viewportRef.current;
    return node && node.clientWidth > 0 ? { width: node.clientWidth, height: node.clientHeight } : viewportSize();
  }

  function maxViewScale() {
    return Math.max(fitScaleRef.current, spotFitMaxScale(visibleSize(), FIT_PADDING));
  }

  function limitView(next: View) {
    return clampView(next, viewportSize(), worldBounds(), {
      minScale: Math.min(fitScaleRef.current, FLAG_MAP_ZOOM.maxScale),
      maxScale: maxViewScale(),
      pad: FIT_PADDING
    });
  }

  function settledView(next: View) {
    const limited = limitView(next);
    const cells = layoutRef.current?.cells;
    if (!cells) return limited;
    const settled = snapEdgeSpots(limited, visibleSize(), cells, FIT_PADDING, {
      minScale: fitScaleRef.current,
      maxScale: maxViewScale(),
      world: worldBounds(),
      clampViewport: viewportSize()
    });
    return limitView(settled);
  }

  function settleGesture() {
    const now = viewRef.current;
    const target = settledView(now);
    if (Math.abs(target.x - now.x) >= 0.5 || Math.abs(target.y - now.y) >= 0.5) {
      animateTo(target);
      return;
    }
    publishCull(true);
    setMoving(false);
  }

  function paintView(next: View) {
    const scale = Math.min(FLAG_MAP_ZOOM.maxScale, Math.max(Math.min(FLAG_MAP_ZOOM.minScale, fitScaleRef.current), next.scale));
    const view = { x: next.x, y: next.y, scale };
    viewRef.current = view;
    const opacity = flagMapLayerOpacities(view.scale);
    const layer = layerRef.current;
    if (layer) {
      layer.style.transform = gestureTransform(view);
      writeOpacity(layer, "--section-opacity", "section", opacity.section);
    }
    paintAxis(view);
    const viewport = viewportRef.current;
    if (viewport) {
      const sections = opacity.section >= 0.5 ? "on" : "off";
      if (viewport.dataset.sections !== sections) viewport.dataset.sections = sections;
    }
    return { view, opacity };
  }

  function paintAxis(view: View) {
    const axis = axisRef.current;
    const field = layoutRef.current;
    if (!axis || !field) return;
    const labels = pinnedAxisLabels(field, view, viewportSize());
    labels.forEach((label, index) => {
      const node = axis.children[index] as HTMLElement | undefined;
      if (!node) return;
      node.style.transform = `translate(${label.x}px, ${label.y}px)`;
      node.style.width = `${label.width}px`;
      node.style.height = `${label.height}px`;
      node.style.fontSize = `${label.fontSize}px`;
      node.style.visibility = label.hidden ? "hidden" : "";
    });
  }

  function writeOpacity(layer: HTMLDivElement, property: string, key: "section", value: number) {
    const next = value < 0.004 ? "0" : value > 0.996 ? "1" : value.toFixed(3);
    if (opacityCacheRef.current[key] === next) return;
    opacityCacheRef.current[key] = next;
    layer.style.setProperty(property, next);
  }

  function publishCull(immediate = false) {
    const run = () => {
      cullFrameRef.current = 0;
      const view = viewRef.current;
      const size = viewportSize();
      const seen = viewportWorldRect(view, size, 180);
      const layout = layoutRef.current;
      if (!layout) return;
      const flagsOnScreen = countFlagsOnScreen(layout.flags, view, size);
      const nextGates = flagMapDetailGates(view.scale, flagsOnScreen);
      gatesRef.current = nextGates;
      const zoneKeys = cullRects(layout.zones, seen).map((zone: { label: string }) => zone.label);
      const cellIds = cullRects(layout.cells, seen).map((cell: { flagGridId: number }) => cell.flagGridId);
      setGates((current) => (sameGates(current, nextGates) ? current : nextGates));
      setRenderScale((current) => (Math.abs(current - view.scale) <= view.scale * 0.004 ? current : view.scale));
      layerRef.current?.style.setProperty("--px", `${1 / view.scale}px`);
      setVisibleZoneKeys((current) => (current.join("|") === zoneKeys.join("|") ? current : zoneKeys));
      setVisibleCellIds((current) => (sameIds(current, cellIds) ? current : cellIds));
      const serviceLabel = serviceFlagsLabelBox(layout.serviceFlags, viewportWorldRect(view, size, 0));
      const slop = 4 / view.scale;
      setServiceLabelBox((current) => {
        if (!current || !serviceLabel) return serviceLabel;
        const same = (["x", "y", "width", "height"] as const).every((key) => Math.abs(current[key] - serviceLabel[key]) <= slop);
        return same ? current : serviceLabel;
      });
      const viewport = viewportRef.current;
      if (viewport) {
        const flags = nextGates.flagsInteractive ? "on" : "off";
        if (viewport.dataset.flags !== flags) viewport.dataset.flags = flags;
      }
      paintView(view);
      const nextLevel = nextGates.level;
      if (nextLevel !== levelRef.current) {
        levelRef.current = nextLevel;
        setLevelText(nextLevel);
      }
      scheduleInView();
    };
    if (immediate) {
      cancelAnimationFrame(cullFrameRef.current);
      run();
      return;
    }
    if (cullFrameRef.current) return;
    cullFrameRef.current = requestAnimationFrame(run);
  }

  // The list trails the view and waits for motion to stop, so a drag or an animated
  // zoom (which can swing far out on the way) never lists a passing view.
  function scheduleInView(waited = 0) {
    window.clearTimeout(inViewTimerRef.current);
    inViewTimerRef.current = window.setTimeout(() => {
      const layout = layoutRef.current;
      if (!layout) return;
      if (waited < 12 && layerRef.current?.classList.contains("isMoving")) {
        scheduleInView(waited + 1);
        return;
      }
      const next = flagMapItemsInView(layout, viewRef.current, visibleSize(), gatesRef.current) as InViewList;
      setInView((current) => (current.kind === next.kind && current.items.map((item) => item.key).join("|") === next.items.map((item) => item.key).join("|") ? current : next));
    }, 120);
  }

  function setMoving(moving: boolean) {
    const layer = layerRef.current;
    if (!layer) return;
    if (moving) {
      if (!layer.classList.contains("isMoving")) layer.classList.add("isMoving");
    } else {
      layer.classList.remove("isMoving");
    }
  }

  function applyView(next: View, cullNow = false) {
    paintView(limitView(next));
    publishCull(cullNow);
  }

  function stopMotion() {
    cancelAnimationFrame(animRef.current);
    cancelAnimationFrame(attractFrameRef.current);
    cancelAnimationFrame(wheelFrameRef.current);
    wheelFrameRef.current = 0;
    window.clearTimeout(wheelSettleRef.current);
    wheelBatchRef.current.flush();
    cancelAnimationFrame(pinchFrameRef.current);
    pinchFrameRef.current = 0;
    window.clearTimeout(pinchSettleRef.current);
    pinchBatchRef.current.flush();
    setMoving(false);
  }

  function animateTo(requested: View, onDone?: () => void) {
    stopMotion();
    const target = limitView(requested);
    const from = { ...viewRef.current };
    const unchanged = Math.abs(from.x - target.x) < 0.5
      && Math.abs(from.y - target.y) < 0.5
      && Math.abs(from.scale - target.scale) < 0.0001;
    if (reducedMotionRef.current || unchanged) {
      applyView(target, true);
      setMoving(false);
      onDone?.();
      return;
    }
    setMoving(true);
    const started = performance.now();
    let lastGates = flagMapDetailGates(from.scale);
    const step = (now: number) => {
      const amount = Math.min(1, (now - started) / 680);
      const eased = springEase(amount);
      const next = {
        x: from.x + (target.x - from.x) * eased,
        y: from.y + (target.y - from.y) * eased,
        scale: from.scale + (target.scale - from.scale) * eased
      };
      paintView(next);
      const nextGates = flagMapDetailGates(next.scale);
      const done = amount >= 1;
      if (wheelFrameShouldCull(lastGates, nextGates, done)) {
        lastGates = nextGates;
        publishCull(true);
      }
      if (!done) {
        animRef.current = requestAnimationFrame(step);
      } else {
        if (!attractingRef.current) setMoving(false);
        onDone?.();
      }
    };
    animRef.current = requestAnimationFrame(step);
  }

  function fitWorld() {
    const fitted = fitWholeField(worldBounds(), viewportSize(), FIT_PADDING);
    fitScaleRef.current = fitted.scale;
    return fitted;
  }

  function writeFlagParam(flagId: number | null) {
    const url = new URL(window.location.href);
    if (flagId) url.searchParams.set("flag", String(flagId));
    else url.searchParams.delete("flag");
    const next = `${url.pathname}${url.search}${url.hash}`;
    const current = `${window.location.pathname}${window.location.search}${window.location.hash}`;
    if (next !== current) window.history.replaceState(null, "", next);
  }

  function zoomBy(factor: number, origin?: { x: number; y: number }) {
    userAdjustedRef.current = true;
    const size = viewportSize();
    const point = origin ?? { x: size.width / 2, y: size.height / 2 };
    animateTo(settledView(zoomAt(viewRef.current, point.x, point.y, viewRef.current.scale * factor)));
  }

  function openFlag(flag: FlagBox, options?: { celebrate?: boolean }) {
    setError("");
    historyRef.current = [...historyRef.current, { ...viewRef.current }].slice(-16);
    setHighlightId(flag.flagGridId);
    setSelected(flag.honoreeId ? flag : null);
    setWaveIds([]);
    setRevealId(options?.celebrate && flag.honoreeId ? flag.flagGridId : null);
    writeFlagParam(flag.flagGridId);
    const size = viewportSize();
    const scale = closestViewScale(size);
    const centerX = flag.x + flag.width / 2;
    const centerY = flag.y + flag.height / 2;
    const panelBias = flag.honoreeId ? drawerCover(size) / 2 : 0;
    userAdjustedRef.current = true;
    animateTo({
      scale,
      x: size.width / 2 - panelBias - centerX * scale,
      y: size.height / 2 - centerY * scale
    });
  }

  function drawerCover(size: { width: number }) {
    const windowWidth = window.innerWidth;
    if (windowWidth <= 760) return 0;
    const drawer = kioskRef.current ? Math.min(460, windowWidth - 32) + 16 : Math.min(420, windowWidth - 24) + 12;
    const viewportRight = boxRef.current.left + size.width;
    return Math.max(0, Math.min(size.width, viewportRight - (windowWidth - drawer)));
  }

  function clearPick() {
    historyRef.current = [...historyRef.current, { ...viewRef.current }].slice(-16);
    userAdjustedRef.current = true;
    setSelected(null);
    setHighlightId(null);
    setRevealId(null);
    setWaveIds([]);
    writeFlagParam(null);
  }

  function zoomToZone(zone: { x: number; y: number; width: number; height: number }) {
    clearPick();
    const zoneView = fitView(zone, viewportSize(), 28);
    animateTo(settledView(zoneView.scale >= spotsViewScale() ? zoneView : zoomAt(zoneView, viewportSize().width / 2, viewportSize().height / 2, spotsViewScale())));
  }

  function zoomToSpot(cell: { flagGridId: number; x: number; y: number; width: number; height: number }) {
    clearPick();
    setHighlightId(cell.flagGridId);
    const size = viewportSize();
    const scale = Math.min(closestViewScale(size), maxViewScale());
    animateTo(settledView({
      scale,
      x: size.width / 2 - (cell.x + cell.width / 2) * scale,
      y: size.height / 2 - (cell.y + cell.height / 2) * scale
    }));
  }

  function pickInView(key: string) {
    setJumpPending("");
    const item = inView.items.find((entry) => entry.key === key);
    if (!item) return;
    exitAttract();
    if (item.kind === "spot") zoomToSpot(item.box as FlagBox);
    else zoomToZone(item.box);
  }

  // A closed select changes its value on an arrow key in some browsers. Those
  // steps wait for Enter so the map only moves on a deliberate choice.
  function onJumpKeyDown(event: React.KeyboardEvent<HTMLSelectElement>) {
    if (["ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", "PageUp", "PageDown", "Home", "End"].includes(event.key)) {
      jumpByArrowRef.current = true;
      return;
    }
    jumpByArrowRef.current = false;
    if (event.key === "Enter" && jumpPending) {
      event.preventDefault();
      pickInView(jumpPending);
    } else if (event.key === "Escape" && jumpPending) {
      setJumpPending("");
    }
  }

  function onJumpChange(event: React.ChangeEvent<HTMLSelectElement>) {
    const key = event.target.value;
    if (!key) {
      setJumpPending("");
      return;
    }
    if (jumpByArrowRef.current) {
      setJumpPending(key);
      return;
    }
    pickInView(key);
  }

  function onToolbarKeyDown(event: React.KeyboardEvent<HTMLDivElement>) {
    const buttons = [...(toolbarRef.current?.querySelectorAll<HTMLButtonElement>("button") ?? [])];
    const current = buttons.indexOf(document.activeElement as HTMLButtonElement);
    if (current < 0 || buttons.length === 0) return;
    const next = event.key === "ArrowRight" ? (current + 1) % buttons.length
      : event.key === "ArrowLeft" ? (current - 1 + buttons.length) % buttons.length
        : event.key === "Home" ? 0
          : event.key === "End" ? buttons.length - 1
            : -1;
    if (next < 0) return;
    event.preventDefault();
    event.stopPropagation();
    setToolIndex(next);
    buttons[next].focus();
  }

  function goHome() {
    exitAttract();
    setError((current) => (current === "That flag is not on the map." ? "" : current));
    historyRef.current = [];
    setSelected(null);
    setHighlightId(null);
    setRevealId(null);
    setWaveIds([]);
    setQuery("");
    setOpen(false);
    userAdjustedRef.current = false;
    writeFlagParam(null);
    syncViewportBox();
    animateTo(fitWorld());
  }

  function goBack() {
    exitAttract();
    setRevealId(null);
    setWaveIds([]);
    setSelected(null);
    setHighlightId(null);
    writeFlagParam(null);
    const previous = historyRef.current.pop();
    if (!previous) {
      goHome();
      return;
    }
    userAdjustedRef.current = true;
    animateTo(previous);
  }

  function beginAttract() {
    if (!kioskRef.current || attractingRef.current) return;
    attractingRef.current = true;
    syncViewportBox();
    const cleared = applyKioskIdleReset({ fitView: fitWorld() });
    setAttracting(true);
    setSelected(cleared.selected);
    setHighlightId(cleared.highlightId);
    setRevealId(cleared.revealId);
    setWaveIds([]);
    setQuery(cleared.query);
    setOpen(cleared.open);
    setActive(cleared.active);
    setSubmitterNote("");
    setSubmitterSuggestions([]);
    historyRef.current = cleared.history;
    writeFlagParam(null);
    userAdjustedRef.current = true;
    const fitted = cleared.view;
    animateTo(fitted, () => {
      if (!attractingRef.current) return;
      const started = performance.now();
      const loop = (now: number) => {
        if (!attractingRef.current) return;
        paintView(attractView(fitted, now - started, boxRef.current, reducedMotionRef.current));
        attractFrameRef.current = requestAnimationFrame(loop);
      };
      if (!reducedMotionRef.current) attractFrameRef.current = requestAnimationFrame(loop);
    });
  }

  function exitAttract() {
    if (!attractingRef.current) return;
    attractingRef.current = false;
    setAttracting(false);
    cancelAnimationFrame(attractFrameRef.current);
    cancelAnimationFrame(animRef.current);
    userAdjustedRef.current = false;
    syncViewportBox();
    const fitted = fitWorld();
    fitScaleRef.current = fitted.scale;
    applyView(fitted, true);
    setMoving(false);
  }

  function armIdle() {
    window.clearTimeout(idleTimerRef.current);
    if (!kioskRef.current || attractingRef.current) return;
    idleTimerRef.current = window.setTimeout(beginAttract, KIOSK_IDLE_MS);
  }

  const beginAttractRef = useRef(beginAttract);
  const exitAttractRef = useRef(exitAttract);
  const armIdleRef = useRef(armIdle);
  beginAttractRef.current = beginAttract;
  exitAttractRef.current = exitAttract;
  armIdleRef.current = armIdle;

  function rememberLayout() {
    const seatsBySection = new Map<string, PublicSeat[]>();
    for (const flag of (layoutRef.current?.flags ?? []) as FlagBox[]) {
      const seats = seatsBySection.get(flag.section) ?? [];
      seats.push(flag);
      seatsBySection.set(flag.section, seats);
    }
    const nextField = layoutFlagField(summariesRef.current.map((summary) => ({
      ...summary,
      seats: seatsBySection.get(summary.section) ?? []
    })));
    layoutRef.current = nextField;
    setField(nextField);
  }

  function loadField() {
    if (fieldTaskRef.current) return fieldTaskRef.current;
    const task = loadJson<{ sections: Array<SectionSummary & { seats: PublicSeat[] }> }>(urls.field)
      .then((payload) => {
        const nextField = layoutFlagField(payload.sections ?? []);
        layoutRef.current = nextField;
        setField(nextField);
      });
    fieldTaskRef.current = task;
    return task;
  }

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    Promise.all([
      loadJson<SectionSummary[]>(urls.sections),
      loadJson<SearchHit[]>(urls.searchIndex)
    ])
      .then(async ([summaries, nextIndex]) => {
        if (cancelled) return;
        summariesRef.current = summaries;
        setIndex(nextIndex);
        if (pendingFlagRef.current == null) {
          rememberLayout();
          void loadField();
        } else {
          await loadField();
        }
        if (!cancelled) setError("");
      })
      .catch((err: unknown) => {
        if (!cancelled) setError(err instanceof Error ? err.message : "The flag map could not be loaded.");
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  useLayoutEffect(() => {
    if (!field || !viewportRef.current) return;
    const node = viewportRef.current;
    const fit = () => {
      syncViewportBox();
      if (userAdjustedRef.current || attractingRef.current) return;
      applyView(fitWorld(), true);
    };
    fit();
    if (pendingFlagRef.current) {
      const id = pendingFlagRef.current;
      pendingFlagRef.current = null;
      const flags = layoutRef.current?.flags as FlagBox[] | undefined;
      const flag = flags?.find((item) => item.flagGridId === id) ?? flags?.find((item) => item.honoreeId === id);
      if (flag) openFlag(flag);
      else setError("That flag is not on the map.");
    } else if (location.flagRejected) {
      setError("That flag is not on the map.");
    }
    const observer = new ResizeObserver(fit);
    observer.observe(node);
    return () => observer.disconnect();
    // applyView is recreated each render and reads the latest refs.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [field]);

  useEffect(() => () => window.clearTimeout(inViewTimerRef.current), []);

  useEffect(() => {
    if (!revealId) return;
    const timer = window.setTimeout(() => setRevealId((current) => (current === revealId ? null : current)), 1400);
    return () => window.clearTimeout(timer);
  }, [revealId]);

  useEffect(() => {
    if (waveIds.length === 0) return;
    const timer = window.setTimeout(() => setWaveIds([]), 2400);
    return () => window.clearTimeout(timer);
  }, [waveIds]);

  useEffect(() => {
    const trimmed = query.trim();
    if (trimmed.length < 3) {
      setSubmitterSuggestions([]);
      return;
    }
    let cancelled = false;
    const timer = window.setTimeout(() => {
      void lookupSubmitterSuggestions(trimmed, undefined, apiBase)
        .then((result) => {
          if (cancelled) return;
          if (result.status === 429) {
            setSubmitterNote("Too many lookups. Wait a minute and try again.");
            setSubmitterSuggestions([]);
            return;
          }
          const body = result.body as { suggestions?: SubmitterSuggestion[] } | null;
          const next = Array.isArray(body?.suggestions)
            ? body.suggestions.filter((item) => item && typeof item.label === "string" && typeof item.token === "string" && item.token.length > 0)
            : [];
          setSubmitterNote("");
          setSubmitterSuggestions(next);
        })
        .catch(() => {
          if (!cancelled) setSubmitterSuggestions([]);
        });
    }, SUBMITTER_SUGGESTION_DEBOUNCE_MS);
    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, [query]);

  useLayoutEffect(() => {
    if (mapFullRef.current) document.documentElement.classList.add(MAP_FULLSCREEN_CLASS);
  }, []);

  useLayoutEffect(() => {
    paintAxis(viewRef.current);
  });

  useEffect(() => {
    const root = document.documentElement;
    const active = () => Boolean(document.fullscreenElement || (document as Document & { webkitFullscreenElement?: Element }).webkitFullscreenElement);
    const onChange = () => {
      if (!active() && mapFullRef.current && !cssFallbackRef.current) {
        mapFullRef.current = false;
        cssFallbackRef.current = false;
        setMapFull(false);
        root.classList.remove(MAP_FULLSCREEN_CLASS);
      }
    };
    const onKey = (event: KeyboardEvent) => {
      if (event.key !== "Escape" || !mapFullRef.current) return;
      const target = flagMapEscapeTarget({ ...escapeStateRef.current, mapFull: mapFullRef.current });
      event.preventDefault();
      event.stopPropagation();
      if (target === "card") {
        setSelected(null);
        setRevealId(null);
      } else if (target === "list") {
        setOpen(false);
        setJumpPending("");
      } else {
        void exitMapFull();
      }
    };
    document.addEventListener("fullscreenchange", onChange);
    document.addEventListener("webkitfullscreenchange", onChange);
    window.addEventListener("keydown", onKey, true);
    return () => {
      document.removeEventListener("fullscreenchange", onChange);
      document.removeEventListener("webkitfullscreenchange", onChange);
      window.removeEventListener("keydown", onKey, true);
      root.classList.remove(MAP_FULLSCREEN_CLASS);
    };
  }, []);

  useLayoutEffect(() => {
    const root = document.documentElement;
    const bandNames = ["--flag-map-header", "--flag-map-tools", "--flag-map-bottom", "--flag-map-side", "--flag-map-pad-top", "--flag-map-pad-bottom", "--flag-map-legend"];
    const clearBands = () => {
      for (const name of bandNames) root.style.removeProperty(name);
    };
    if (kiosk) {
      root.classList.remove("flagMapPage");
      clearBands();
      root.classList.add("flagMapKiosk");
      const meta = document.querySelector('meta[name="viewport"]');
      const previous = meta?.getAttribute("content") ?? "";
      meta?.setAttribute("content", "width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no");
      return () => {
        root.classList.remove("flagMapKiosk");
        if (previous) meta?.setAttribute("content", previous);
      };
    }
    root.classList.remove("flagMapKiosk");
    root.classList.add("flagMapPage");
    const applyBands = () => {
      const bands = flagMapPageBands({ width: window.innerWidth, height: window.innerHeight });
      root.style.setProperty("--flag-map-header", `${bands.header}px`);
      root.style.setProperty("--flag-map-tools", `${bands.tools}px`);
      root.style.setProperty("--flag-map-bottom", `${bands.bottom}px`);
      root.style.setProperty("--flag-map-side", `${bands.side}px`);
      root.style.setProperty("--flag-map-pad-top", `${bands.mapPadTop}px`);
      root.style.setProperty("--flag-map-pad-bottom", `${bands.mapPadBottom}px`);
      root.style.setProperty("--flag-map-legend", `${bands.legend}px`);
    };
    applyBands();
    window.addEventListener("resize", applyBands);
    return () => {
      root.classList.remove("flagMapPage");
      clearBands();
      window.removeEventListener("resize", applyBands);
    };
  }, [kiosk]);

  useEffect(() => {
    const media = window.matchMedia("(prefers-reduced-motion: reduce)");
    const apply = () => {
      reducedMotionRef.current = media.matches;
    };
    apply();
    media.addEventListener("change", apply);
    return () => media.removeEventListener("change", apply);
  }, []);

  useEffect(() => {
    if (!kiosk) return;
    const blockMenu = (event: Event) => event.preventDefault();
    const blockGesture = (event: Event) => event.preventDefault();
    const blockCtrlWheel = (event: WheelEvent) => {
      if (event.ctrlKey) event.preventDefault();
    };
    const onFirstPointer = () => {
      if (fullscreenTriedRef.current) return;
      fullscreenTriedRef.current = true;
      requestKioskFullscreen();
    };
    const onWake = (event: Event) => {
      if (!attractingRef.current) return;
      const target = event.target;
      if (target instanceof Element && target.closest(".kioskExitZone, .kioskPinOverlay")) return;
      event.preventDefault();
      event.stopPropagation();
      exitAttractRef.current();
      armIdleRef.current();
    };
    const blockDrag = (event: Event) => event.preventDefault();
    const blockSelect = (event: Event) => {
      const target = event.target;
      if (target instanceof Element && target.closest("input, textarea")) return;
      event.preventDefault();
    };
    const blockPagePinch = (event: TouchEvent) => {
      if (event.touches.length < 2) return;
      const target = event.target;
      if (target instanceof Element && target.closest(".publicFlagViewport")) return;
      event.preventDefault();
    };
    const blockNavKey = (event: KeyboardEvent) => {
      const target = event.target;
      const typing = target instanceof HTMLElement && Boolean(target.closest("input, textarea"));
      if ((event.altKey && event.key === "ArrowLeft") || (!typing && event.key === "Backspace")) {
        event.preventDefault();
      }
    };
    const onActivity = () => {
      if (attractingRef.current) return;
      armIdleRef.current();
    };
    document.addEventListener("contextmenu", blockMenu);
    document.addEventListener("gesturestart", blockGesture);
    document.addEventListener("dragstart", blockDrag);
    document.addEventListener("selectstart", blockSelect);
    document.addEventListener("touchmove", blockPagePinch, { passive: false });
    window.addEventListener("keydown", blockNavKey);
    window.addEventListener("wheel", blockCtrlWheel, { passive: false });
    window.addEventListener("pointerdown", onFirstPointer, true);
    window.addEventListener("pointerdown", onWake, true);
    window.addEventListener("touchstart", onWake, true);
    window.addEventListener("pointerdown", onActivity, true);
    window.addEventListener("keydown", onActivity);
    armIdleRef.current();
    return () => {
      document.removeEventListener("contextmenu", blockMenu);
      document.removeEventListener("gesturestart", blockGesture);
      document.removeEventListener("dragstart", blockDrag);
      document.removeEventListener("selectstart", blockSelect);
      document.removeEventListener("touchmove", blockPagePinch);
      window.removeEventListener("keydown", blockNavKey);
      window.removeEventListener("wheel", blockCtrlWheel);
      window.clearTimeout(exitHoldRef.current);
      window.removeEventListener("pointerdown", onFirstPointer, true);
      window.removeEventListener("pointerdown", onWake, true);
      window.removeEventListener("touchstart", onWake, true);
      window.removeEventListener("pointerdown", onActivity, true);
      window.removeEventListener("keydown", onActivity);
      window.clearTimeout(idleTimerRef.current);
      cancelAnimationFrame(attractFrameRef.current);
    };
  }, [kiosk]);

  useEffect(() => {
    const node = viewportRef.current;
    if (!node) return;
    let start: { x: number; y: number } | null = null;
    let origin = { x: 0, y: 0 };
    let moved = false;
    let captured = false;
    let pinching = false;
    let pinchStart = 0;
    let pinchOrigin: View | null = null;
    let pinchStartMid = { x: 0, y: 0 };

    const flushWheelFrame = () => {
      wheelFrameRef.current = 0;
      const batch = wheelBatchRef.current.flush();
      if (!batch) return;
      const next = limitView(zoomAt(
        viewRef.current,
        batch.point.x,
        batch.point.y,
        viewRef.current.scale * wheelScaleFactor(batch.deltaY)
      ));
      viewRef.current = next;
      setMoving(true);
      if (layerRef.current) layerRef.current.style.transform = gestureTransform(next);
      paintAxis(next);
      window.clearTimeout(wheelSettleRef.current);
      wheelSettleRef.current = window.setTimeout(settleGesture, WHEEL_SETTLE_MS);
    };

    const flushPinchFrame = () => {
      pinchFrameRef.current = 0;
      const pending = pinchBatchRef.current.flush();
      if (!pending) return;
      const next = limitView(pending);
      viewRef.current = next;
      setMoving(true);
      if (layerRef.current) layerRef.current.style.transform = gestureTransform(next);
      paintAxis(next);
      window.clearTimeout(pinchSettleRef.current);
      pinchSettleRef.current = window.setTimeout(settleGesture, WHEEL_SETTLE_MS);
    };

    const onWheel = (event: WheelEvent) => {
      event.preventDefault();
      if (attractingRef.current) exitAttractRef.current();
      cancelAnimationFrame(animRef.current);
      userAdjustedRef.current = true;
      if (!wheelFrameRef.current) syncViewportBox();
      wheelBatchRef.current.push(event.deltaY, localPoint(event));
      armIdleRef.current();
      if (wheelFrameRef.current) return;
      wheelFrameRef.current = requestAnimationFrame(flushWheelFrame);
    };

    const onPointerDown = (event: PointerEvent) => {
      suppressClickRef.current = false;
      if (pinching || event.button > 0) return;
      if ((event.target as HTMLElement | null)?.closest(".publicFlagChrome, .publicFlagDetail")) return;
      syncViewportBox();
      start = localPoint(event);
      origin = { x: viewRef.current.x, y: viewRef.current.y };
      moved = false;
      captured = false;
    };

    const onPointerMove = (event: PointerEvent) => {
      if (!start || pinching) return;
      const point = localPoint(event);
      if (!pointerDragCapture(start, point, captured)) return;
      if (!captured) {
        captured = true;
        cancelAnimationFrame(animRef.current);
        try {
          node.setPointerCapture(event.pointerId);
        } catch {
          captured = false;
        }
      }
      moved = true;
      userAdjustedRef.current = true;
      applyView({ x: origin.x + point.x - start.x, y: origin.y + point.y - start.y, scale: viewRef.current.scale });
    };

    const onPointerUp = (event: PointerEvent) => {
      if (!start) return;
      const point = localPoint(event);
      if (captured && node.hasPointerCapture(event.pointerId)) {
        node.releasePointerCapture(event.pointerId);
      }
      suppressClickRef.current = !pointerShouldOpen(start, point);
      if (captured) setMoving(false);
      start = null;
      captured = false;
      moved = false;
    };

    const touchDistance = (event: TouchEvent) => {
      const [first, second] = [event.touches[0], event.touches[1]];
      return Math.hypot(first.clientX - second.clientX, first.clientY - second.clientY);
    };

    const onTouchStart = (event: TouchEvent) => {
      if (event.touches.length !== 2) return;
      cancelAnimationFrame(animRef.current);
      pinching = true;
      pinchStart = touchDistance(event);
      pinchOrigin = { ...viewRef.current };
      syncViewportBox();
      const rect = boxRef.current;
      pinchStartMid = {
        x: (event.touches[0].clientX + event.touches[1].clientX) / 2 - rect.left,
        y: (event.touches[0].clientY + event.touches[1].clientY) / 2 - rect.top
      };
    };

    const onTouchMove = (event: TouchEvent) => {
      if (!pinching || !pinchOrigin || event.touches.length < 2 || pinchStart <= 0) return;
      event.preventDefault();
      userAdjustedRef.current = true;
      const rect = boxRef.current;
      const mid = {
        x: (event.touches[0].clientX + event.touches[1].clientX) / 2 - rect.left,
        y: (event.touches[0].clientY + event.touches[1].clientY) / 2 - rect.top
      };
      pinchBatchRef.current.push(pinchView(pinchOrigin, pinchStartMid, touchDistance(event), pinchStart, mid));
      if (pinchFrameRef.current) return;
      pinchFrameRef.current = requestAnimationFrame(flushPinchFrame);
    };

    const onTouchEnd = () => {
      if (!pinching) return;
      pinching = false;
      pinchOrigin = null;
      cancelAnimationFrame(pinchFrameRef.current);
      pinchFrameRef.current = 0;
      const next = pinchBatchRef.current.flush();
      if (next) paintView(limitView(next));
      window.clearTimeout(pinchSettleRef.current);
      settleGesture();
    };

    node.addEventListener("wheel", onWheel, { passive: false });
    node.addEventListener("pointerdown", onPointerDown);
    node.addEventListener("pointermove", onPointerMove);
    node.addEventListener("pointerup", onPointerUp);
    node.addEventListener("pointercancel", onPointerUp);
    node.addEventListener("touchstart", onTouchStart, { passive: true });
    node.addEventListener("touchmove", onTouchMove, { passive: false });
    node.addEventListener("touchend", onTouchEnd);
    node.addEventListener("touchcancel", onTouchEnd);
    return () => {
      node.removeEventListener("wheel", onWheel);
      node.removeEventListener("pointerdown", onPointerDown);
      node.removeEventListener("pointermove", onPointerMove);
      node.removeEventListener("pointerup", onPointerUp);
      node.removeEventListener("pointercancel", onPointerUp);
      node.removeEventListener("touchstart", onTouchStart);
      node.removeEventListener("touchmove", onTouchMove);
      node.removeEventListener("touchend", onTouchEnd);
      node.removeEventListener("touchcancel", onTouchEnd);
      cancelAnimationFrame(wheelFrameRef.current);
      window.clearTimeout(wheelSettleRef.current);
      cancelAnimationFrame(pinchFrameRef.current);
      window.clearTimeout(pinchSettleRef.current);
    };
    // Gesture listeners read refs so they can stay attached for the life of the canvas.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [field]);

  function acceptClick(action: () => void) {
    if (suppressClickRef.current) {
      suppressClickRef.current = false;
      return;
    }
    action();
  }

  function onSearchKeyDown(event: React.KeyboardEvent<HTMLInputElement>) {
    if (event.key === "ArrowDown") {
      event.preventDefault();
      setOpen(true);
      setActive((current) => Math.min(current + 1, Math.max(searchRows.length - 1, 0)));
    } else if (event.key === "ArrowUp") {
      event.preventDefault();
      setOpen(true);
      setActive((current) => Math.max(current - 1, 0));
    } else if (event.key === "Enter" && open && searchRows[active]) {
      event.preventDefault();
      pickSuggestion(searchRows[active]);
    } else if (event.key === "Escape") {
      setOpen(false);
    }
  }

  function pickSuggestion(row: SearchRow) {
    if (row.kind === "submitter") {
      void waveSubmitter(row.token);
      return;
    }
    const hit = row.hit;
    setQuery(hit.displayName);
    setOpen(false);
    const flag = (layoutRef.current?.flags as FlagBox[] | undefined)?.find((item) => item.flagGridId === hit.flagGridId);
    if (flag) openFlag(flag, { celebrate: true });
  }

  async function waveSubmitter(token: string) {
    setOpen(false);
    setSubmitterNote("");
    setSelected(null);
    setHighlightId(null);
    setRevealId(null);
    writeFlagParam(null);
    try {
      const result = await resolveSubmitterFlags(token, undefined, apiBase);
      if (result.status === 429) {
        setSubmitterNote("Too many lookups. Wait a minute and try again.");
        return;
      }
      const body = result.body as { flags?: Array<{ flagGridId?: number; honoreeId?: number }> } | null;
      const flags = (layoutRef.current?.flags ?? []) as FlagBox[];
      const matched: FlagBox[] = [];
      for (const hit of body?.flags ?? []) {
        const id = Number(hit.flagGridId);
        const honoreeId = Number(hit.honoreeId);
        const flag = flags.find((item) => item.flagGridId === id)
          ?? (Number.isFinite(honoreeId) ? flags.find((item) => item.honoreeId === honoreeId) : undefined);
        if (flag && !matched.some((item) => item.flagGridId === flag.flagGridId)) matched.push(flag);
      }
      if (matched.length === 0) {
        setWaveIds([]);
        setSubmitterNote("No flags found");
        return;
      }
      setWaveIds(matched.map((flag) => flag.flagGridId));
      const bounds = boundsOf(matched);
      if (bounds) {
        userAdjustedRef.current = true;
        animateTo(settledView(fitView(bounds, viewportSize(), 48)));
      }
    } catch {
      setWaveIds([]);
      setSubmitterNote("No flags found");
    }
  }

  function onMapKeyDown(event: React.KeyboardEvent<HTMLElement>) {
    if (event.target instanceof HTMLInputElement || event.target instanceof HTMLTextAreaElement || event.target instanceof HTMLSelectElement) return;
    if (event.key === "Escape") {
      setSelected(null);
      setRevealId(null);
      setOpen(false);
      return;
    }
    if (event.key === "+" || event.key === "=") {
      event.preventDefault();
      zoomBy(FLAG_MAP_ZOOM.zoomStep);
    } else if (event.key === "-" || event.key === "_") {
      event.preventDefault();
      zoomBy(1 / FLAG_MAP_ZOOM.zoomStep);
    } else if (event.key === "ArrowLeft") {
      event.preventDefault();
      userAdjustedRef.current = true;
      applyView(panView(viewRef.current, FLAG_MAP_ZOOM.panStep, 0), true);
    } else if (event.key === "ArrowRight") {
      event.preventDefault();
      userAdjustedRef.current = true;
      applyView(panView(viewRef.current, -FLAG_MAP_ZOOM.panStep, 0), true);
    } else if (event.key === "ArrowUp") {
      event.preventDefault();
      userAdjustedRef.current = true;
      applyView(panView(viewRef.current, 0, FLAG_MAP_ZOOM.panStep), true);
    } else if (event.key === "ArrowDown") {
      event.preventDefault();
      userAdjustedRef.current = true;
      applyView(panView(viewRef.current, 0, -FLAG_MAP_ZOOM.panStep), true);
    }
  }

  function exitMapFull() {
    cssFallbackRef.current = false;
    mapFullRef.current = false;
    setMapFull(false);
    document.documentElement.classList.remove(MAP_FULLSCREEN_CLASS);
    return exitDocumentFullscreen(document);
  }

  async function enterMapFull() {
    const root = document.documentElement;
    mapFullRef.current = true;
    setMapFull(true);
    root.classList.add(MAP_FULLSCREEN_CLASS);
    const ok = await requestElementFullscreen(root);
    cssFallbackRef.current = !ok;
    if (!ok && !mapFullRef.current) root.classList.remove(MAP_FULLSCREEN_CLASS);
  }

  function toggleMapFull() {
    if (mapFullRef.current) void exitMapFull();
    else void enterMapFull();
  }

  function beginExitHold(event: { preventDefault: () => void; stopPropagation: () => void }) {
    event.preventDefault();
    event.stopPropagation();
    window.clearTimeout(exitHoldRef.current);
    exitHoldRef.current = window.setTimeout(() => {
      setPinDigits("");
      setPinMessage("");
      setPinOpen(true);
    }, KIOSK_EXIT_HOLD_MS);
  }

  function cancelExitHold() {
    window.clearTimeout(exitHoldRef.current);
  }

  function appendPinDigit(digit: string) {
    setPinMessage("");
    setPinDigits((current) => (current.length >= 12 ? current : current + digit));
  }

  async function submitExitPin() {
    if (pinBusy) return;
    setPinBusy(true);
    try {
      const status = await requestKioskExit(pinDigits);
      if (status === 204) {
        setPinOpen(false);
        onExitKiosk?.();
        return;
      }
      setPinDigits("");
      setPinMessage(status === 429 ? "Too many tries. Wait a minute and try again." : "That PIN is not right.");
    } catch {
      setPinDigits("");
      setPinMessage("That PIN is not right.");
    } finally {
      setPinBusy(false);
    }
  }

  return (
    <section
      className={["publicFlagMap", kiosk ? "isKiosk" : "", selected ? "hasDetail" : ""].filter(Boolean).join(" ")}
      data-kiosk={kiosk ? "1" : "0"}
      data-attract={attracting ? "1" : "0"}
      aria-label="Public Flag Map"
      onKeyDown={onMapKeyDown}
      onContextMenu={kiosk ? (event) => event.preventDefault() : undefined}
    >
      <div className="publicFlagChrome">
        <div className="sectionHeader publicFlagHeading">
          <div>
            <p className="eyebrow">Field</p>
            <h2>Public Flag Map</h2>
            <p id={helpId} className={kiosk ? "helperText srOnly" : "helperText"}>
              Pinch or use the wheel to zoom. Drag to move around the field. Zooming in shows each flag number, then the honoree name inside the spot, then the branch seal once fewer than 10 flags are on screen.
            </p>
          </div>
        </div>
        <div className="publicFlagSearch">
          <label htmlFor="public-flag-search">Search honorees</label>
          <input
            id="public-flag-search"
            role="combobox"
            aria-expanded={open && searchRows.length > 0}
            aria-controls={listId}
            aria-autocomplete="list"
            aria-activedescendant={activeDescendant}
            type="search"
            inputMode="search"
            enterKeyHint="search"
            autoComplete="off"
            autoCapitalize="none"
            placeholder="Type a name"
            value={query}
            onChange={(event) => {
              setQuery(event.target.value);
              setOpen(true);
              setActive(0);
            }}
            onFocus={() => setOpen(true)}
            onKeyDown={onSearchKeyDown}
          />
          {submitterNote ? <p className="message" role="status">{submitterNote}</p> : null}
          {open && searchRows.length > 0 ? (
            <ul id={listId} role="listbox" className="publicFlagSuggestions">
              {searchRows.map((row, optionIndex) => row.kind === "honoree" ? (
                <li
                  id={`${listId}-${row.hit.honoreeId}`}
                  key={`${row.hit.honoreeId}-${row.hit.flagGridId}`}
                  role="option"
                  aria-selected={optionIndex === active}
                  className={optionIndex === active ? "isActive" : ""}
                  onMouseDown={(event) => {
                    event.preventDefault();
                    pickSuggestion(row);
                  }}
                >
                  <strong>{row.hit.displayName}</strong>
                  <span>{[row.hit.serviceBranchName, row.hit.section, row.hit.flagGridName].filter(Boolean).join(" · ")}</span>
                </li>
              ) : (
                <li
                  id={`${listId}-submitter-${row.token}`}
                  key={row.token}
                  role="option"
                  aria-selected={optionIndex === active}
                  className={optionIndex === active ? "isActive" : ""}
                  onMouseDown={(event) => {
                    event.preventDefault();
                    pickSuggestion(row);
                  }}
                >
                  <strong>{row.label}</strong>
                </li>
              ))}
            </ul>
          ) : null}
        </div>
        <div className="publicFlagToolbar">
          {kiosk ? <button type="button" onClick={goHome} aria-label="Home">Home</button> : null}
          {kiosk ? <button type="button" className="secondary" onClick={goBack} aria-label="Back">Back</button> : null}
          <div ref={toolbarRef} className="publicFlagControls" role="toolbar" aria-label="Map controls" onKeyDown={onToolbarKeyDown}>
            <button type="button" className="secondary publicFlagIconButton" tabIndex={toolIndex === 0 ? 0 : -1} onFocus={() => setToolIndex(0)} onClick={() => zoomBy(1 / FLAG_MAP_ZOOM.zoomStep)} aria-label="Zoom out" data-tooltip="Zoom out">
              <ToolIcon name="zoomOut" />
            </button>
            <button type="button" className="secondary publicFlagIconButton" tabIndex={toolIndex === 1 ? 0 : -1} onFocus={() => setToolIndex(1)} onClick={() => zoomBy(FLAG_MAP_ZOOM.zoomStep)} aria-label="Zoom in" data-tooltip="Zoom in">
              <ToolIcon name="zoomIn" />
            </button>
            <button
              type="button"
              className="secondary publicFlagIconButton"
              tabIndex={toolIndex === 2 ? 0 : -1}
              onFocus={() => setToolIndex(2)}
              aria-pressed={mapFull}
              aria-label={mapFull ? "Exit full screen" : "Full screen"}
              data-tooltip={mapFull ? "Exit full screen" : "Full screen"}
              onClick={toggleMapFull}
            >
              <ToolIcon name={mapFull ? "exitFullScreen" : "fullScreen"} />
            </button>
            {kiosk ? null : (
              <button type="button" className="secondary publicFlagIconButton" tabIndex={toolIndex === 3 ? 0 : -1} onFocus={() => setToolIndex(3)} onClick={goHome} aria-label="Reset view" data-tooltip="Reset view">
                <ToolIcon name="reset" />
              </button>
            )}
            <div className="publicFlagJump">
              <label htmlFor={jumpId} className="srOnly">{inView.kind === "sections" ? "Go to a section in view" : "Go to a spot in view"}</label>
              <select id={jumpId} value={jumpPending} onChange={onJumpChange} onKeyDown={onJumpKeyDown} onBlur={() => setJumpPending("")}>
                <option value="">{flagMapItemsSummary(inView)}</option>
                {inView.items.map((item) => <option key={item.key} value={item.key}>{item.label}</option>)}
              </select>
            </div>
          </div>
          <p className="publicFlagLevel srOnly" aria-live="polite">{levelText}</p>
        </div>
      </div>

      {error ? <p className="message error" role="alert">{error}</p> : null}

      <div className="publicFlagStage">
      <div
        ref={viewportRef}
        className="publicFlagViewport"
        role="region"
        aria-label="Flag field"
        tabIndex={0}
        aria-describedby={helpId}
      >
        {loading ? <p className="publicFlagLoading">Loading the flag map…</p> : null}
        <div ref={layerRef} className="publicFlagLayer" style={{ width: field?.worldWidth, height: field?.worldHeight }}>
          {field?.aisle ? (
            <div className="publicFlagAisle" style={{ left: field.aisle.x, top: field.aisle.y, width: field.aisle.width, height: field.aisle.height }} />
          ) : null}
          {visibleCells.map((cell) => (
            <div
              key={`cell-${cell.flagGridId}`}
              className={cell.status === "Reserved" ? "publicFlagSeat isReserved" : "publicFlagSeat"}
              style={{ left: cell.x, top: cell.y, width: cell.width, height: cell.height }}
            />
          ))}
          {field?.pavilion ? (
            <div className="publicFlagPavilion" style={{ left: field.pavilion.x, top: field.pavilion.y, width: field.pavilion.width, height: field.pavilion.height, fontSize: labelFontSize(field.pavilion.label, field.pavilion.width, field.pavilion.height, renderScale, { max: 13 }) || undefined }}>
              {labelFontSize(field.pavilion.label, field.pavilion.width, field.pavilion.height, renderScale, { max: 13 }) ? field.pavilion.label : null}
            </div>
          ) : null}
          {visibleZones.map((zone) => {
            const sizes = zoneLabelSizes(zone, renderScale, sectionLabels);
            return (
              <button
                key={zone.label}
                type="button"
                className={["publicFlagTile publicFlagZone", sizes.narrow ? "isNarrow" : "", sizes.tiny ? "isTiny" : ""].filter(Boolean).join(" ")}
                style={{ left: zone.x, top: zone.y, width: zone.width, height: zone.height, paddingTop: sizes.top || undefined }}
                aria-label={`${zone.label}, ${zone.occupiedCount} of ${zone.flagCount} flags`}
                onClick={() => acceptClick(() => zoomToZone(zone))}
              >
                {sizes.letter ? <strong className={sizes.letterStacked ? "isUpright" : undefined} style={{ fontSize: sizes.letter, minHeight: sizes.letterBlock || undefined }}>{zone.section}</strong> : null}
              </button>
            );
          })}
          {field?.serviceFlags ? (() => {
            const box = field.serviceFlags;
            const area = serviceLabelBox ?? serviceFlagsLabelBox(box, null);
            const label = area ? serviceFlagsLabelSize(area, renderScale, sectionLabels) : { size: 0, vertical: false, text: "", narrow: false, top: 0 };
            return (
              <div className={label.narrow ? "publicFlagServiceFlags isNarrow" : "publicFlagServiceFlags"} role="img" aria-label={`${box.label}, ${SERVICE_FLAGS_SECTION} 1\u2013${FIELD_MAP_Z_ROWS}`} style={{ left: box.x, top: box.y, width: box.width, height: box.height }}>
                {area && label.size ? (
                  <span style={{ left: area.x, top: area.y, width: area.width, height: area.height, fontSize: label.size, paddingTop: label.top || undefined }}>
                    {label.vertical ? <span className="isVertical">{label.text}</span> : label.text.split(" ").map((word) => <span key={word}>{word}</span>)}
                  </span>
                ) : null}
              </div>
            );
          })() : null}
          {visibleCells.map((cell) => {
            const spot = spotContents(cell, gates, renderScale);
            const interactive = !cell.template && cell.flagGridId > 0 && cell.status !== "Reserved";
            const revealed = cell.flagGridId === revealId || waveIds.includes(cell.flagGridId);
            const highlighted = cell.flagGridId === highlightId;
            const className = [
              "publicFlagMarker",
              spot.status === "Occupied" ? "isOccupied" : spot.status === "Reserved" ? "isReserved" : "isOpen",
              highlighted ? "isHighlighted" : "",
              revealed ? "isRevealed" : ""
            ].filter(Boolean).join(" ");
            const label = [cell.flagGridName, cell.displayName || cell.status, cell.rank, cell.serviceBranchName].filter(Boolean).join(", ");
            const box = { left: cell.x, top: cell.y, width: cell.width, height: cell.height, padding: spot.pad || undefined };
            const body = (
              <>
                {spot.seal > 0 && cell.logoUrl ? (
                  <img
                    className="publicFlagSealImg"
                    src={sealUrl(cell.logoUrl, spot.seal * renderScale > 64 ? 256 : 128, true)}
                    alt=""
                    width={spot.seal}
                    height={spot.seal}
                    style={{ width: spot.seal, height: spot.seal }}
                    loading="lazy"
                    decoding="async"
                    draggable={false}
                  />
                ) : null}
                {spot.number ? <b className="publicFlagNumber" style={{ fontSize: spot.numberSize }}>{spot.number}</b> : null}
                {spot.lines.length ? (
                  <em style={{ fontSize: spot.nameSize }}>
                    {spot.lines.map((line, lineIndex) => <span key={lineIndex}>{line}</span>)}
                  </em>
                ) : null}
                {spot.rank ? <small style={{ fontSize: spot.rankSize }}>{spot.rank}</small> : null}
              </>
            );
            return interactive ? (
              <button
                id={`public-flag-${cell.flagGridId}`}
                key={cell.flagGridId}
                type="button"
                className={className}
                style={box}
                aria-current={highlighted ? "true" : undefined}
                aria-label={label}
                onClick={() => acceptClick(() => openFlag(cell as FlagBox))}
              >
                {body}
              </button>
            ) : (
              <div
                key={cell.flagGridId}
                className={className}
                style={box}
                aria-hidden="true"
              >
                {body}
              </div>
            );
          })}
        </div>
        <div ref={axisRef} className="publicFlagAxis" aria-hidden="true">
          {field?.rowLabels.map((label) => (
            <div key={`row-${label.side}-${label.row}`} className={label.side === "right" ? "publicFlagRowLabel isRight" : "publicFlagRowLabel"}>{label.label}</div>
          ))}
          {field?.columnLabels.map((label) => (
            <div key={`col-${label.section}`} className="publicFlagColumnLabel">{label.label}</div>
          ))}
        </div>
      </div>
      <ul className="publicFlagLegend" aria-label="Spot key">
        <li><i className="isOccupied" aria-hidden="true" />Occupied</li>
        <li><i className="isOpen" aria-hidden="true" />Open</li>
      </ul>
      </div>
      {selected ? createPortal(
        <div className={kiosk ? "publicFlagDetailLayer isKiosk" : "publicFlagDetailLayer"}>
          <div className="publicFlagDetailBackdrop" aria-hidden="true" />
          <FlagDetail
            flag={selected}
            revealed={selected.flagGridId === revealId}
            shareUrl={shareUrl}
            onPreview={onPreviewCard}
            showSeal={gates.seals}
            onClose={() => {
              setSelected(null);
              setRevealId(null);
              writeFlagParam(null);
            }}
          />
        </div>,
        document.body
      ) : null}
      {kiosk ? createPortal(<>
        <button
          type="button"
          className="kioskExitZone"
          aria-hidden="true"
          tabIndex={-1}
          onPointerDown={beginExitHold}
          onPointerUp={cancelExitHold}
          onPointerCancel={cancelExitHold}
          onPointerLeave={cancelExitHold}
          onContextMenu={(event) => event.preventDefault()}
        />
        {pinOpen ? (
          <div className="kioskPinOverlay" role="dialog" aria-modal="true" aria-label="Exit code">
            <div className="kioskPinPad">
              <p className="kioskPinDots" aria-live="polite">{pinDigits ? "•".repeat(pinDigits.length) : "Enter the code"}</p>
              {pinMessage ? <p className="message error" role="alert">{pinMessage}</p> : null}
              <div className="kioskPinKeys">
                {["1", "2", "3", "4", "5", "6", "7", "8", "9"].map((digit) => (
                  <button key={digit} type="button" onClick={() => appendPinDigit(digit)}>{digit}</button>
                ))}
                <button type="button" className="secondary" onClick={() => { setPinDigits(""); setPinMessage(""); }}>Clear</button>
                <button type="button" onClick={() => appendPinDigit("0")}>0</button>
                <button type="button" onClick={() => { setPinOpen(false); setPinDigits(""); setPinMessage(""); }}>Cancel</button>
              </div>
              <button type="button" disabled={pinBusy || pinDigits.length === 0} onClick={() => { void submitExitPin(); }}>
                {pinBusy ? "Checking…" : "Continue"}
              </button>
            </div>
          </div>
        ) : null}
      </>, document.body) : null}
    </section>
  );
}
