import { useEffect, useId, useLayoutEffect, useMemo, useRef, useState } from "react";
import {
  FLAG_MAP_ZOOM,
  cullRects,
  filterFlagMapSuggestions,
  fitView,
  flagMapDetailGates,
  flagMapLayerOpacities,
  flagYears,
  layoutFlagField,
  panView,
  pointerIsTap,
  publicFlagMapUrls,
  viewportWorldRect,
  withApiBase,
  zoomAt
} from "./publicFlagMap.js";

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

const apiBase = import.meta.env.VITE_API_BASE_URL ?? "";
const urls = publicFlagMapUrls(apiBase);

async function loadJson<T>(url: string): Promise<T> {
  const response = await fetch(url, {
    cache: "no-cache",
    headers: { Accept: "application/json" }
  });
  if (!response.ok) {
    throw new Error(`The flag map could not be loaded (${response.status}).`);
  }
  return response.json() as Promise<T>;
}

function sameIds(left: number[], right: number[]) {
  if (left.length !== right.length) return false;
  for (let index = 0; index < left.length; index += 1) {
    if (left[index] !== right[index]) return false;
  }
  return true;
}

export default function PublicFlagMap({
  onPreviewCard
}: {
  onPreviewCard: (honoreeId: number, title: string) => void;
}) {
  const listId = useId();
  const helpId = useId();
  const viewportRef = useRef<HTMLDivElement | null>(null);
  const layerRef = useRef<HTMLDivElement | null>(null);
  const viewRef = useRef<View>({ x: 0, y: 0, scale: 1 });
  const fitScaleRef = useRef(1);
  const userAdjustedRef = useRef(false);
  const animRef = useRef(0);
  const cullFrameRef = useRef(0);
  const suppressClickRef = useRef(false);
  const layoutRef = useRef<ReturnType<typeof layoutFlagField> | null>(null);
  const [field, setField] = useState<ReturnType<typeof layoutFlagField> | null>(null);
  const [index, setIndex] = useState<SearchHit[]>([]);
  const [query, setQuery] = useState("");
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(true);
  const [visibleSectionKeys, setVisibleSectionKeys] = useState<string[]>([]);
  const [visibleFlagIds, setVisibleFlagIds] = useState<number[]>([]);
  const [gates, setGates] = useState({ seals: false, names: false, ranks: false, sectionsInteractive: true, flagsInteractive: false });
  const [levelText, setLevelText] = useState("Sections");
  const [selected, setSelected] = useState<FlagBox | null>(null);
  const [highlightId, setHighlightId] = useState<number | null>(null);
  const suggestions = filterFlagMapSuggestions(index, query) as SearchHit[];

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

  function viewportSize() {
    const rect = viewportRef.current?.getBoundingClientRect();
    return { width: rect?.width || 1, height: rect?.height || 1 };
  }

  function worldBounds() {
    return { x: 0, y: 0, width: layoutRef.current?.worldWidth || 1, height: layoutRef.current?.worldHeight || 1 };
  }

  function applyView(next: View, cullNow = false) {
    const scale = Math.min(FLAG_MAP_ZOOM.maxScale, Math.max(FLAG_MAP_ZOOM.minScale, next.scale));
    const view = { x: next.x, y: next.y, scale };
    viewRef.current = view;
    const layer = layerRef.current;
    const opacity = flagMapLayerOpacities(view.scale, fitScaleRef.current);
    if (layer) {
      layer.style.transform = `translate(${view.x}px, ${view.y}px) scale(${view.scale})`;
      layer.style.setProperty("--section-opacity", String(opacity.section));
      layer.style.setProperty("--seal-opacity", String(opacity.seal));
      layer.style.setProperty("--name-opacity", String(opacity.name));
      layer.style.setProperty("--rank-opacity", String(opacity.rank));
    }
    const viewport = viewportRef.current;
    if (viewport) {
      viewport.dataset.relative = String(opacity.relative);
      viewport.dataset.sections = opacity.section >= 0.5 ? "on" : "off";
      viewport.dataset.flags = opacity.seal > 0.5 ? "on" : "off";
    }
    publishCull(cullNow);
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
      const sectionKeys = cullRects(layout.sections, seen).map((section: Box) => section.section as string);
      const flagIds = nextGates.seals
        ? cullRects(layout.flags, seen).map((flag: FlagBox) => flag.flagGridId)
        : [];
      setGates((current) => (
        current.seals === nextGates.seals &&
        current.names === nextGates.names &&
        current.ranks === nextGates.ranks &&
        current.sectionsInteractive === nextGates.sectionsInteractive &&
        current.flagsInteractive === nextGates.flagsInteractive
          ? current
          : nextGates
      ));
      setVisibleSectionKeys((current) => (current.join("|") === sectionKeys.join("|") ? current : sectionKeys));
      setVisibleFlagIds((current) => (sameIds(current, flagIds) ? current : flagIds));
      const opacity = flagMapLayerOpacities(view.scale, fitScaleRef.current);
      const nextLevel = opacity.rank >= 0.85 ? "Closest" : opacity.name >= 0.85 ? "Names" : opacity.seal >= 0.85 ? "Seals" : opacity.section >= 0.85 ? "Sections" : "";
      if (nextLevel) setLevelText((current) => (current === nextLevel ? current : nextLevel));
    };
    if (immediate) {
      cancelAnimationFrame(cullFrameRef.current);
      run();
      return;
    }
    if (cullFrameRef.current) return;
    cullFrameRef.current = requestAnimationFrame(run);
  }

  function animateTo(target: View) {
    cancelAnimationFrame(animRef.current);
    const from = { ...viewRef.current };
    const started = performance.now();
    const step = (now: number) => {
      const amount = Math.min(1, (now - started) / 480);
      const eased = 1 - (1 - amount) ** 3;
      applyView({
        x: from.x + (target.x - from.x) * eased,
        y: from.y + (target.y - from.y) * eased,
        scale: from.scale + (target.scale - from.scale) * eased
      }, amount === 1 || amount === 0);
      if (amount < 1) animRef.current = requestAnimationFrame(step);
    };
    animRef.current = requestAnimationFrame(step);
  }

  function fitWorld() {
    const fitted = fitView(worldBounds(), viewportSize());
    fitScaleRef.current = fitted.scale;
    return fitted;
  }

  function zoomBy(factor: number, origin?: { x: number; y: number }) {
    userAdjustedRef.current = true;
    const size = viewportSize();
    const point = origin ?? { x: size.width / 2, y: size.height / 2 };
    animateTo(zoomAt(viewRef.current, point.x, point.y, viewRef.current.scale * factor));
  }

  function openFlag(flag: FlagBox) {
    setHighlightId(flag.flagGridId);
    setSelected(flag.honoreeId ? flag : null);
    const size = viewportSize();
    const scale = Math.min(FLAG_MAP_ZOOM.maxScale, fitScaleRef.current * FLAG_MAP_ZOOM.rankFadeEnd);
    const centerX = flag.x + flag.width / 2;
    const centerY = flag.y + flag.height / 2;
    userAdjustedRef.current = true;
    animateTo({
      scale,
      x: size.width / 2 - centerX * scale,
      y: size.height / 2 - centerY * scale
    });
  }

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
      if (userAdjustedRef.current) return;
      applyView(fitWorld(), true);
    };
    fit();
    const observer = new ResizeObserver(fit);
    observer.observe(node);
    return () => observer.disconnect();
    // applyView is recreated each render and reads the latest refs.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [field]);

  useEffect(() => {
    const node = viewportRef.current;
    if (!node) return;
    let start: { x: number; y: number } | null = null;
    let origin = { x: 0, y: 0 };
    let moved = false;
    let pinching = false;
    let pinchStart = 0;
    let pinchScale = 1;
    let pinchMid = { x: 0, y: 0 };

    const localPoint = (event: { clientX: number; clientY: number }) => {
      const rect = node.getBoundingClientRect();
      return { x: event.clientX - rect.left, y: event.clientY - rect.top };
    };

    const onWheel = (event: WheelEvent) => {
      event.preventDefault();
      userAdjustedRef.current = true;
      const point = localPoint(event);
      const factor = Math.exp(-event.deltaY * 0.0015);
      applyView(zoomAt(viewRef.current, point.x, point.y, viewRef.current.scale * factor));
    };

    const onPointerDown = (event: PointerEvent) => {
      suppressClickRef.current = false;
      if (pinching || event.button > 0) return;
      if ((event.target as HTMLElement | null)?.closest(".publicFlagChrome, .publicFlagDetail")) return;
      start = localPoint(event);
      origin = { x: viewRef.current.x, y: viewRef.current.y };
      moved = false;
      node.setPointerCapture(event.pointerId);
    };

    const onPointerMove = (event: PointerEvent) => {
      if (!start || pinching) return;
      const point = localPoint(event);
      if (!pointerIsTap(start, point)) moved = true;
      if (moved) {
        userAdjustedRef.current = true;
        applyView({ x: origin.x + point.x - start.x, y: origin.y + point.y - start.y, scale: viewRef.current.scale });
      }
    };

    const onPointerUp = (event: PointerEvent) => {
      if (!start) return;
      const point = localPoint(event);
      if (moved || !pointerIsTap(start, point)) suppressClickRef.current = true;
      start = null;
    };

    const touchDistance = (event: TouchEvent) => {
      const [first, second] = [event.touches[0], event.touches[1]];
      return Math.hypot(first.clientX - second.clientX, first.clientY - second.clientY);
    };

    const onTouchStart = (event: TouchEvent) => {
      if (event.touches.length !== 2) return;
      pinching = true;
      pinchStart = touchDistance(event);
      pinchScale = viewRef.current.scale;
      const rect = node.getBoundingClientRect();
      pinchMid = {
        x: (event.touches[0].clientX + event.touches[1].clientX) / 2 - rect.left,
        y: (event.touches[0].clientY + event.touches[1].clientY) / 2 - rect.top
      };
    };

    const onTouchMove = (event: TouchEvent) => {
      if (!pinching || event.touches.length < 2 || pinchStart <= 0) return;
      event.preventDefault();
      userAdjustedRef.current = true;
      const rect = node.getBoundingClientRect();
      const mid = {
        x: (event.touches[0].clientX + event.touches[1].clientX) / 2 - rect.left,
        y: (event.touches[0].clientY + event.touches[1].clientY) / 2 - rect.top
      };
      const zoomed = zoomAt(viewRef.current, pinchMid.x, pinchMid.y, pinchScale * (touchDistance(event) / pinchStart));
      applyView(panView(zoomed, mid.x - pinchMid.x, mid.y - pinchMid.y));
      pinchMid = mid;
    };

    const onTouchEnd = () => {
      pinching = false;
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
    if (flag) openFlag(flag);
  }

  function onMapKeyDown(event: React.KeyboardEvent<HTMLElement>) {
    if (event.target instanceof HTMLInputElement || event.target instanceof HTMLTextAreaElement) return;
    if (event.key === "Escape") {
      setSelected(null);
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

  return (
    <section className="publicFlagMap" aria-label="Public Flag Map" onKeyDown={onMapKeyDown}>
      <div className="publicFlagChrome">
        <div className="sectionHeader">
          <div>
            <p className="eyebrow">Field</p>
            <h2>Public Flag Map</h2>
            <p id={helpId} className="helperText">
              Pinch or use the wheel to zoom. Drag to move around the field. Zooming in reveals seals, then names, then ranks.
            </p>
          </div>
        </div>
        <div className="publicFlagSearch">
          <label htmlFor="public-flag-search">Search honorees</label>
          <input
            id="public-flag-search"
            role="combobox"
            aria-expanded={open && suggestions.length > 0}
            aria-controls={listId}
            aria-autocomplete="list"
            aria-activedescendant={open && suggestions[active] ? `${listId}-${suggestions[active].honoreeId}` : undefined}
            type="search"
            enterKeyHint="search"
            autoComplete="off"
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
        </div>
        <div className="publicFlagToolbar">
          <button type="button" className="secondary" onClick={() => zoomBy(1 / FLAG_MAP_ZOOM.zoomStep)} aria-label="Zoom out">−</button>
          <button type="button" className="secondary" onClick={() => zoomBy(FLAG_MAP_ZOOM.zoomStep)} aria-label="Zoom in">+</button>
          <button type="button" className="secondary" onClick={() => { userAdjustedRef.current = false; animateTo(fitWorld()); }} aria-label="Reset view">Reset view</button>
          <p className="srOnly" aria-live="polite">{levelText}</p>
        </div>
      </div>

      {error ? <p className="message error" role="alert">{error}</p> : null}

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
                userAdjustedRef.current = true;
                setSelected(null);
                animateTo(fitView(section, viewportSize(), 48));
              })}
            >
              <strong>{section.section}</strong>
              <span>{section.occupiedCount} of {section.flagCount}</span>
            </button>
          ))}
          {visibleFlags.map((flag) => (
            <button
              id={`public-flag-${flag.flagGridId}`}
              key={flag.flagGridId}
              type="button"
              className={flag.flagGridId === highlightId ? "publicFlagMarker isHighlighted" : "publicFlagMarker"}
              style={{ left: flag.x, top: flag.y, width: flag.width, height: flag.height }}
              aria-current={flag.flagGridId === highlightId ? "true" : undefined}
              aria-label={[flag.flagGridName, flag.displayName || flag.status, flag.rank, flag.serviceBranchName].filter(Boolean).join(", ")}
              onClick={() => acceptClick(() => openFlag(flag))}
            >
              {flag.logoUrl ? <img src={withApiBase(apiBase, flag.logoUrl)} alt="" /> : <span className="publicFlagMarkerStatus">{flag.status === "Reserved" ? "Reserved" : flag.status === "Open" ? "Open" : ""}</span>}
              {gates.names ? <em>{flag.displayName || flag.flagGridName}</em> : null}
              {gates.ranks && flag.rank ? <small>{flag.rank}</small> : null}
            </button>
          ))}
        </div>
        {selected ? (
          <aside className="publicFlagDetail" aria-label="Flag detail">
            {selected.photoUrl ? <img className="publicFlagPhoto" src={withApiBase(apiBase, selected.photoUrl)} alt="" /> : null}
            <h3>{selected.displayName}</h3>
            {selected.rank ? <p>{selected.rank}</p> : null}
            {selected.serviceBranchName ? (
              <p>
                {selected.logoUrl ? <img src={withApiBase(apiBase, selected.logoUrl)} alt="" /> : null}
                {selected.serviceBranchName}
                {selected.serviceBranchCategoryName ? ` · ${selected.serviceBranchCategoryName}` : ""}
              </p>
            ) : null}
            <p>{selected.flagGridName} · Section {selected.section}</p>
            {flagYears(selected) ? <p>{flagYears(selected)}</p> : null}
            {selected.kia ? <p>Killed in action</p> : null}
            {selected.description ? <p>{selected.description}</p> : null}
            {selected.conflictsServed ? <p>Conflicts served: {selected.conflictsServed}</p> : null}
            {selected.awards ? <p>Awards: {selected.awards}</p> : null}
            {selected.honoreeId ? (
              <button type="button" onClick={() => onPreviewCard(selected.honoreeId!, selected.displayName || "Honoree")}>
                Preview card
              </button>
            ) : null}
            <button type="button" className="secondary" onClick={() => setSelected(null)}>Close</button>
          </aside>
        ) : null}
      </div>
    </section>
  );
}
