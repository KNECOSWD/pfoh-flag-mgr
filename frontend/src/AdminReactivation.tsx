import { FormEvent, useEffect, useState } from "react";
import type { AccountInfo, IPublicClientApplication } from "@azure/msal-browser";
import {
  adminReactivationApi,
  type ReactivationEligible,
  type ReactivationLinkRow
} from "./api";

const previewRows: ReactivationEligible[] = [
  {
    honoreeId: 101,
    name: "TEST Reactivation Honoree",
    requesterEmail: "test-requester@example.test",
    activeLinkExpiresUtc: null
  },
  {
    honoreeId: 102,
    name: "TEST Second Honoree",
    requesterEmail: "other-requester@example.test",
    activeLinkExpiresUtc: "2026-12-29T00:00:00Z"
  }
];

const previewResult: ReactivationLinkRow[] = [
  {
    honoreeId: "101",
    name: "TEST Reactivation Honoree",
    requesterEmail: "test-requester@example.test",
    link: "https://flags.example.test/reactivate/preview-token-not-a-real-secret",
    expiry: "2026-12-29T00:00:00Z",
    eligibilityReason: "Eligible"
  },
  {
    honoreeId: "404",
    name: "",
    requesterEmail: "",
    link: "",
    expiry: "",
    eligibilityReason: "not found"
  }
];

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
  account,
  preview
}: {
  instance: IPublicClientApplication;
  account: AccountInfo | null;
  preview?: "list" | "result" | null;
}) {
  const [rows, setRows] = useState<ReactivationEligible[]>(preview ? previewRows : []);
  const [selected, setSelected] = useState<number[]>([]);
  const [file, setFile] = useState<File | null>(null);
  const [results, setResults] = useState<ReactivationLinkRow[]>(preview === "result" ? previewResult : []);
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (preview || !account) return;
    let cancelled = false;
    adminReactivationApi.eligible(instance, account)
      .then((items) => {
        if (!cancelled) setRows(items);
      })
      .catch(() => {
        if (!cancelled) setMessage("The eligibility list could not be loaded.");
      });
    return () => {
      cancelled = true;
    };
  }, [instance, account, preview, account?.homeAccountId]);

  function toggle(id: number) {
    setSelected((current) => current.includes(id) ? current.filter((item) => item !== id) : [...current, id]);
  }

  function toggleAll() {
    setSelected((current) => current.length === rows.length ? [] : rows.map((row) => row.honoreeId));
  }

  async function onGenerate(event: FormEvent) {
    event.preventDefault();
    if (preview) {
      setResults(previewResult);
      return;
    }
    if (!account) return;
    setBusy(true);
    setMessage("");
    try {
      const result = file
        ? await adminReactivationApi.generateCsv(instance, account, file)
        : await adminReactivationApi.generate(instance, account, selected);
      setResults(result.rows);
      downloadCsv(result.csv);
      setFile(null);
      const fresh = await adminReactivationApi.eligible(instance, account);
      setRows(fresh);
      setSelected([]);
      setMessage("Links are in the downloaded CSV. A link is shown only in that file.");
    } catch {
      setMessage("Links could not be generated.");
    } finally {
      setBusy(false);
    }
  }

  async function revoke(honoreeId: number) {
    if (preview || !account) return;
    setBusy(true);
    setMessage("");
    try {
      const result = await adminReactivationApi.revoke(instance, account, honoreeId);
      const fresh = await adminReactivationApi.eligible(instance, account);
      setRows(fresh);
      setMessage(result.revoked ? "The active link was revoked." : "There was no active link to revoke.");
    } catch {
      setMessage("The link could not be revoked.");
    } finally {
      setBusy(false);
    }
  }

  const allSelected = rows.length > 0 && selected.length === rows.length;

  return (
    <section className="reactivationAdmin" aria-labelledby="reactivation-admin-title">
      <h2 id="reactivation-admin-title">Reactivation links</h2>
      <p>
        Every inactive honoree with a requester email and no flag is listed. Generate links for the selected rows, or leave the selection empty to cover the whole list. A CSV can limit the list. Ineligible rows are reported and get no link.
      </p>
      {message ? <p className="reactivationMessage" role="status">{message}</p> : null}
      <form className="reactivationToolbar" onSubmit={(event) => { void onGenerate(event); }}>
        <label className="reactivationFile">
          Optional CSV
          <input
            type="file"
            accept=".csv,text/csv"
            onChange={(event) => setFile(event.target.files?.[0] ?? null)}
          />
        </label>
        <button type="submit" disabled={busy || (!file && rows.length === 0)}>
          {busy ? "Working..." : "Generate links"}
        </button>
      </form>
      {rows.length === 0 ? (
        <p>No inactive honorees with a requester email and no flag are waiting.</p>
      ) : (
        <div className="reactivationTableWrap">
          <table className="reactivationTable">
            <thead>
              <tr>
                <th>
                  <input
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
                      className="secondary"
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
        <div className="reactivationTableWrap">
          <h3>Generation result</h3>
          <table className="reactivationTable">
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
      ) : null}
    </section>
  );
}
