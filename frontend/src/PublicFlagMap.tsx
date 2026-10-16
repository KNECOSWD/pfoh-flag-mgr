import { useEffect, useId, useLayoutEffect, useMemo, useRef, useState } from "react";
import { qrModules, qrPath } from "./qrCode.js";
import {
  FLAG_MAP_ZOOM,
  KIOSK_IDLE_MS,
  WHEEL_SETTLE_MS,
  applyKioskIdleReset,
  attractView,
  createPinchBatch,
  createWheelBatch,
  createFlagMapLoader,
  cullRects,
  filterFlagMapSuggestions,
  fitView,
  flagDeepLink,
  flagMapDetailGates,
  lookupSubmittedFlags,
  flagMapLayerOpacities,
  flagYears,
  layoutFlagField,
  panView,
  pinchView,
  pointerDragCapture,
  pointerShouldOpen,
  publicFlagMapUrls,
  readFlagMapLocation,
  springEase,
  viewportWorldRect,
  wheelFrameShouldCull,
  wheelScaleFactor,
  withApiBase,
  zoomAt
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
  seals: false,
  names: false,
  ranks: false,
  sectionsInteractive: true,
  flagsInteractive: false
};

const loadJson = createFlagMapLoader() as <T>(url: string) => Promise<T>;

function sameIds(left: number[], right: number[]) {
  if (left.length !== right.length) return false;
  for (let index = 0; index < left.length; index += 1) {
    if (left[index] !== right[index]) return false;
  }
  return true;
}

function sameGates(left: Gates, right: Gates) {
  return left.seals === right.seals
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
  onClose
}: {
  flag: FlagBox;
  revealed: boolean;
  shareUrl: string;
  onPreview: (honoreeId: number, title: string) => void;
  onClose: () => void;
}) {
  const [showPhoto, setShowPhoto] = useState(Boolean(flag.photoUrl));
  useEffect(() => {
    setShowPhoto(Boolean(flag.photoUrl));
  }, [flag.flagGridId, flag.photoUrl]);
  const years = flagYears(flag);

  return (
    <aside className={revealed ? "publicFlagDetail isRevealed" : "publicFlagDetail"} aria-label="Flag detail">
      {showPhoto && flag.photoUrl ? (
        <img
          className="publicFlagPhoto"
          src={withApiBase(apiBase, flag.photoUrl)}
          alt=""
          draggable={false}
          onError={() => setShowPhoto(false)}
        />
      ) : null}
      <div className="publicFlagDetailBody">
        {flag.displayName ? <h3>{flag.displayName}</h3> : null}
        {flag.rank ? <p className="publicFlagRank">{flag.rank}</p> : null}
        {flag.serviceBranchName ? (
          <p className="publicFlagBranch">
            {flag.logoUrl ? <img className="publicFlagSeal" src={withApiBase(apiBase, flag.logoUrl)} alt="" draggable={false} /> : null}
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
  const location = useMemo(() => readFlagMapLocation(window.location.search), []);
  const kiosk = kioskLocked || location.kiosk || kioskSessionOn(window.sessionStorage);
  const viewportRef = useRef<HTMLDivElement | null>(null);
  const layerRef = useRef<HTMLDivElement | null>(null);
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
  const reducedMotionRef = useRef(false);
  const kioskRef = useRef(kiosk);
  const gatesRef = useRef<Gates>(EMPTY_GATES);
  const levelRef = useRef("Sections");
  const pendingFlagRef = useRef<number | null>(location.flagId);
  const historyRef = useRef<View[]>([]);
  const wheelBatchRef = useRef(createWheelBatch());
  const pinchBatchRef = useRef(createPinchBatch());
  const boxRef = useRef({ left: 0, top: 0, width: 1, height: 1 });
  const opacityCacheRef = useRef({ section: "", seal: "", name: "", rank: "" });
  const layoutRef = useRef<ReturnType<typeof layoutFlagField> | null>(null);
  const [field, setField] = useState<ReturnType<typeof layoutFlagField> | null>(null);
  const [index, setIndex] = useState<SearchHit[]>([]);
  const [query, setQuery] = useState("");
  const [searchTab, setSearchTab] = useState<"honoree" | "submitted">("honoree");
  const [submitterFirst, setSubmitterFirst] = useState("");
  const [submitterLast, setSubmitterLast] = useState("");
  const [submitterNote, setSubmitterNote] = useState("");
  const [submitterHits, setSubmitterHits] = useState<SearchHit[]>([]);
  const [submitterBusy, setSubmitterBusy] = useState(false);
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const [error, setError] = useState(location.flagRejected ? "That flag is not on the map." : "");
  const [loading, setLoading] = useState(true);
  const [visibleSectionKeys, setVisibleSectionKeys] = useState<string[]>([]);
  const [visibleFlagIds, setVisibleFlagIds] = useState<number[]>([]);
  const [gates, setGates] = useState<Gates>(EMPTY_GATES);
  const [levelText, setLevelText] = useState("Sections");
  const [selected, setSelected] = useState<FlagBox | null>(null);
  const [highlightId, setHighlightId] = useState<number | null>(null);
  const [revealId, setRevealId] = useState<number | null>(null);
  const [attracting, setAttracting] = useState(false);
  const [pinOpen, setPinOpen] = useState(false);
  const [pinDigits, setPinDigits] = useState("");
  const [pinMessage, setPinMessage] = useState("");
  const [pinBusy, setPinBusy] = useState(false);
  const suggestions = filterFlagMapSuggestions(index, query) as SearchHit[];
  const shareUrl = selected ? flagDeepLink(window.location.href, selected.flagGridId) : "";

  const visibleSections = useMemo(() => {
    if (!field) return [];
    const keys = new Set(visibleSectionKeys);
    return field.sections.filter((section) => keys.has(section.section));
  }, [field, visibleSectionKeys]);

  const visibleFlags = useMemo(() => {
    if (!field || !gates.seals) return [];
    const ids = new Set(visibleFlagIds);
    return (field.flags as FlagBox[]).filter((flag) => ids.has(flag.flagGridId));
  }, [field, gates.seals, visibleFlagIds]);

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

  function paintView(next: View) {
    const scale = Math.min(FLAG_MAP_ZOOM.maxScale, Math.max(FLAG_MAP_ZOOM.minScale, next.scale));
    const view = { x: next.x, y: next.y, scale };
    viewRef.current = view;
    const opacity = flagMapLayerOpacities(view.scale, fitScaleRef.current);
    const layer = layerRef.current;
    if (layer) {
      layer.style.transform = `translate(${view.x}px, ${view.y}px) scale(${view.scale})`;
      writeOpacity(layer, "--section-opacity", "section", opacity.section);
      writeOpacity(layer, "--seal-opacity", "seal", opacity.seal);
      writeOpacity(layer, "--name-opacity", "name", opacity.name);
      writeOpacity(layer, "--rank-opacity", "rank", opacity.rank);
    }
    const viewport = viewportRef.current;
    if (viewport) {
      const sections = opacity.section >= 0.5 ? "on" : "off";
      const flags = opacity.seal > 0.5 ? "on" : "off";
      if (viewport.dataset.sections !== sections) viewport.dataset.sections = sections;
      if (viewport.dataset.flags !== flags) viewport.dataset.flags = flags;
    }
    const nextLevel = opacity.rank >= 0.85 ? "Closest" : opacity.name >= 0.85 ? "Names" : opacity.seal >= 0.85 ? "Seals" : opacity.section >= 0.85 ? "Sections" : "";
    return { view, opacity, nextLevel };
  }

  function writeOpacity(layer: HTMLDivElement, property: string, key: "section" | "seal" | "name" | "rank", value: number) {
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
      const nextGates = flagMapDetailGates(view.scale, fitScaleRef.current);
      gatesRef.current = nextGates;
      const sectionKeys = cullRects(layout.sections, seen).map((section: Box) => section.section as string);
      const flagIds = nextGates.seals
        ? cullRects(layout.flags, seen).map((flag: FlagBox) => flag.flagGridId)
        : [];
      setGates((current) => (sameGates(current, nextGates) ? current : nextGates));
      setVisibleSectionKeys((current) => (current.join("|") === sectionKeys.join("|") ? current : sectionKeys));
      setVisibleFlagIds((current) => (sameIds(current, flagIds) ? current : flagIds));
      const painted = paintView(view);
      if (painted.nextLevel && painted.nextLevel !== levelRef.current) {
        levelRef.current = painted.nextLevel;
        setLevelText(painted.nextLevel);
      }
    };
    if (immediate) {
      cancelAnimationFrame(cullFrameRef.current);
      run();
      return;
    }
    if (cullFrameRef.current) return;
    cullFrameRef.current = requestAnimationFrame(run);
  }

  function applyView(next: View, cullNow = false) {
    paintView(next);
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
  }

  function animateTo(target: View, onDone?: () => void) {
    stopMotion();
    const from = { ...viewRef.current };
    const unchanged = Math.abs(from.x - target.x) < 0.5
      && Math.abs(from.y - target.y) < 0.5
      && Math.abs(from.scale - target.scale) < 0.0001;
    if (reducedMotionRef.current || unchanged) {
      applyView(target, true);
      onDone?.();
      return;
    }
    const started = performance.now();
    let lastGates = flagMapDetailGates(from.scale, fitScaleRef.current);
    const step = (now: number) => {
      const amount = Math.min(1, (now - started) / 680);
      const eased = springEase(amount);
      const next = {
        x: from.x + (target.x - from.x) * eased,
        y: from.y + (target.y - from.y) * eased,
        scale: from.scale + (target.scale - from.scale) * eased
      };
      paintView(next);
      const nextGates = flagMapDetailGates(next.scale, fitScaleRef.current);
      const done = amount >= 1;
      if (wheelFrameShouldCull(lastGates, nextGates, done)) {
        lastGates = nextGates;
        publishCull(true);
      }
      if (!done) {
        animRef.current = requestAnimationFrame(step);
      } else {
        onDone?.();
      }
    };
    animRef.current = requestAnimationFrame(step);
  }

  function fitWorld() {
    const fitted = fitView(worldBounds(), viewportSize());
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
    animateTo(zoomAt(viewRef.current, point.x, point.y, viewRef.current.scale * factor));
  }

  function openFlag(flag: FlagBox, options?: { celebrate?: boolean }) {
    setError("");
    historyRef.current = [...historyRef.current, { ...viewRef.current }].slice(-16);
    setHighlightId(flag.flagGridId);
    setSelected(flag.honoreeId ? flag : null);
    setRevealId(options?.celebrate && flag.honoreeId ? flag.flagGridId : null);
    writeFlagParam(flag.flagGridId);
    const size = viewportSize();
    const scale = Math.min(FLAG_MAP_ZOOM.maxScale, fitScaleRef.current * FLAG_MAP_ZOOM.rankFadeEnd);
    const centerX = flag.x + flag.width / 2;
    const centerY = flag.y + flag.height / 2;
    const panelBias = kioskRef.current ? Math.min(size.width * 0.16, 180) : 0;
    userAdjustedRef.current = true;
    animateTo({
      scale,
      x: size.width / 2 - panelBias - centerX * scale,
      y: size.height / 2 - centerY * scale
    });
  }

  function goHome() {
    exitAttract();
    setError((current) => (current === "That flag is not on the map." ? "" : current));
    historyRef.current = [];
    setSelected(null);
    setHighlightId(null);
    setRevealId(null);
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
    setQuery(cleared.query);
    setOpen(cleared.open);
    setActive(cleared.active);
    setSearchTab(cleared.searchTab === "submitted" ? "submitted" : "honoree");
    setSubmitterFirst(cleared.submitterFirst);
    setSubmitterLast(cleared.submitterLast);
    setSubmitterNote(cleared.submitterNote);
    setSubmitterHits(cleared.submitterHits);
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

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    Promise.all([
      loadJson<SectionSummary[]>(urls.sections),
      loadJson<SearchHit[]>(urls.searchIndex)
    ])
      .then(async ([summaries, nextIndex]) => {
        const seats = await Promise.all(summaries.map(async (summary) => {
          const payload = await loadJson<{ seats: PublicSeat[] }>(urls.section(summary.section));
          return { ...summary, seats: payload.seats };
        }));
        if (cancelled) return;
        const nextField = layoutFlagField(seats);
        layoutRef.current = nextField;
        setField(nextField);
        setIndex(nextIndex);
        setError("");
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

  useEffect(() => {
    if (!revealId) return;
    const timer = window.setTimeout(() => setRevealId((current) => (current === revealId ? null : current)), 1400);
    return () => window.clearTimeout(timer);
  }, [revealId]);

  useLayoutEffect(() => {
    const root = document.documentElement;
    if (!kiosk) return;
    root.classList.add("flagMapKiosk");
    const meta = document.querySelector('meta[name="viewport"]');
    const previous = meta?.getAttribute("content") ?? "";
    meta?.setAttribute("content", "width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no");
    return () => {
      root.classList.remove("flagMapKiosk");
      if (previous) meta?.setAttribute("content", previous);
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
      const next = zoomAt(
        viewRef.current,
        batch.point.x,
        batch.point.y,
        viewRef.current.scale * wheelScaleFactor(batch.deltaY)
      );
      paintView(next);
      const nextGates = flagMapDetailGates(next.scale, fitScaleRef.current);
      if (wheelFrameShouldCull(gatesRef.current, nextGates, false)) {
        gatesRef.current = nextGates;
        publishCull(true);
      }
      window.clearTimeout(wheelSettleRef.current);
      wheelSettleRef.current = window.setTimeout(() => {
        publishCull(true);
      }, WHEEL_SETTLE_MS);
    };

    const flushPinchFrame = () => {
      pinchFrameRef.current = 0;
      const next = pinchBatchRef.current.flush();
      if (!next) return;
      paintView(next);
      const nextGates = flagMapDetailGates(next.scale, fitScaleRef.current);
      if (wheelFrameShouldCull(gatesRef.current, nextGates, false)) {
        gatesRef.current = nextGates;
        publishCull(true);
      }
      window.clearTimeout(pinchSettleRef.current);
      pinchSettleRef.current = window.setTimeout(() => {
        publishCull(true);
      }, WHEEL_SETTLE_MS);
    };

    const onWheel = (event: WheelEvent) => {
      event.preventDefault();
      if (attractingRef.current) exitAttractRef.current();
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
      if (next) paintView(next);
      window.clearTimeout(pinchSettleRef.current);
      publishCull(true);
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
      setActive((current) => Math.min(current + 1, Math.max(suggestions.length - 1, 0)));
    } else if (event.key === "ArrowUp") {
      event.preventDefault();
      setOpen(true);
      setActive((current) => Math.max(current - 1, 0));
    } else if (event.key === "Enter" && open && suggestions[active]) {
      event.preventDefault();
      pickSuggestion(suggestions[active]);
    } else if (event.key === "Escape") {
      setOpen(false);
    }
  }

  function pickSuggestion(hit: SearchHit) {
    setQuery(hit.displayName);
    setOpen(false);
    const flag = (layoutRef.current?.flags as FlagBox[] | undefined)?.find((item) => item.flagGridId === hit.flagGridId);
    if (flag) openFlag(flag, { celebrate: true });
  }

  function onMapKeyDown(event: React.KeyboardEvent<HTMLElement>) {
    if (event.target instanceof HTMLInputElement || event.target instanceof HTMLTextAreaElement) return;
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

  async function runSubmitterLookup() {
    setSubmitterBusy(true);
    setSubmitterNote("");
    setSubmitterHits([]);
    try {
      const result = await lookupSubmittedFlags(submitterFirst.trim(), submitterLast.trim(), undefined, apiBase);
      if (result.status === 429) {
        setSubmitterNote("Too many lookups. Wait a minute and try again.");
        return;
      }
      const body = result.body as { message?: string; refine?: boolean; flags?: SearchHit[] } | null;
      if (result.status === 400) {
        setSubmitterNote(body?.message || "Enter at least 2 letters of the first name and 3 of the last name.");
        return;
      }
      if (body?.refine) {
        setSubmitterNote("Too many flags match. Enter more of the name.");
        return;
      }
      if (!body?.flags || body.flags.length === 0) {
        setSubmitterNote(body?.message || "No flags found");
        return;
      }
      setSubmitterHits(body.flags);
    } catch {
      setSubmitterNote("No flags found");
    } finally {
      setSubmitterBusy(false);
    }
  }

  function openSubmitterHit(hit: SearchHit) {
    const flags = layoutRef.current?.flags as FlagBox[] | undefined;
    const flag = flags?.find((item) => item.flagGridId === hit.flagGridId) ?? flags?.find((item) => item.honoreeId === hit.honoreeId);
    if (!flag) {
      setSubmitterNote("No flags found");
      return;
    }
    setSubmitterNote("");
    openFlag(flag, { celebrate: true });
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
      className={kiosk ? "publicFlagMap isKiosk" : "publicFlagMap"}
      data-kiosk={kiosk ? "1" : "0"}
      data-attract={attracting ? "1" : "0"}
      aria-label="Public Flag Map"
      onKeyDown={onMapKeyDown}
      onContextMenu={kiosk ? (event) => event.preventDefault() : undefined}
    >
      <div className="publicFlagChrome">
        <div className="sectionHeader publicFlagHeading">
          <span className="publicFlagWave" aria-hidden="true" />
          <div>
            <p className="eyebrow">Field</p>
            <h2>Public Flag Map</h2>
            <p id={helpId} className={kiosk ? "helperText srOnly" : "helperText"}>
              Pinch or use the wheel to zoom. Drag to move around the field. Zooming in reveals seals, then names, then ranks.
            </p>
          </div>
        </div>
        <div className="publicFlagSearch">
          <div className="publicFlagSearchTabs" role="tablist" aria-label="Flag search">
            <button type="button" role="tab" aria-selected={searchTab === "honoree"} className={searchTab === "honoree" ? "isActive" : ""} onClick={() => setSearchTab("honoree")}>Find a honoree</button>
            <button type="button" role="tab" aria-selected={searchTab === "submitted"} className={searchTab === "submitted" ? "isActive" : ""} onClick={() => setSearchTab("submitted")}>Flags I submitted</button>
          </div>
          {searchTab === "honoree" ? (
            <>
              <label htmlFor="public-flag-search">Search honorees</label>
              <input
                id="public-flag-search"
                role="combobox"
                aria-expanded={open && suggestions.length > 0}
                aria-controls={listId}
                aria-autocomplete="list"
                aria-activedescendant={open && suggestions[active] ? `${listId}-${suggestions[active].honoreeId}` : undefined}
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
              {open && suggestions.length > 0 ? (
                <ul id={listId} role="listbox" className="publicFlagSuggestions">
                  {suggestions.map((hit, optionIndex) => (
                    <li
                      id={`${listId}-${hit.honoreeId}`}
                      key={`${hit.honoreeId}-${hit.flagGridId}`}
                      role="option"
                      aria-selected={optionIndex === active}
                      className={optionIndex === active ? "isActive" : ""}
                      onMouseDown={(event) => {
                        event.preventDefault();
                        pickSuggestion(hit);
                      }}
                    >
                      <strong>{hit.displayName}</strong>
                      <span>{[hit.serviceBranchName, hit.section, hit.flagGridName].filter(Boolean).join(" · ")}</span>
                    </li>
                  ))}
                </ul>
              ) : null}
            </>
          ) : (
            <form
              className="publicFlagSubmitter"
              onSubmit={(event) => {
                event.preventDefault();
                void runSubmitterLookup();
              }}
            >
              <label htmlFor="submitter-first">First name
                <input
                  id="submitter-first"
                  type="text"
                  inputMode="text"
                  enterKeyHint="next"
                  autoComplete="given-name"
                  autoCapitalize="words"
                  value={submitterFirst}
                  onChange={(event) => setSubmitterFirst(event.target.value)}
                />
              </label>
              <label htmlFor="submitter-last">Last name
                <input
                  id="submitter-last"
                  type="text"
                  inputMode="text"
                  enterKeyHint="search"
                  autoComplete="family-name"
                  autoCapitalize="words"
                  value={submitterLast}
                  onChange={(event) => setSubmitterLast(event.target.value)}
                />
              </label>
              <button type="submit" disabled={submitterBusy}>{submitterBusy ? "Looking…" : "Find flags"}</button>
              {submitterNote ? <p className="message" role="status">{submitterNote}</p> : null}
              {submitterHits.length > 0 ? (
                <ul className="publicFlagSuggestions" aria-label="Flags for that name">
                  {submitterHits.map((hit) => (
                    <li key={`${hit.honoreeId}-${hit.flagGridId}`}>
                      <button type="button" onClick={() => openSubmitterHit(hit)}>
                        <strong>{hit.displayName}</strong>
                        <span>{[hit.serviceBranchName, hit.section, hit.flagGridName].filter(Boolean).join(" · ")}</span>
                      </button>
                    </li>
                  ))}
                </ul>
              ) : null}
            </form>
          )}
        </div>
        <div className="publicFlagToolbar">
          {kiosk ? <button type="button" onClick={goHome} aria-label="Home">Home</button> : null}
          {kiosk ? <button type="button" className="secondary" onClick={goBack} aria-label="Back">Back</button> : null}
          <button type="button" className="secondary" onClick={() => zoomBy(1 / FLAG_MAP_ZOOM.zoomStep)} aria-label="Zoom out">−</button>
          <button type="button" className="secondary" onClick={() => zoomBy(FLAG_MAP_ZOOM.zoomStep)} aria-label="Zoom in">+</button>
          {kiosk ? null : (
            <button type="button" className="secondary" onClick={goHome} aria-label="Reset view">Reset view</button>
          )}
          <p className="publicFlagLevel" aria-live="polite">{levelText}</p>
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
          {visibleSections.map((section) => (
            <button
              key={section.section}
              type="button"
              className="publicFlagTile"
              style={{ left: section.x, top: section.y, width: section.width, height: section.height }}
              aria-label={`${section.section}, ${section.occupiedCount} of ${section.flagCount} flags`}
              onClick={() => acceptClick(() => {
                historyRef.current = [...historyRef.current, { ...viewRef.current }].slice(-16);
                userAdjustedRef.current = true;
                setSelected(null);
                setHighlightId(null);
                setRevealId(null);
                writeFlagParam(null);
                animateTo(fitView(section, viewportSize(), 48));
              })}
            >
              <strong>{section.section}</strong>
              <span>{section.occupiedCount} of {section.flagCount}</span>
            </button>
          ))}
          {visibleFlags.map((flag) => {
            const revealed = flag.flagGridId === revealId;
            const highlighted = flag.flagGridId === highlightId;
            const className = [
              "publicFlagMarker",
              highlighted ? "isHighlighted" : "",
              revealed ? "isRevealed" : ""
            ].filter(Boolean).join(" ");
            return (
              <button
                id={`public-flag-${flag.flagGridId}`}
                key={flag.flagGridId}
                type="button"
                className={className}
                style={{ left: flag.x, top: flag.y, width: flag.width, height: flag.height }}
                aria-current={highlighted ? "true" : undefined}
                aria-label={[flag.flagGridName, flag.displayName || flag.status, flag.rank, flag.serviceBranchName].filter(Boolean).join(", ")}
                onClick={() => acceptClick(() => openFlag(flag))}
              >
                {flag.logoUrl ? <img src={withApiBase(apiBase, flag.logoUrl)} alt="" draggable={false} /> : <span className="publicFlagMarkerStatus">{flag.status === "Reserved" ? "Reserved" : flag.status === "Open" ? "Open" : ""}</span>}
                {gates.names ? <em>{flag.displayName || flag.flagGridName}</em> : null}
                {gates.ranks && flag.rank ? <small>{flag.rank}</small> : null}
              </button>
            );
          })}
        </div>
      </div>
      {selected ? (
        <FlagDetail
          flag={selected}
          revealed={selected.flagGridId === revealId}
          shareUrl={shareUrl}
          onPreview={onPreviewCard}
          onClose={() => {
            setSelected(null);
            setRevealId(null);
            writeFlagParam(null);
          }}
        />
      ) : null}
      </div>
      {kiosk ? (
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
      ) : null}
      {kiosk && pinOpen ? (
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
    </section>
  );
}
