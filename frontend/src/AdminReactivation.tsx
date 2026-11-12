import { FormEvent, useEffect, useState } from "react";
import type { AccountInfo, IPublicClientApplication } from "@azure/msal-browser";
import {
  adminReactivationApi,
  ApiError,
  type InactiveHonoreeResult,
  type InactiveHonoreeRow,
  type ReactivationEligible,
  type ReactivationLinkRow
} from "./api";
import { formatDeactivated, notRecorded } from "./inactiveHonoreeAdmin.js";
import { confirmReviewQueueMessage } from "./reactivationQueue.js";

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

type Confirm = (
  message: string,
  options?: { title?: string; confirmText?: string; tone?: "default" | "danger" }
) => Promise<boolean>;

export function AdminReactivation({
  instance,
  account,
  confirm
}: {
  instance: IPublicClientApplication;
  account: AccountInfo | null;
  confirm: Confirm;
}) {
  const [view, setView] = useState<"links" | "queue">("links");

  return (
    <section aria-labelledby={view === "links" ? "reactivation-admin-title" : "reactivation-queue-title"}>
      <div className="d-flex flex-column flex-sm-row gap-2 mb-3" role="tablist" aria-label="Reactivation tools">
        <button
          type="button"
          role="tab"
          id="reactivation-links-tab"
          className={view === "links" ? "btn btn-outline-primary" : "btn btn-outline-secondary"}
          aria-controls="reactivation-links-panel"
          aria-selected={view === "links"}
          onClick={() => setView("links")}
        >
          Reactivation links
        </button>
        <button
          type="button"
          role="tab"
          id="reactivation-queue-tab"
          className={view === "queue" ? "btn btn-outline-primary" : "btn btn-outline-secondary"}
          aria-controls="reactivation-queue-panel"
          aria-selected={view === "queue"}
          onClick={() => setView("queue")}
        >
          Review queue
        </button>
      </div>
      <div id="reactivation-links-panel" role="tabpanel" aria-labelledby="reactivation-links-tab" hidden={view !== "links"}>
        <ReactivationLinks instance={instance} account={account} confirm={confirm} />
      </div>
      <div id="reactivation-queue-panel" role="tabpanel" aria-labelledby="reactivation-queue-tab" hidden={view !== "queue"}>
        <ReactivationQueue instance={instance} account={account} confirm={confirm} active={view === "queue"} />
      </div>
    </section>
  );
}

function ReactivationLinks({
  instance,
  account,
  confirm
}: {
  instance: IPublicClientApplication;
  account: AccountInfo | null;
  confirm: Confirm;
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
    const people = rows.length === 1 ? "honoree" : "honorees";
    const confirmed = await confirm(
      `Generate a new link for all ${rows.length} eligible ${people}? Existing links will be revoked.`,
      { title: "Generate links", confirmText: "Generate links" }
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
    const label = selected.length === 1
      ? "1 selected reactivation link"
      : `${selected.length} selected reactivation links`;
    const confirmed = await confirm(
      `Revoke ${label}? Existing links will be revoked.`,
      { title: "Revoke links", confirmText: "Revoke", tone: "danger" }
    );
    if (!confirmed) return;
    await revokeMany(selected, false);
  }

  async function revokeAll() {
    if (!account) return;
    const confirmed = await confirm(
      "Revoke every active reactivation link? Existing links will be revoked.",
      { title: "Revoke links", confirmText: "Revoke", tone: "danger" }
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
      setMessage(result.revokedCount === 0
        ? "There were no active links to revoke."
        : result.revokedCount === 1
          ? "1 active link was revoked."
          : `${result.revokedCount} active links were revoked.`);
    } catch {
      setMessageTone("danger");
      setMessage("The links could not be revoked.");
    } finally {
      setBusy(false);
    }
  }

  const allSelected = rows.length > 0 && selected.length === rows.length;

  return (
    <>
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
            className="secondary"
            disabled={busy || rows.length === 0}
            onClick={() => { void generateAll(); }}
          >
            {`Generate for all ${rows.length} eligible`}
          </button>
          <button
            type="button"
            className="secondary"
            disabled={busy || selected.length === 0}
            onClick={() => { void revokeSelected(); }}
          >
            Revoke selected
          </button>
          <button
            type="button"
            className="secondary"
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
        <div className="table-responsive">
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
        <div className="mt-4">
          <h3 className="h5 pt-2 pb-3 mb-0">Generation result</h3>
          <div className="table-responsive">
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
                    <td className="reactivationLink">{row.link}</td>
                    <td>{row.expiry}</td>
                    <td>{row.eligibilityReason}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      ) : null}
    </>
  );
}

function ReactivationQueue({
  instance,
  account,
  confirm,
  active
}: {
  instance: IPublicClientApplication;
  account: AccountInfo | null;
  confirm: Confirm;
  active: boolean;
}) {
  const [query, setQuery] = useState("");
  const [submittedQuery, setSubmittedQuery] = useState("");
  const [page, setPage] = useState(1);
  const [total, setTotal] = useState(0);
  const [pageSize, setPageSize] = useState(25);
  const [rows, setRows] = useState<InactiveHonoreeRow[]>([]);
  const [selected, setSelected] = useState<number[]>([]);
  const [results, setResults] = useState<InactiveHonoreeResult[]>([]);
  const [message, setMessage] = useState("");
  const [messageTone, setMessageTone] = useState<"secondary" | "danger">("secondary");
  const [busy, setBusy] = useState(false);
  const [loaded, setLoaded] = useState(false);

  async function load(nextPage: number, nextQuery: string) {
    if (!account) return;
    const list = await adminReactivationApi.inactive(instance, account, nextQuery, nextPage);
    setRows(list.items);
    setTotal(list.total);
    setPage(list.page);
    setPageSize(list.pageSize);
    setLoaded(true);
  }

  useEffect(() => {
    if (!active || !account || loaded) return;
    let cancelled = false;
    adminReactivationApi.inactive(instance, account, "", 1)
      .then((list) => {
        if (cancelled) return;
        setRows(list.items);
        setTotal(list.total);
        setPage(list.page);
        setPageSize(list.pageSize);
        setLoaded(true);
      })
      .catch(() => {
        if (cancelled) return;
        setMessageTone("danger");
        setMessage("Inactive records could not be loaded.");
        setLoaded(true);
      });
    return () => {
      cancelled = true;
    };
  }, [active, instance, account, account?.homeAccountId, loaded]);

  const selectable = rows.filter((row) => row.canReactivate);
  const allSelected = selectable.length > 0 && selectable.every((row) => selected.includes(row.honoreeId));
  const selectedRows = rows.filter((row) => selected.includes(row.honoreeId) && row.canReactivate);

  function toggle(id: number) {
    setSelected((current) => current.includes(id) ? current.filter((item) => item !== id) : [...current, id]);
  }

  function toggleAll() {
    setSelected(allSelected ? [] : selectable.map((row) => row.honoreeId));
  }

  async function onSearch(event: FormEvent) {
    event.preventDefault();
    if (!account) return;
    setBusy(true);
    setMessage("");
    setResults([]);
    setSelected([]);
    try {
      setSubmittedQuery(query);
      await load(1, query);
    } catch {
      setMessageTone("danger");
      setMessage("Inactive records could not be loaded.");
    } finally {
      setBusy(false);
    }
  }

  async function goTo(nextPage: number) {
    if (!account) return;
    setBusy(true);
    setMessage("");
    setResults([]);
    setSelected([]);
    try {
      await load(nextPage, submittedQuery);
    } catch {
      setMessageTone("danger");
      setMessage("Inactive records could not be loaded.");
    } finally {
      setBusy(false);
    }
  }

  async function addSelected() {
    if (!account || selectedRows.length === 0) return;
    const confirmed = await confirm(confirmReviewQueueMessage(selectedRows), {
      title: "Add to review queue",
      confirmText: "Add to review queue"
    });
    if (!confirmed) return;

    setBusy(true);
    setMessage("");
    setResults([]);
    try {
      const payload = await adminReactivationApi.addToReviewQueue(
        instance,
        account,
        selectedRows.map((row) => row.honoreeId)
      );
      setResults(payload.results);
      setMessageTone(payload.results.some((row) => row.outcome !== "reactivated") ? "danger" : "secondary");
      setSelected([]);
      await load(page, submittedQuery);
    } catch (error) {
      setMessageTone("danger");
      const serverMessage = error instanceof ApiError ? error.serverMessage : "";
      setMessage(serverMessage || "The selected records could not be added to the review queue.");
      try {
        await load(page, submittedQuery);
      } catch {
        // Keep the refusal message when the list cannot be refreshed.
      }
    } finally {
      setBusy(false);
    }
  }

  const firstShown = total === 0 ? 0 : (page - 1) * pageSize + 1;
  const lastShown = Math.min(page * pageSize, total);

  return (
    <div className="reactivationQueue">
      <h2 id="reactivation-queue-title" className="h4">Review queue</h2>
      <p>
        Search inactive records that are not assigned to a flag. Select one or more, then add them to the review queue. A flag is chosen later, during approval.
      </p>
      {message ? <p className={`alert alert-${messageTone}`} role={messageTone === "danger" ? "alert" : "status"}>{message}</p> : null}
      {results.length > 0 ? (
        <div className={`alert alert-${messageTone}`} role={messageTone === "danger" ? "alert" : "status"}>
          <ul className="mb-0">
            {results.map((result) => (
              <li key={result.honoreeId}>{result.message}</li>
            ))}
          </ul>
        </div>
      ) : null}
      <form className="d-flex flex-column flex-md-row gap-2 align-items-md-end mb-3" onSubmit={(event) => { void onSearch(event); }}>
        <div className="flex-grow-1">
          <label className="form-label" htmlFor="reactivation-queue-search">Search by name, branch, or record id</label>
          <input
            id="reactivation-queue-search"
            className="form-control"
            type="search"
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            autoComplete="off"
          />
        </div>
        <button type="submit" className="btn btn-outline-secondary" disabled={busy}>Search</button>
      </form>
      <div className="mb-3">
        <button
          type="button"
          className="btn btn-primary"
          disabled={busy || selectedRows.length === 0}
          onClick={() => { void addSelected(); }}
        >
          Add to review queue
        </button>
      </div>
      {!loaded ? <p>Loading records.</p> : null}
      {loaded && rows.length === 0 ? (
        <p>{submittedQuery ? "No records match this search." : "No inactive records are waiting."}</p>
      ) : null}
      {rows.length > 0 ? (
        <>
          <div className="d-md-none">
            <div className="form-check mb-3">
              <input
                id="reactivation-queue-select-all-narrow"
                className="form-check-input"
                type="checkbox"
                aria-label="Select all visible records"
                checked={allSelected}
                disabled={selectable.length === 0}
                onChange={toggleAll}
              />
              <label className="form-check-label" htmlFor="reactivation-queue-select-all-narrow">Select all visible records</label>
            </div>
            {rows.map((row) => (
              <article key={row.honoreeId} className="border rounded p-3 mb-2">
                <div className="form-check">
                  <input
                    id={`reactivation-queue-${row.honoreeId}`}
                    className="form-check-input"
                    type="checkbox"
                    aria-label={row.canReactivate ? `Select ${row.name}` : `${row.name} cannot be added to the review queue`}
                    checked={selected.includes(row.honoreeId)}
                    disabled={!row.canReactivate}
                    onChange={() => toggle(row.honoreeId)}
                  />
                  <label className="form-check-label" htmlFor={`reactivation-queue-${row.honoreeId}`}>
                    <strong>{row.name}</strong>
                  </label>
                </div>
                {row.rank ? <div>{row.rank}</div> : null}
                <div>{row.branch || notRecorded}</div>
                <div>Record {row.honoreeId}</div>
                {row.legacyKey ? <div>Legacy {row.legacyKey}</div> : null}
                <div>{formatDeactivated(row.deactivatedUtc)}</div>
                <div>{row.deactivatedBy || notRecorded}</div>
                <div>{row.reason || notRecorded}</div>
                {row.blockReason ? <div>{row.blockReason}</div> : null}
              </article>
            ))}
          </div>
          <div className="d-none d-md-block table-responsive">
            <table className="table table-sm align-middle mb-0">
              <thead>
                <tr>
                  <th>
                    <input
                      className="form-check-input"
                      type="checkbox"
                      aria-label="Select all visible records"
                      checked={allSelected}
                      disabled={selectable.length === 0}
                      onChange={toggleAll}
                    />
                  </th>
                  <th>Name</th>
                  <th>Branch</th>
                  <th>Record id</th>
                  <th>Deactivated</th>
                  <th>Reason</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((row) => (
                  <tr key={row.honoreeId}>
                    <td>
                      <input
                        className="form-check-input"
                        type="checkbox"
                        aria-label={row.canReactivate ? `Select ${row.name}` : `${row.name} cannot be added to the review queue`}
                        checked={selected.includes(row.honoreeId)}
                        disabled={!row.canReactivate}
                        onChange={() => toggle(row.honoreeId)}
                      />
                    </td>
                    <td>
                      <strong>{row.name}</strong>
                      {row.rank ? <div>{row.rank}</div> : null}
                    </td>
                    <td>{row.branch || notRecorded}</td>
                    <td>
                      <div>{row.honoreeId}</div>
                      {row.legacyKey ? <div>Legacy {row.legacyKey}</div> : null}
                    </td>
                    <td>
                      <div>{formatDeactivated(row.deactivatedUtc)}</div>
                      <div>{row.deactivatedBy || notRecorded}</div>
                    </td>
                    <td>
                      {row.reason || notRecorded}
                      {row.blockReason ? <div>{row.blockReason}</div> : null}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      ) : null}
      {total > pageSize ? (
        <div className="d-flex flex-wrap gap-2 align-items-center mt-3">
          <button type="button" className="btn btn-outline-secondary btn-sm" disabled={busy || page <= 1} onClick={() => { void goTo(page - 1); }}>
            Previous page
          </button>
          <span>{`Showing ${firstShown}-${lastShown} of ${total}`}</span>
          <button type="button" className="btn btn-outline-secondary btn-sm" disabled={busy || lastShown >= total} onClick={() => { void goTo(page + 1); }}>
            Next page
          </button>
        </div>
      ) : null}
    </div>
  );
}
