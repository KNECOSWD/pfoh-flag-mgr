import { useEffect, useState } from "react";
import type { AccountInfo, IPublicClientApplication } from "@azure/msal-browser";
import { adminApi, type AdminActivity as AdminActivityData, type ActivityStatusWindow } from "./api";

const UTC_NOTE = "Counts use UTC days. Last 7 days includes today and the six days before it. Last 30 days includes today and the 29 days before it.";

function asOfLabel(value: string) {
  const match = /^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2})/.exec(value || "");
  if (!match) return "";
  return `As of ${match[1]} ${match[2]} UTC.`;
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

  const stamp = data ? asOfLabel(data.asOfUtc) : "";

  return (
    <section id="activity" className="card card-body" aria-labelledby="activity-heading">
      <h2 id="activity-heading">Activity</h2>
      <p className="text-muted small mb-3">
        {UTC_NOTE}
        {stamp ? ` ${stamp}` : ""}
      </p>
      {error ? <p className="alert alert-danger" role="alert">{error}</p> : null}
      {loading ? <p>Loading activity.</p> : null}
      {data ? (
        <>
          <section aria-labelledby="activity-accounts" className="mb-4">
            <h3 id="activity-accounts" className="h5">New accounts</h3>
            <dl className="row mb-0">
              <CountRow label="Last 7 days" value={data.accounts.last7Days} />
              <CountRow label="Last 30 days" value={data.accounts.last30Days} />
              <CountRow label="All time" value={data.accounts.allTime} />
            </dl>
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

          <section aria-labelledby="activity-daily">
            <h3 id="activity-daily" className="h5">Daily counts</h3>
            <div className="table-responsive w-100">
              <table className="table table-sm mb-0">
                <caption className="text-muted small">Last 30 UTC days</caption>
                <thead>
                  <tr>
                    <th scope="col">Date</th>
                    <th scope="col" className="text-end">Accounts</th>
                    <th scope="col" className="text-end">Change requests</th>
                    <th scope="col" className="text-end">Flag claims</th>
                    <th scope="col" className="text-end">Receipts</th>
                  </tr>
                </thead>
                <tbody>
                  {data.daily.map((day) => (
                    <tr key={day.date}>
                      <td>{day.date}</td>
                      <td className="text-end">{day.accounts}</td>
                      <td className="text-end">{day.changeRequests}</td>
                      <td className="text-end">{day.flagClaims}</td>
                      <td className="text-end">{day.receipts}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>
        </>
      ) : null}
    </section>
  );
}
