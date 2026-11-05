import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import type { KeyboardEvent, MouseEvent, PointerEvent } from "react";
import type { AccountInfo, IPublicClientApplication } from "@azure/msal-browser";
import * as Dialog from "@radix-ui/react-dialog";
import * as Slider from "@radix-ui/react-slider";
import {
  Bar,
  CartesianGrid,
  ComposedChart,
  Customized,
  Line,
  ResponsiveContainer,
  XAxis,
  YAxis
} from "recharts";
import { adminApi, type AdminActivity as AdminActivityData, type ActivityPageViews, type ActivityStatusWindow } from "./api";

const UTC_NOTE = "Counts use UTC days. Last 7 days includes today and the six days before it. Last 30 days includes today and the 29 days before it.";
const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
const NAVY = "#173b73";
const CRIMSON = "#9b1c31";
const INK = "#162033";
const LINE = "#dce6f3";
const RANGES = [
  { id: "14", label: "2 weeks" },
  { id: "42", label: "6 weeks" },
  { id: "all", label: "All" }
] as const;

type RangeId = (typeof RANGES)[number]["id"];
type PresetPick = RangeId | "custom";
type DayPoint = { date: string; count: number; running: number };
type ActivityJump = "/admin/review" | "/admin/flag-map";
type ColumnShape = {
  x?: number;
  y?: number;
  width?: number;
  height?: number;
  index?: number;
  payload?: DayPoint;
  background?: { x?: number; y?: number; width?: number; height?: number };
};

function asOfLabel(value: string) {
  const match = /^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2})/.exec(value || "");
  if (!match) return "";
  return `As of ${match[1]} ${match[2]} UTC.`;
}

function formatDay(iso: string) {
  return `${MONTHS[Number(iso.slice(5, 7)) - 1]} ${Number(iso.slice(8, 10))}`;
}

function rangeForPreset(id: RangeId, length: number): [number, number] {
  const end = Math.max(0, length - 1);
  if (id === "all") return [0, end];
  return [Math.max(0, length - Number(id)), end];
}

function presetFor(start: number, end: number, length: number): RangeId | null {
  if (length <= 0) return null;
  const last = length - 1;
  if (start === 0 && end === last) return "all";
  if (end === last && start === Math.max(0, length - 14)) return "14";
  if (end === last && start === Math.max(0, length - 42)) return "42";
  return null;
}

function registrationSeries(data: AdminActivityData): { date: string; count: number }[] {
  if (Array.isArray(data.registrations) && data.registrations.length > 0) {
    return data.registrations.map((day) => ({ date: day.date, count: day.count }));
  }
  return data.daily.map((day) => ({ date: day.date, count: day.accounts }));
}

function useReducedMotion() {
  const [reduced, setReduced] = useState(() =>
    typeof window !== "undefined" && window.matchMedia("(prefers-reduced-motion: reduce)").matches
  );
  useEffect(() => {
    const media = window.matchMedia("(prefers-reduced-motion: reduce)");
    const apply = () => setReduced(media.matches);
    apply();
    media.addEventListener("change", apply);
    return () => media.removeEventListener("change", apply);
  }, []);
  return reduced;
}

function CountRow({ label, value }: { label: string; value: number }) {
  return (
    <>
      <dt className="col-7 col-md-8">{label}</dt>
      <dd className="col-5 col-md-4 text-end fw-semibold mb-1">{value}</dd>
    </>
  );
}

function StatusWindowView({ label, window }: { label: string; window: ActivityStatusWindow }) {
  return (
    <div className="mb-3">
      <p className="mb-2">{label} total <strong>{window.total}</strong></p>
      {window.byStatus.length > 0 ? (
        <div className="table-responsive w-100">
          <table className="table table-sm mb-0">
            <thead>
              <tr>
                <th scope="col">Status</th>
                <th scope="col" className="text-end">Count</th>
              </tr>
            </thead>
            <tbody>
              {window.byStatus.map((row) => (
                <tr key={`${label}-${row.status}`}>
                  <td>{row.status}</td>
                  <td className="text-end">{row.count}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : null}
    </div>
  );
}

function PageViewsBlock({ pageViews }: { pageViews?: ActivityPageViews | null }) {
  if (!pageViews) return null;
  return (
    <section aria-labelledby="activity-page-views" className="mt-4">
      <h3 id="activity-page-views" className="h5">Page views since Nov 4, 2026</h3>
      <p className="fs-3 fw-semibold mb-2">{pageViews.total}</p>
      <div className="table-responsive w-100">
        <table className="table table-sm">
          <caption>Daily page views</caption>
          <thead>
            <tr>
              <th scope="col">Date</th>
              <th scope="col" className="text-end">Page views</th>
            </tr>
          </thead>
          <tbody>
            {pageViews.daily.map((day) => (
              <tr key={day.date}>
                <td>{day.date}</td>
                <td className="text-end">{day.pageViews}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <div className="table-responsive w-100">
        <table className="table table-sm mb-0">
          <caption>Top pages</caption>
          <thead>
            <tr>
              <th scope="col">Page</th>
              <th scope="col" className="text-end">Page views</th>
            </tr>
          </thead>
          <tbody>
            {pageViews.topPages.map((page) => (
              <tr key={page.name}>
                <td>{page.name}</td>
                <td className="text-end">{page.pageViews}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function FlagMark({ x, y, s }: { x: number; y: number; s: number }) {
  return (
    <g className="activity-flag" aria-hidden="true" pointerEvents="none">
      <line x1={x} y1={y} x2={x} y2={y + s * 1.6} stroke={NAVY} strokeWidth={1.25} />
      <path d={`M${x},${y} h${s} l${-s * 0.22},${s * 0.28} l${s * 0.22},${s * 0.28} h${-s} z`} fill={NAVY} />
    </g>
  );
}

function RunningTotalMark({
  width = 0,
  offset,
  running = 0
}: {
  width?: number;
  offset?: { left?: number; width?: number };
  running?: number;
}) {
  const plotRight = (offset?.left ?? 0) + (offset?.width ?? Math.max(0, width - (offset?.left ?? 0)));
  if (plotRight <= 0) return null;
  return (
    <text x={plotRight - 4} y={18} textAnchor="end" fontSize={13} fill={INK}>
      {`Running total: ${running}`}
    </text>
  );
}

function DayColumn({
  shape,
  active,
  tabbable,
  onHover,
  onLeave,
  onFocusDay,
  onOpen,
  onKeyDown
}: {
  shape: ColumnShape;
  active: boolean;
  tabbable: boolean;
  onHover: (index: number) => void;
  onLeave: () => void;
  onFocusDay: (index: number) => void;
  onOpen: (day: DayPoint, index: number) => void;
  onKeyDown: (event: KeyboardEvent<SVGGElement>, index: number) => void;
}) {
  const day = shape.payload;
  if (!day || shape.index == null) return null;
  const index = shape.index;
  const x = shape.x ?? 0;
  const y = shape.y ?? 0;
  const width = shape.width ?? 0;
  const height = shape.height ?? 0;
  const hitX = shape.background?.x ?? x;
  const hitY = shape.background?.y ?? y;
  const hitW = shape.background?.width ?? width;
  const hitH = shape.background?.height ?? height;
  const barH = Math.abs(height);
  const barY = height < 0 ? y + height : y;
  const showFlag = day.count > 0 && hitW >= 16 && hitH >= 28;
  const flagSize = Math.min(16, hitW * 0.55);
  const label = `${formatDay(day.date)}: ${day.count} new, ${day.running} in total`;
  return (
    <g
      data-activity-day="true"
      role="button"
      tabIndex={tabbable ? 0 : -1}
      aria-label={label}
      onMouseEnter={() => onHover(index)}
      onMouseLeave={onLeave}
      onFocus={() => onFocusDay(index)}
      onClick={() => onOpen(day, index)}
      onKeyDown={(event) => onKeyDown(event, index)}
    >
      <rect
        className="activity-day-hit"
        x={hitX}
        y={hitY}
        width={Math.max(hitW, 0)}
        height={Math.max(hitH, 0)}
        fill={active ? "rgba(23, 59, 115, 0.08)" : "rgba(23, 59, 115, 0)"}
        pointerEvents="all"
      />
      {barH > 0 && width > 0 ? (
        <rect x={x} y={barY} width={width} height={barH} fill={NAVY} pointerEvents="none" />
      ) : null}
      {showFlag ? <FlagMark x={hitX + hitW / 2} y={hitY + hitH - flagSize * 1.65} s={flagSize} /> : null}
    </g>
  );
}

export function AdminActivity({
  instance,
  account,
  onNavigate
}: {
  instance: IPublicClientApplication;
  account: AccountInfo | null;
  onNavigate: (route: ActivityJump) => void;
}) {
  const [data, setData] = useState<AdminActivityData | null>(null);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(true);
  const [preset, setPreset] = useState<PresetPick>("42");
  const [range, setRange] = useState<[number, number] | null>(null);
  const [hover, setHover] = useState<number | null>(null);
  const [pinned, setPinned] = useState<number | null>(null);
  const [openDay, setOpenDay] = useState<DayPoint | null>(null);
  const [compact, setCompact] = useState(false);
  const reducedMotion = useReducedMotion();
  const chartRef = useRef<HTMLDivElement>(null);
  const keyboardOwns = useRef(false);
  const lastPointer = useRef<{ x: number; y: number } | null>(null);

  useEffect(() => {
    if (!account) {
      setLoading(false);
      return;
    }

    let cancelled = false;
    adminApi.activity(instance, account)
      .then((activity) => {
        if (cancelled) return;
        if (!activity?.accounts || !activity.changeRequests || !activity.flagClaims || !activity.receipts || !Array.isArray(activity.daily)) {
          setError("Activity could not be loaded.");
          return;
        }
        setData(activity);
      })
      .catch(() => {
        if (!cancelled) setError("Activity could not be loaded.");
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [instance, account, account?.homeAccountId]);

  useEffect(() => {
    const media = window.matchMedia("(max-width: 575.98px)");
    const apply = () => setCompact(media.matches);
    apply();
    media.addEventListener("change", apply);
    return () => media.removeEventListener("change", apply);
  }, []);

  const series = useMemo(() => {
    if (!data) return [] as DayPoint[];
    const days = registrationSeries(data);
    const counted = days.reduce((sum, day) => sum + day.count, 0);
    let running = Math.max(0, data.accounts.allTime - counted);
    return days.map((day) => {
      running += day.count;
      return { date: day.date, count: day.count, running };
    });
  }, [data]);

  if (series.length > 0 && range === null) {
    setRange(rangeForPreset("42", series.length));
  }

  const visible = useMemo(() => {
    if (!range || series.length === 0) return [] as DayPoint[];
    const start = Math.max(0, Math.min(range[0], series.length - 1));
    const end = Math.max(start, Math.min(range[1], series.length - 1));
    return series.slice(start, end + 1);
  }, [series, range]);

  const headlines = useMemo(() => {
    const runningTotal = visible.length ? visible[visible.length - 1].running : 0;
    const newAccounts = visible.reduce((sum, day) => sum + day.count, 0);
    const daily = new Map((data?.daily ?? []).map((row) => [row.date, row]));
    const firstDaily = data?.daily[0]?.date ?? "";
    let claims = 0;
    let changes = 0;
    let receipts = 0;
    let partial = false;
    for (const day of visible) {
      if (firstDaily && day.date < firstDaily) {
        partial = true;
        continue;
      }
      const row = daily.get(day.date);
      if (!row) continue;
      claims += row.flagClaims;
      changes += row.changeRequests;
      receipts += row.receipts;
    }
    return { runningTotal, newAccounts, claims, changes, receipts, partial };
  }, [visible, data]);

  const activeIndex = hover ?? pinned ?? (visible.length > 0 ? visible.length - 1 : null);
  const activeDay = activeIndex != null && activeIndex >= 0 && activeIndex < visible.length ? visible[activeIndex] : null;
  const tabbable = pinned ?? (visible.length > 0 ? visible.length - 1 : 0);
  const stamp = data ? asOfLabel(data.asOfUtc) : "";
  const maxCount = Math.max(1, ...visible.map((day) => day.count));
  const runValues = visible.map((day) => day.running);
  const runLo = runValues.length ? Math.min(...runValues) : 0;
  const runHi = runValues.length ? Math.max(...runValues) : 1;
  const runDomain: [number, number] = [Math.max(0, runLo - 1), runHi + 1];
  const tickEvery = Math.max(1, Math.ceil(visible.length / 6));
  const openDaily = openDay && data ? data.daily.find((row) => row.date === openDay.date) ?? null : null;
  const openPageViews = openDay && data?.pageViews
    ? data.pageViews.daily.find((row) => row.date === openDay.date)?.pageViews
    : undefined;

  useLayoutEffect(() => {
    if (!keyboardOwns.current || pinned == null) return;
    const root = chartRef.current;
    if (!root) return;
    const current = document.activeElement;
    if (current && current !== document.body && !root.contains(current)) return;
    const target = root.querySelectorAll<SVGGElement>("[data-activity-day]")[pinned];
    target?.focus();
  }, [pinned, visible]);

  function applyPreset(id: RangeId) {
    if (series.length === 0) return;
    setPreset(id);
    setRange(rangeForPreset(id, series.length));
    setHover(null);
    setPinned(null);
    keyboardOwns.current = false;
  }

  function onRange(next: number[]) {
    if (series.length === 0) return;
    const start = Math.max(0, Math.min(next[0] ?? 0, next[1] ?? 0));
    const end = Math.min(series.length - 1, Math.max(next[0] ?? 0, next[1] ?? 0));
    setRange([start, end]);
    setPreset(presetFor(start, end, series.length) ?? "custom");
    setHover(null);
    setPinned(null);
    keyboardOwns.current = false;
  }

  function onHover(index: number) {
    if (keyboardOwns.current) return;
    setHover(index);
  }

  function onChartPointerMove(event: PointerEvent<HTMLDivElement>) {
    const next = { x: event.clientX, y: event.clientY };
    const prev = lastPointer.current;
    lastPointer.current = next;
    if (!prev) return;
    if (Math.hypot(next.x - prev.x, next.y - prev.y) < 2) return;
    keyboardOwns.current = false;
  }

  function moveDay(event: KeyboardEvent<SVGGElement>, index: number) {
    if (event.key !== "ArrowRight" && event.key !== "ArrowLeft" && event.key !== "Home" && event.key !== "End") return;
    event.preventDefault();
    const last = visible.length - 1;
    const next = event.key === "ArrowRight" ? Math.min(last, index + 1)
      : event.key === "ArrowLeft" ? Math.max(0, index - 1)
        : event.key === "Home" ? 0 : last;
    keyboardOwns.current = true;
    setHover(null);
    setPinned(next);
  }

  function onDayKeyDown(event: KeyboardEvent<SVGGElement>, index: number) {
    if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      const day = visible[index];
      if (!day) return;
      setPinned(index);
      setOpenDay(day);
      return;
    }
    moveDay(event, index);
  }

  function openColumn(day: DayPoint, index: number) {
    setPinned(index);
    setHover(null);
    setOpenDay(day);
  }

  function jump(event: MouseEvent<HTMLAnchorElement>, route: ActivityJump) {
    event.preventDefault();
    onNavigate(route);
  }

  const rangeStart = visible[0];
  const rangeEnd = visible[visible.length - 1];

  return (
    <section id="activity" className="card card-body" aria-labelledby="activity-heading">
      <h2 id="activity-heading">Field of Honor Activity</h2>
      <p className="small mb-3">
        {UTC_NOTE}
        {stamp ? ` ${stamp}` : ""}
      </p>
      {error ? <p className="alert alert-danger" role="alert">{error}</p> : null}
      {loading ? <p>Loading activity.</p> : null}
      {data ? (
        <>
          <div className="row row-cols-2 row-cols-lg-5 g-2 mb-2">
            {[
              { label: "Accounts in total", value: headlines.runningTotal },
              { label: "New accounts in range", value: headlines.newAccounts },
              { label: "Flag claims", value: headlines.claims },
              { label: "Change requests", value: headlines.changes },
              { label: "Receipts", value: headlines.receipts }
            ].map((item) => (
              <div className="col" key={item.label} style={{ minWidth: 0 }}>
                <div className="border rounded p-2 h-100 bg-white">
                  <div className="fs-4 fw-semibold">{item.value}</div>
                  <div className="small">{item.label}</div>
                </div>
              </div>
            ))}
          </div>
          <p className="small mb-4">
            These counts follow the selected dates. Accounts in total is the running total on the last selected day.
            {headlines.partial ? " Flag claims, change requests, and receipts include only the last 30 UTC days inside this range." : ""}
          </p>

          <section aria-labelledby="activity-accounts" className="mb-4">
            <h3 id="activity-accounts" className="h5">New accounts</h3>
            <p className="mb-2">{data.accounts.allTime} accounts in total.</p>
            <div className="d-flex flex-wrap gap-2" role="group" aria-label="Date range">
              {RANGES.map((item) => (
                <button
                  key={item.id}
                  type="button"
                  className={preset === item.id ? "btn btn-primary" : "btn btn-outline-secondary"}
                  aria-pressed={preset === item.id}
                  onClick={() => applyPreset(item.id)}
                >
                  {item.label}
                </button>
              ))}
            </div>
            {series.length > 1 && range ? (
              <div className="mt-3" role="group" aria-labelledby="activity-range-label">
                <p id="activity-range-label" className="mb-1">
                  {rangeStart && rangeEnd ? `${formatDay(rangeStart.date)} to ${formatDay(rangeEnd.date)}` : ""}
                </p>
                <Slider.Root
                  className="position-relative d-flex align-items-center w-100 user-select-none"
                  style={{ height: "2.75rem", touchAction: "none" }}
                  min={0}
                  max={series.length - 1}
                  step={1}
                  minStepsBetweenThumbs={0}
                  value={range}
                  onValueChange={onRange}
                >
                  <Slider.Track
                    className="position-relative flex-grow-1 rounded-pill bg-body-secondary"
                    style={{ height: "0.5rem" }}
                  >
                    <Slider.Range className="position-absolute top-0 h-100 rounded-pill bg-primary" />
                  </Slider.Track>
                  <Slider.Thumb
                    className="d-block rounded-circle bg-primary border border-2 border-white shadow"
                    style={{ width: "1.25rem", height: "1.25rem" }}
                    aria-label="From"
                    aria-valuetext={series[range[0]] ? formatDay(series[range[0]].date) : undefined}
                  />
                  <Slider.Thumb
                    className="d-block rounded-circle bg-primary border border-2 border-white shadow"
                    style={{ width: "1.25rem", height: "1.25rem" }}
                    aria-label="To"
                    aria-valuetext={series[range[1]] ? formatDay(series[range[1]].date) : undefined}
                  />
                </Slider.Root>
              </div>
            ) : null}
            <p id="activity-day-detail" className="mt-3 mb-2" aria-live="polite">
              {activeDay
                ? `${formatDay(activeDay.date)}: ${activeDay.count} new, ${activeDay.running} in total.`
                : "Hover or tap a day for new accounts and the running total."}
            </p>
            <p id="activity-chart-desc" className="visually-hidden">
              Columns show new accounts for each UTC day and fill the width of the chart. The line is the running total.
            </p>
            {series.length === 0 ? <p>No accounts have been created yet.</p> : visible.length > 0 ? (
              <div
                id="activity-chart"
                ref={chartRef}
                className="w-100 min-w-0"
                style={{ height: compact ? 320 : 280 }}
                data-chart-motion={reducedMotion ? "off" : "on"}
                role="group"
                aria-labelledby="activity-accounts"
                aria-describedby="activity-chart-desc"
                onPointerMove={onChartPointerMove}
              >
                <ResponsiveContainer width="100%" height="100%">
                  <ComposedChart
                    data={visible}
                    margin={{ top: 28, right: 8, left: 0, bottom: 0 }}
                    barCategoryGap="0%"
                    barGap={0}
                  >
                    <CartesianGrid vertical={false} stroke={LINE} />
                    <XAxis
                      dataKey="date"
                      tickFormatter={formatDay}
                      interval={tickEvery - 1}
                      tick={{ fontSize: 12, fill: INK }}
                      stroke={LINE}
                      tickMargin={8}
                    />
                    <YAxis
                      yAxisId="count"
                      domain={[0, maxCount]}
                      allowDecimals={false}
                      width={36}
                      tick={{ fontSize: 12, fill: INK }}
                      stroke={LINE}
                    />
                    <YAxis
                      yAxisId="run"
                      orientation="right"
                      domain={runDomain}
                      allowDecimals={false}
                      width={40}
                      tick={{ fontSize: 12, fill: INK }}
                      stroke={LINE}
                    />
                    <Bar
                      yAxisId="count"
                      dataKey="count"
                      fill={NAVY}
                      isAnimationActive={!reducedMotion}
                      animationDuration={reducedMotion ? 0 : 280}
                      background={{ fill: "transparent" }}
                      shape={(raw: unknown) => {
                        const column = raw as ColumnShape;
                        return (
                          <DayColumn
                            shape={column}
                            active={column.index === activeIndex}
                            tabbable={column.index === tabbable}
                            onHover={onHover}
                            onLeave={() => {
                              if (!keyboardOwns.current) setHover(null);
                            }}
                            onFocusDay={(index) => {
                            if (keyboardOwns.current) return;
                            setPinned(index);
                          }}
                            onOpen={openColumn}
                            onKeyDown={onDayKeyDown}
                          />
                        );
                      }}
                    />
                    <Line
                      yAxisId="run"
                      type="linear"
                      dataKey="running"
                      stroke={CRIMSON}
                      strokeWidth={2.5}
                      dot={false}
                      activeDot={false}
                      isAnimationActive={!reducedMotion}
                      animationDuration={reducedMotion ? 0 : 280}
                      pointerEvents="none"
                    />
                    <Customized component={RunningTotalMark} running={headlines.runningTotal} />
                  </ComposedChart>
                </ResponsiveContainer>
              </div>
            ) : null}
            <div className="visually-hidden">
              <table>
                <caption>New accounts by UTC day</caption>
                <thead>
                  <tr>
                    <th scope="col">Date</th>
                    <th scope="col">New accounts</th>
                    <th scope="col">Running total</th>
                  </tr>
                </thead>
                <tbody>
                  {visible.map((day) => (
                    <tr key={`reg-${day.date}`}>
                      <td>{day.date}</td>
                      <td>{day.count}</td>
                      <td>{day.running}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>

          <section aria-labelledby="activity-requests" className="mb-4">
            <h3 id="activity-requests" className="h5">Change requests</h3>
            <StatusWindowView label="Last 7 days" window={data.changeRequests.last7Days} />
            <StatusWindowView label="Last 30 days" window={data.changeRequests.last30Days} />
          </section>

          <section aria-labelledby="activity-claims" className="mb-4">
            <h3 id="activity-claims" className="h5">Flag claims</h3>
            <StatusWindowView label="Last 7 days" window={data.flagClaims.last7Days} />
            <StatusWindowView label="Last 30 days" window={data.flagClaims.last30Days} />
          </section>

          <section aria-labelledby="activity-receipts" className="mb-4">
            <h3 id="activity-receipts" className="h5">Submission receipts</h3>
            <dl className="row mb-0">
              <CountRow label="Last 7 days" value={data.receipts.last7Days} />
              <CountRow label="Last 30 days" value={data.receipts.last30Days} />
            </dl>
          </section>

          <PageViewsBlock pageViews={data.pageViews} />

          <div className="visually-hidden">
            <table>
              <caption>Last 30 UTC days</caption>
              <thead>
                <tr>
                  <th scope="col">Date</th>
                  <th scope="col">Accounts</th>
                  <th scope="col">Change requests</th>
                  <th scope="col">Flag claims</th>
                  <th scope="col">Receipts</th>
                </tr>
              </thead>
              <tbody>
                {data.daily.map((day) => (
                  <tr key={day.date}>
                    <td>{day.date}</td>
                    <td>{day.accounts}</td>
                    <td>{day.changeRequests}</td>
                    <td>{day.flagClaims}</td>
                    <td>{day.receipts}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <Dialog.Root open={openDay !== null} onOpenChange={(open) => { if (!open) setOpenDay(null); }}>
            <Dialog.Portal>
              <Dialog.Overlay className="offcanvas-backdrop fade show" />
              <Dialog.Content className="offcanvas offcanvas-end show">
                {openDay ? (
                  <>
                    <div className="offcanvas-header">
                      <Dialog.Title className="offcanvas-title h5 mb-0">{formatDay(openDay.date)}</Dialog.Title>
                      <Dialog.Close asChild>
                        <button type="button" className="btn-close" aria-label="Close" />
                      </Dialog.Close>
                    </div>
                    <div className="offcanvas-body">
                      <Dialog.Description className="mb-3">
                        {`${openDay.date} UTC. ${openDay.count} new accounts. ${openDay.running} in total.`}
                      </Dialog.Description>
                      <dl className="row mb-3">
                        <CountRow label="New accounts" value={openDay.count} />
                        <CountRow label="Running total" value={openDay.running} />
                        {openDaily ? (
                          <>
                            <CountRow label="Change requests" value={openDaily.changeRequests} />
                            <CountRow label="Flag claims" value={openDaily.flagClaims} />
                            <CountRow label="Receipts" value={openDaily.receipts} />
                          </>
                        ) : null}
                        {openPageViews != null ? <CountRow label="Page views" value={openPageViews} /> : null}
                      </dl>
                      {openDaily ? null : (
                        <p className="small">Change requests, flag claims, and receipts for this day are outside the last 30 UTC days.</p>
                      )}
                      <p className="mb-2">Open the matching admin screen.</p>
                      <div className="d-flex flex-wrap gap-3">
                        <a className="link-primary" href="/admin/review" onClick={(event) => jump(event, "/admin/review")}>Review</a>
                        <a className="link-primary" href="/admin/flag-map" onClick={(event) => jump(event, "/admin/flag-map")}>Flag Manager</a>
                      </div>
                    </div>
                  </>
                ) : null}
              </Dialog.Content>
            </Dialog.Portal>
          </Dialog.Root>
        </>
      ) : null}
    </section>
  );
}
