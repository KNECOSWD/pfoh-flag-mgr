import { useEffect, useId, useRef, useState } from "react";
import { filterFlagMapSuggestions, flagYears, publicFlagMapUrls, withApiBase } from "./publicFlagMap.js";

type SectionSummary = {
  section: string;
  flagCount: number;
  occupiedCount: number;
};

type PublicSeat = {
  flagGridId: number;
  flagGridName: string;
  section: string;
  columnNumber?: number | null;
  status: "Occupied" | "Open" | "Reserved" | string;
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
  logoUrl?: string | null;
};

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

export default function PublicFlagMap({
  onPreviewCard
}: {
  onPreviewCard: (honoreeId: number, title: string) => void;
}) {
  const listId = useId();
  const boardRef = useRef<HTMLDivElement | null>(null);
  const scaleRef = useRef(1);
  const [sections, setSections] = useState<SectionSummary[]>([]);
  const [index, setIndex] = useState<SearchHit[]>([]);
  const [section, setSection] = useState("");
  const [seats, setSeats] = useState<PublicSeat[]>([]);
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const [query, setQuery] = useState("");
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(true);
  const [scale, setScale] = useState(1);
  const suggestions = filterFlagMapSuggestions(index, query) as SearchHit[];
  const selected = seats.find((seat) => seat.flagGridId === selectedId && seat.honoreeId) ?? null;

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    Promise.all([
      loadJson<SectionSummary[]>(urls.sections),
      loadJson<SearchHit[]>(urls.searchIndex)
    ])
      .then(([nextSections, nextIndex]) => {
        if (cancelled) return;
        setSections(nextSections);
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

  useEffect(() => {
    const node = boardRef.current;
    if (!node) return;
    let startDistance = 0;
    let startScale = 1;

    const distance = (event: TouchEvent) => {
      const [first, second] = [event.touches[0], event.touches[1]];
      return Math.hypot(first.clientX - second.clientX, first.clientY - second.clientY);
    };

    const start = (event: TouchEvent) => {
      if (event.touches.length === 2) {
        startDistance = distance(event);
        startScale = scaleRef.current;
      }
    };
    const move = (event: TouchEvent) => {
      if (event.touches.length !== 2 || startDistance <= 0) return;
      event.preventDefault();
      const next = Math.min(2.5, Math.max(1, startScale * (distance(event) / startDistance)));
      scaleRef.current = next;
      setScale(next);
    };
    const end = () => {
      startDistance = 0;
    };

    node.addEventListener("touchstart", start, { passive: true });
    node.addEventListener("touchmove", move, { passive: false });
    node.addEventListener("touchend", end);
    node.addEventListener("touchcancel", end);
    return () => {
      node.removeEventListener("touchstart", start);
      node.removeEventListener("touchmove", move);
      node.removeEventListener("touchend", end);
      node.removeEventListener("touchcancel", end);
    };
  }, [section]);

  useEffect(() => {
    if (!selectedId) return;
    document.getElementById(`public-flag-${selectedId}`)?.scrollIntoView({ block: "nearest", inline: "nearest" });
  }, [selectedId, seats]);

  async function openSection(nextSection: string, highlightId?: number) {
    setError("");
    setLoading(true);
    try {
      const payload = await loadJson<{ section: string; seats: PublicSeat[] }>(urls.section(nextSection));
      setSection(payload.section);
      setSeats(payload.seats);
      setSelectedId(highlightId ?? null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "That section could not be loaded.");
    } finally {
      setLoading(false);
    }
  }

  function pickSuggestion(hit: SearchHit) {
    setQuery(hit.displayName);
    setOpen(false);
    void openSection(hit.section, hit.flagGridId);
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

  return (
    <section className="publicFlagMap" aria-label="Public Flag Map">
      <div className="sectionHeader">
        <div>
          <p className="eyebrow">Field</p>
          <h2>Public Flag Map</h2>
          <p className="helperText">Choose a section, or search for an honoree. The map lists approved flags only.</p>
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
                <span>
                  {[hit.serviceBranchName, hit.section, hit.flagGridName].filter(Boolean).join(" · ")}
                </span>
              </li>
            ))}
          </ul>
        ) : null}
      </div>

      {error ? <p className="message error" role="alert">{error}</p> : null}
      {loading ? <p className="helperText">Loading the flag map…</p> : null}

      {!section ? (
        <div className="publicFlagSections" aria-label="Field sections">
          {sections.map((item) => (
            <button key={item.section} type="button" className="publicFlagSection" onClick={() => void openSection(item.section)}>
              <strong>{item.section}</strong>
              <span>{item.occupiedCount} of {item.flagCount} flags</span>
            </button>
          ))}
        </div>
      ) : (
        <div className="publicFlagLayout">
          <div>
            <div className="publicFlagToolbar">
              <button type="button" className="secondary" onClick={() => { setSection(""); setSeats([]); setSelectedId(null); }}>
                All sections
              </button>
              <p className="helperText">Section {section}</p>
              <button type="button" className="secondary" onClick={() => setScale((current) => { const next = Math.max(1, current - 0.25); scaleRef.current = next; return next; })} aria-label="Zoom out">
                −
              </button>
              <button type="button" className="secondary" onClick={() => setScale((current) => { const next = Math.min(2.5, current + 0.25); scaleRef.current = next; return next; })} aria-label="Zoom in">
                +
              </button>
            </div>
            <div className="publicFlagBoard" ref={boardRef} style={{ transform: `scale(${scale})` }}>
              <div className="publicFlagSeats" role="grid" aria-label={`Section ${section}`}>
                {seats.map((seat) => (
                  <button
                    id={`public-flag-${seat.flagGridId}`}
                    key={seat.flagGridId}
                    type="button"
                    role="gridcell"
                    className={[
                      "publicFlagSeat",
                      seat.status === "Open" ? "isOpen" : "",
                      seat.status === "Reserved" ? "isReserved" : "",
                      seat.flagGridId === selectedId ? "isHighlighted" : ""
                    ].filter(Boolean).join(" ")}
                    aria-current={seat.flagGridId === selectedId ? "true" : undefined}
                    onClick={() => setSelectedId(seat.flagGridId)}
                  >
                    <strong>{seat.flagGridName}</strong>
                    {seat.status === "Occupied" ? (
                      <>
                        <em>{seat.displayName}</em>
                        {seat.rank ? <small>{seat.rank}</small> : null}
                        {seat.serviceBranchName ? (
                          <small>
                            {seat.logoUrl ? <img src={withApiBase(apiBase, seat.logoUrl)} alt="" /> : null}
                            {seat.serviceBranchName}
                          </small>
                        ) : null}
                      </>
                    ) : (
                      <em>{seat.status === "Reserved" ? "Reserved" : "Open"}</em>
                    )}
                  </button>
                ))}
              </div>
            </div>
          </div>

          {selected ? (
            <aside className="publicFlagDetail" aria-label="Flag detail">
              {selected.photoUrl ? (
                <img className="publicFlagPhoto" src={withApiBase(apiBase, selected.photoUrl)} alt="" />
              ) : null}
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
            </aside>
          ) : (
            <p className="helperText">Select a flag to see the honoree.</p>
          )}
        </div>
      )}
    </section>
  );
}
