import { FormEvent, useEffect, useState } from "react";
import type { AccountInfo, IPublicClientApplication } from "@azure/msal-browser";
import {
  adminReactivationApi,
  type ReactivationEligible,
  type ReactivationLinkRow
} from "./api";

function downloadCsv(csv: string) {
  const blob = new Blob([csv], { type: "text/csv;charset=utf-8" });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = "pfoh-reactivation-links.csv";
  anchor.click();
  URL.revokeObjectURL(url);
}

function expiryLabel(value?: string | null) {
  if (!value) return "No active link";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "Link active";
  return `Link active until ${date.toLocaleDateString()}`;
}

export function AdminReactivation({
  instance,
  account
}: {
  instance: IPublicClientApplication;
  account: AccountInfo | null;
}) {
  const [rows, setRows] = useState<ReactivationEligible[]>([]);
  const [selected, setSelected] = useState<number[]>([]);
  const [file, setFile] = useState<File | null>(null);
  const [results, setResults] = useState<ReactivationLinkRow[]>([]);
  const [message, setMessage] = useState("");
  const [messageTone, setMessageTone] = useState<"secondary" | "danger">("secondary");
  const [busy, setBusy] = useState(false);

  async function reload() {
    if (!account) return;
    const items = await adminReactivationApi.eligible(instance, account);
    setRows(items);
  }

  useEffect(() => {
    if (!account) return;
    let cancelled = false;
    adminReactivationApi.eligible(instance, account)
      .then((items) => {
        if (!cancelled) setRows(items);
      })
      .catch(() => {
        if (!cancelled) {
          setMessageTone("danger");
          setMessage("The eligibility list could not be loaded.");
        }
      });
    return () => {
      cancelled = true;
    };
  }, [instance, account, account?.homeAccountId]);

  function toggle(id: number) {
    setSelected((current) => current.includes(id) ? current.filter((item) => item !== id) : [...current, id]);
  }

  function toggleAll() {
    setSelected((current) => current.length === rows.length ? [] : rows.map((row) => row.honoreeId));
  }

  async function onGenerate(event: FormEvent) {
    event.preventDefault();
    if (!account) return;
    if (!file && selected.length === 0) {
      setMessageTone("secondary");
      setMessage("Select at least one honoree, or use Generate for all eligible.");
      return;
    }

    setBusy(true);
    setMessage("");
    try {
      const result = file
        ? await adminReactivationApi.generateCsv(instance, account, file)
        : await adminReactivationApi.generate(instance, account, selected, false);
      setResults(result.rows);
      downloadCsv(result.csv);
      setFile(null);
      await reload();
      setSelected([]);
      setMessageTone("secondary");
      setMessage("Links are in the downloaded CSV. A link is shown only in that file.");
    } catch {
      setMessageTone("danger");
      setMessage("Links could not be generated.");
    } finally {
      setBusy(false);
    }
  }

  async function generateAll() {
    if (!account || rows.length === 0) return;
    const confirmed = window.confirm(
      `Generate a new link for all ${rows.length} eligible honorees? Existing links will be revoked.`
    );
    if (!confirmed) return;

    setBusy(true);
    setMessage("");
    try {
      const result = await adminReactivationApi.generate(instance, account, [], true);
      setResults(result.rows);
      downloadCsv(result.csv);
      await reload();
      setSelected([]);
      setMessageTone("secondary");
      setMessage("Links are in the downloaded CSV. Existing links for those honorees were revoked.");
    } catch {
      setMessageTone("danger");
      setMessage("Links could not be generated.");
    } finally {
      setBusy(false);
    }
  }

  async function revoke(honoreeId: number) {
    if (!account) return;
    setBusy(true);
    setMessage("");
    try {
      const result = await adminReactivationApi.revoke(instance, account, honoreeId);
      await reload();
      setMessageTone("secondary");
      setMessage(result.revoked ? "The active link was revoked." : "There was no active link to revoke.");
    } catch {
      setMessageTone("danger");
      setMessage("The link could not be revoked.");
    } finally {
      setBusy(false);
    }
  }

  async function revokeSelected() {
    if (!account || selected.length === 0) return;
    const confirmed = window.confirm(
      `Revoke ${selected.length} selected reactivation links? Existing links will be revoked.`
    );
    if (!confirmed) return;
    await revokeMany(selected, false);
  }

  async function revokeAll() {
    if (!account) return;
    const confirmed = window.confirm(
      "Revoke every active reactivation link? Existing links will be revoked."
    );
    if (!confirmed) return;
    await revokeMany([], true);
  }

  async function revokeMany(honoreeIds: number[], all: boolean) {
    if (!account) return;
    setBusy(true);
    setMessage("");
    try {
      const result = await adminReactivationApi.revokeMany(instance, account, honoreeIds, all);
      await reload();
      setSelected([]);
      setMessageTone("secondary");
      setMessage(result.revokedCount > 0
        ? `${result.revokedCount} active links were revoked.`
        : "There were no active links to revoke.");
    } catch {
      setMessageTone("danger");
      setMessage("The links could not be revoked.");
    } finally {
      setBusy(false);
    }
  }

  const allSelected = rows.length > 0 && selected.length === rows.length;

  return (
    <section aria-labelledby="reactivation-admin-title">
      <h2 id="reactivation-admin-title" className="h4">Reactivation links</h2>
      <p>
        Every inactive honoree with a requester email and no flag is listed. Select rows to generate links, or generate for everyone after a confirmation. A CSV can limit the list. Ineligible rows are reported and get no link.
      </p>
      {message ? <p className={`alert alert-${messageTone}`} role="status">{message}</p> : null}
      <form className="d-flex flex-column gap-3 mb-3" onSubmit={(event) => { void onGenerate(event); }}>
        <div>
          <label className="form-label" htmlFor="reactivation-csv">Optional CSV</label>
          <input
            id="reactivation-csv"
            className="form-control"
            type="file"
            accept=".csv,text/csv"
            onChange={(event) => setFile(event.target.files?.[0] ?? null)}
          />
        </div>
        <div className="d-flex flex-column flex-md-row gap-2">
          <button type="submit" className="btn btn-primary" disabled={busy}>
            {busy ? "Working..." : "Generate links"}
          </button>
          <button
            type="button"
            className="btn btn-outline-secondary"
            disabled={busy || rows.length === 0}
            onClick={() => { void generateAll(); }}
          >
            {`Generate for all ${rows.length} eligible`}
          </button>
          <button
            type="button"
            className="btn btn-outline-secondary"
            disabled={busy || selected.length === 0}
            onClick={() => { void revokeSelected(); }}
          >
            Revoke selected
          </button>
          <button
            type="button"
            className="btn btn-outline-secondary"
            disabled={busy || rows.length === 0}
            onClick={() => { void revokeAll(); }}
          >
            Revoke all
          </button>
        </div>
      </form>
      {rows.length === 0 ? (
        <p>No inactive honorees with a requester email and no flag are waiting.</p>
      ) : (
        <div className="table-responsive reactivationAdmin">
          <table className="table table-sm align-middle mb-0">
            <thead>
              <tr>
                <th>
                  <input
                    className="form-check-input"
                    type="checkbox"
                    aria-label="Select all eligible honorees"
                    checked={allSelected}
                    onChange={toggleAll}
                  />
                </th>
                <th>Honoree id</th>
                <th>Name</th>
                <th>Requester email</th>
                <th>Link</th>
                <th>Revoke</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => (
                <tr key={row.honoreeId}>
                  <td>
                    <input
                      className="form-check-input"
                      type="checkbox"
                      aria-label={`Select ${row.name}`}
                      checked={selected.includes(row.honoreeId)}
                      onChange={() => toggle(row.honoreeId)}
                    />
                  </td>
                  <td>{row.honoreeId}</td>
                  <td>{row.name}</td>
                  <td>{row.requesterEmail}</td>
                  <td>{expiryLabel(row.activeLinkExpiresUtc)}</td>
                  <td>
                    <button
                      type="button"
                      className="btn btn-outline-secondary btn-sm"
                      disabled={busy || !row.activeLinkExpiresUtc}
                      onClick={() => { void revoke(row.honoreeId); }}
                    >
                      Revoke
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {results.length > 0 ? (
        <div className="mt-4">
          <h3 className="h5 pt-2 pb-3 mb-0">Generation result</h3>
          <div className="table-responsive reactivationAdmin">
            <table className="table table-sm align-middle mb-0">
              <thead>
                <tr>
                  <th>Honoree id</th>
                  <th>Name</th>
                  <th>Requester email</th>
                  <th>Link</th>
                  <th>Expiry</th>
                  <th>Eligibility reason</th>
                </tr>
              </thead>
              <tbody>
                {results.map((row, index) => (
                  <tr key={`${row.honoreeId}-${index}`}>
                    <td>{row.honoreeId}</td>
                    <td>{row.name}</td>
                    <td>{row.requesterEmail}</td>
                    <td>{row.link}</td>
                    <td>{row.expiry}</td>
                    <td>{row.eligibilityReason}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      ) : null}
    </section>
  );
}
