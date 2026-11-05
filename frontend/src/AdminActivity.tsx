import { useEffect, useMemo, useState } from "react";
import type { KeyboardEvent } from "react";
import type { AccountInfo, IPublicClientApplication } from "@azure/msal-browser";
import { adminApi, type AdminActivity as AdminActivityData, type ActivityPageViews, type ActivityStatusWindow } from "./api";

const UTC_NOTE = "Counts use UTC days. Last 7 days includes today and the six days before it. Last 30 days includes today and the 29 days before it.";
const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
const NAVY = "#173b73";
const CRIMSON = "#9b1c31";
const INK = "#162033";
const RANGES = [
  { id: "14", label: "2 weeks" },
  { id: "42", label: "6 weeks" },
  { id: "all", label: "All" }
] as const;

type RangeId = (typeof RANGES)[number]["id"];
type DayPoint = { date: string; count: number; running: number };

function asOfLabel(value: string) {
  const match = /^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2})/.exec(value || "");
  if (!match) return "";
  return `As of ${match[1]} ${match[2]} UTC.`;
}

function formatDay(iso: string) {
  return `${MONTHS[Number(iso.slice(5, 7)) - 1]} ${Number(iso.slice(8, 10))}`;
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

function registrationSeries(data: AdminActivityData): { date: string; count: number }[] {
  if (Array.isArray(data.registrations) && data.registrations.length > 0) {
    return data.registrations.map((day) => ({ date: day.date, count: day.count }));
  }
  return data.daily.map((day) => ({ date: day.date, count: day.accounts }));
}

function FlagMark({ x, y, s }: { x: number; y: number; s: number }) {
  return (
    <g>
      <line x1={x} y1={y} x2={x} y2={y + s * 2} stroke={NAVY} strokeWidth={0.8} opacity={0.7} />
      <path
        className="activity-flag"
        d={`M${x},${y} q${s * 0.5},${-s * 0.2} ${s},0 t${s * 0.4},0 v${s * 0.75} q${-s * 0.5},${s * 0.2} ${-s},0 t${-s * 0.4},0 z`}
        fill={NAVY}
      />
    </g>
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

export function AdminActivity({
  instance,
  account
}: {
  instance: IPublicClientApplication;
  account: AccountInfo | null;
}) {
  const [data, setData] = useState<AdminActivityData | null>(null);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(true);
  const [span, setSpan] = useState<RangeId>("42");
  const [hover, setHover] = useState<number | null>(null);
  const [pinned, setPinned] = useState<number | null>(null);

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

  const series = useMemo(() => {
    if (!data) return [] as DayPoint[];
    let running = 0;
    return registrationSeries(data).map((day) => {
      running += day.count;
      return { date: day.date, count: day.count, running };
    });
  }, [data]);

  const visible = useMemo(() => {
    if (span === "all") return series;
    return series.slice(Math.max(0, series.length - Number(span)));
  }, [series, span]);

  const active = hover ?? pinned;
  const activeDay = active != null && active >= 0 && active < visible.length ? visible[active] : null;
  const stamp = data ? asOfLabel(data.asOfUtc) : "";

  const chart = useMemo(() => {
    const W = 1200;
    const H = 280;
    const padB = 36;
    const top = 22;
    const padX = 8;
    const plotH = H - padB - top;
    const maxDay = Math.max(1, ...visible.map((day) => day.count));
    const maxRun = Math.max(1, visible.length ? visible[visible.length - 1].running : 1);
    const cw = (W - padX * 2) / Math.max(1, visible.length);
    const rowH = Math.min(14, plotH / maxDay);
    const fs = Math.max(2, Math.min(8, cw * 0.45, rowH * 0.65));
    const tickEvery = Math.max(1, Math.ceil(visible.length / 6));
    const yOf = (running: number) => H - padB - (running / maxRun) * plotH;
    const line = visible.map((day, index) => `${padX + index * cw + cw / 2},${yOf(day.running)}`).join(" ");
    return { W, H, padB, top, padX, cw, rowH, fs, tickEvery, line };
  }, [visible]);

  function moveDay(event: KeyboardEvent<SVGGElement>, index: number) {
    if (event.key !== "ArrowRight" && event.key !== "ArrowLeft" && event.key !== "Home" && event.key !== "End") return;
    event.preventDefault();
    const last = visible.length - 1;
    const next = event.key === "ArrowRight" ? Math.min(last, index + 1)
      : event.key === "ArrowLeft" ? Math.max(0, index - 1)
        : event.key === "Home" ? 0 : last;
    setPinned(next);
    setHover(null);
    const buttons = event.currentTarget.parentElement?.querySelectorAll<SVGGElement>("[data-activity-day]");
    buttons?.[next]?.focus();
  }

  return (
    <section id="activity" className="card card-body" aria-labelledby="activity-heading">
      <style>{`
        .activity-flag {
          transform-box: fill-box;
          transform-origin: left center;
          animation: activity-flag-wave 2.4s ease-in-out infinite;
        }
        @keyframes activity-flag-wave {
          0%, 100% { transform: skewX(0deg); }
          50% { transform: skewX(-8deg); }
        }
        @media (prefers-reduced-motion: reduce) {
          .activity-flag { animation: none; }
        }
        .activity-day:focus-visible .activity-day-hit {
          stroke: #173b73;
          stroke-width: 2;
        }
      `}</style>
      <h2 id="activity-heading">Field of Honor Activity</h2>
      <p className="small mb-3">
        {UTC_NOTE}
        {stamp ? ` ${stamp}` : ""}
      </p>
      {error ? <p className="alert alert-danger" role="alert">{error}</p> : null}
      {loading ? <p>Loading activity.</p> : null}
      {data ? (
        <>
          <div className="row row-cols-2 row-cols-lg-5 g-2 mb-4">
            {[
              { label: "Accounts in total", value: data.accounts.allTime },
              { label: "New accounts in 7 days", value: data.accounts.last7Days },
              { label: "Flag claims", value: data.flagClaims.last7Days.total },
              { label: "Change requests", value: data.changeRequests.last7Days.total },
              { label: "Receipts", value: data.receipts.last7Days }
            ].map((item) => (
              <div className="col" key={item.label} style={{ minWidth: 0 }}>
                <div className="border rounded p-2 h-100 bg-white">
                  <div className="fs-4 fw-semibold">{item.value}</div>
                  <div className="small">{item.label}</div>
                </div>
              </div>
            ))}
          </div>

          <section aria-labelledby="activity-accounts" className="mb-4">
            <h3 id="activity-accounts" className="h5">New accounts</h3>
            <p className="mb-2">{data.accounts.allTime} accounts in total.</p>
            <div className="d-flex flex-wrap gap-2" role="group" aria-label="Date range">
              {RANGES.map((range) => (
                <button
                  key={range.id}
                  type="button"
                  className={span === range.id ? "btn btn-primary" : "btn btn-outline-secondary"}
                  aria-pressed={span === range.id}
                  onClick={() => {
                    setSpan(range.id);
                    setHover(null);
                    setPinned(null);
                  }}
                >
                  {range.label}
                </button>
              ))}
            </div>
            <p id="activity-day-detail" className="mt-3 mb-2" aria-live="polite">
              {activeDay
                ? `${formatDay(activeDay.date)}: ${activeDay.count} new, ${activeDay.running} in total.`
                : "Hover or tap a day for new accounts and the running total."}
            </p>
            <p id="activity-chart-desc" className="visually-hidden">
              Each flag is one new account. The line is the running total. Flags stay still when reduced motion is on.
            </p>
            {visible.length === 0 ? <p>No accounts have been created yet.</p> : (
              <div className="overflow-hidden">
              <svg
                role="group"
                aria-labelledby="activity-accounts"
                aria-describedby="activity-chart-desc"
                viewBox={`0 0 ${chart.W} ${chart.H}`}
                className="d-block"
                style={{ width: "100%", minWidth: 0, maxWidth: "100%" }}
              >
                <line x1={chart.padX} x2={chart.W - chart.padX} y1={chart.H - chart.padB} y2={chart.H - chart.padB} stroke="#dce6f3" />
                {visible.map((day, index) => {
                  const x = chart.padX + index * chart.cw;
                  const cx = x + chart.cw * 0.28;
                  const flags = [];
                  for (let mark = 0; mark < day.count; mark += 1) {
                    flags.push(<FlagMark key={mark} x={cx} y={chart.H - chart.padB - (mark + 1) * chart.rowH} s={chart.fs} />);
                  }
                  return (
                    <g
                      key={day.date}
                      className="activity-day"
                      data-activity-day="true"
                      role="button"
                      tabIndex={index === (pinned ?? visible.length - 1) ? 0 : -1}
                      aria-label={`${formatDay(day.date)}: ${day.count} new, ${day.running} in total`}
                      onMouseEnter={() => setHover(index)}
                      onMouseLeave={() => setHover(null)}
                      onClick={() => setPinned(index)}
                      onFocus={() => setPinned(index)}
                      onKeyDown={(event) => moveDay(event, index)}
                    >
                      <rect
                        className="activity-day-hit"
                        x={x}
                        y={chart.top}
                        width={chart.cw}
                        height={chart.H - chart.padB - chart.top}
                        fill={active === index ? "rgba(23, 59, 115, 0.08)" : "transparent"}
                      />
                      {flags}
                      {(index % chart.tickEvery === 0 || index === visible.length - 1) ? (
                        <text x={x + chart.cw / 2} y={chart.H - 12} textAnchor="middle" fontSize={12} fill={INK}>{formatDay(day.date)}</text>
                      ) : null}
                    </g>
                  );
                })}
                <polyline className="activity-running-total" points={chart.line} fill="none" stroke={CRIMSON} strokeWidth={2.5} pointerEvents="none" />
                <text x={chart.W - 12} y={16} textAnchor="end" fontSize={12} fill={INK}>{`Running total: ${visible[visible.length - 1].running}`}</text>
              </svg>
              </div>
            )}
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
        </>
      ) : null}
    </section>
  );
}
