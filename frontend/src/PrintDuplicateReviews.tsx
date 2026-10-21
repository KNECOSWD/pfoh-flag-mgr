import { useEffect, useState } from "react";
import type { AccountInfo, IPublicClientApplication } from "@azure/msal-browser";
import { ApiError, adminApi, type ReprintDuplicateReview } from "./api";

function formatWhen(value: string) {
  try {
    return new Intl.DateTimeFormat(undefined, {
      year: "numeric",
      month: "short",
      day: "numeric"
    }).format(new Date(value));
  } catch {
    return value;
  }
}

export function PrintDuplicateList({
  rows,
  loadError,
  busy,
  onApprove,
  onDismiss,
  onReload
}: {
  rows: ReprintDuplicateReview[];
  loadError: string;
  busy: boolean;
  onApprove: (id: number) => Promise<void>;
  onDismiss: (id: number) => Promise<void>;
  onReload: () => void;
}) {
  const [error, setError] = useState("");

  async function run(action: (id: number) => Promise<void>, id: number) {
    setError("");
    try {
      await action(id);
      onReload();
    } catch (err) {
      setError(err instanceof ApiError ? err.serverMessage || err.message : "That review could not be saved.");
    }
  }

  return (
    <section className="printDuplicateReviews" aria-label="Flagged reprint duplicates">
      <div className="sectionHeader">
        <div>
          <h3>
            Flagged reprint duplicates
            <span className="countBadge">{rows.length}</span>
          </h3>
          <p className="helperText">
            These queued cards may be duplicates. Approve removes that one card from the queue. Dismiss keeps both. Nothing is removed until you choose.
          </p>
        </div>
        <button type="button" className="secondary" onClick={onReload} disabled={busy}>
          Refresh
        </button>
      </div>
      {loadError ? <p className="message error" role="alert">{loadError}</p> : null}
      {error ? <p className="message error" role="alert">{error}</p> : null}
      {rows.length === 0 ? (
        <p className="compactEmptyState">No reprint duplicates are waiting for review.</p>
      ) : (
        <div className="tableWrap">
          <table className="responsiveTable">
            <thead>
              <tr>
                <th>Honoree</th>
                <th>Duplicate item</th>
                <th>Kept item</th>
                <th>Flagged</th>
                <th>Actions</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => (
                <tr key={row.id}>
                  <td data-label="Honoree">{row.honoreeName}</td>
                  <td data-label="Duplicate item">#{row.duplicateChangeRequestId}</td>
                  <td data-label="Kept item">#{row.keptChangeRequestId}</td>
                  <td data-label="Flagged">{formatWhen(row.flaggedUtc)}</td>
                  <td data-label="Actions">
                    <div className="rowActions">
                      <button type="button" disabled={busy} onClick={() => void run(onApprove, row.id)}>
                        Approve
                      </button>
                      <button type="button" className="secondary" disabled={busy} onClick={() => void run(onDismiss, row.id)}>
                        Dismiss
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

export function PrintDuplicateReviews({
  instance,
  account
}: {
  instance: IPublicClientApplication;
  account: AccountInfo | null;
}) {
  const [rows, setRows] = useState<ReprintDuplicateReview[]>([]);
  const [loadError, setLoadError] = useState("");
  const [busy, setBusy] = useState(false);

  async function load() {
    if (!account) return;
    setBusy(true);
    try {
      setRows(await adminApi.reprintDuplicateReviews(instance, account));
      setLoadError("");
    } catch (err) {
      setLoadError(err instanceof ApiError ? err.serverMessage || err.message : "Flagged reprint duplicates could not be loaded.");
    } finally {
      setBusy(false);
    }
  }

  useEffect(() => {
    void load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [account?.homeAccountId]);

  if (!account) return null;

  return (
    <PrintDuplicateList
      rows={rows}
      loadError={loadError}
      busy={busy}
      onReload={() => void load()}
      onApprove={(id) => adminApi.approveReprintDuplicate(instance, account, id).then(() => undefined)}
      onDismiss={(id) => adminApi.dismissReprintDuplicate(instance, account, id).then(() => undefined)}
    />
  );
}
