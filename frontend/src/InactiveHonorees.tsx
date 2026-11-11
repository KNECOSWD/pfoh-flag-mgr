import { FormEvent, useEffect, useMemo, useState } from "react";
import type { AccountInfo, IPublicClientApplication } from "@azure/msal-browser";
import {
  ApiError,
  inactiveHonoreeApi,
  type InactiveHonoreeRow,
  type InactiveHonoreeFreeSlot,
  type InactiveHonoreeResult
} from "./api";
import {
  confirmRestorationMessage,
  conflictLead,
  formatDeactivated,
  needsFlagChoice,
  notRecorded,
  restorationPlan
} from "./inactiveHonoreeAdmin.js";

export function InactiveHonorees({
  instance,
  account,
  confirm
}: {
  instance: IPublicClientApplication;
  account: AccountInfo | null;
  confirm: (message: string, options?: { title?: string; confirmText?: string; tone?: "default" | "danger" }) => Promise<boolean>;
}) {
  const [query, setQuery] = useState("");
  const [submittedQuery, setSubmittedQuery] = useState("");
  const [page, setPage] = useState(1);
  const [total, setTotal] = useState(0);
  const [pageSize, setPageSize] = useState(25);
  const [rows, setRows] = useState<InactiveHonoreeRow[]>([]);
  const [selected, setSelected] = useState<number[]>([]);
  const [slots, setSlots] = useState<InactiveHonoreeFreeSlot[]>([]);
  const [slotQuery, setSlotQuery] = useState("");
  const [choices, setChoices] = useState<Record<number, InactiveHonoreeFreeSlot>>({});
  const [results, setResults] = useState<InactiveHonoreeResult[]>([]);
  const [message, setMessage] = useState("");
  const [messageTone, setMessageTone] = useState<"secondary" | "danger">("secondary");
  const [busy, setBusy] = useState(false);
  const [loaded, setLoaded] = useState(false);

  async function load(nextPage: number, nextQuery: string) {
    if (!account) return;
    const list = await inactiveHonoreeApi.list(instance, account, nextQuery, nextPage);
    setRows(list.items);
    setTotal(list.total);
    setPage(list.page);
    setPageSize(list.pageSize);
    setLoaded(true);
  }

  useEffect(() => {
    if (!account) return;
    let cancelled = false;
    inactiveHonoreeApi.list(instance, account, "", 1)
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
        setMessage("Inactive honorees could not be loaded.");
        setLoaded(true);
      });
    return () => {
      cancelled = true;
    };
  }, [instance, account, account?.homeAccountId]);

  const selectable = rows.filter((row) => row.canReactivate);
  const allSelected = selectable.length > 0 && selectable.every((row) => selected.includes(row.honoreeId));
  const selectedRows = rows.filter((row) => selected.includes(row.honoreeId));
  const conflictRows = selectedRows.filter((row) => needsFlagChoice(row));

  useEffect(() => {
    if (!account || conflictRows.length === 0) return;
    let cancelled = false;
    inactiveHonoreeApi.freeSlots(instance, account, slotQuery)
      .then((list) => {
        if (!cancelled) setSlots(list.items);
      })
      .catch(() => {
        if (!cancelled) {
          setMessageTone("danger");
          setMessage("Open flags could not be loaded.");
        }
      });
    return () => {
      cancelled = true;
    };
  }, [instance, account, account?.homeAccountId, conflictRows.length, slotQuery]);

  const plans = useMemo(
    () => selectedRows.map((row) => ({
      row,
      plan: restorationPlan(row, choices[row.honoreeId] ?? null)
    })),
    [selectedRows, choices]
  );
  const blockedPlan = plans.find((item) => item.plan.blocked);

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
    setSelected([]);
    try {
      setSubmittedQuery(query);
      setPage(1);
      await load(1, query);
    } catch {
      setMessageTone("danger");
      setMessage("Inactive honorees could not be loaded.");
    } finally {
      setBusy(false);
    }
  }

  async function goTo(nextPage: number) {
    if (!account) return;
    setBusy(true);
    setMessage("");
    setSelected([]);
    try {
      await load(nextPage, submittedQuery);
    } catch {
      setMessageTone("danger");
      setMessage("Inactive honorees could not be loaded.");
    } finally {
      setBusy(false);
    }
  }

  async function restore(target: InactiveHonoreeRow[]) {
    if (!account || target.length === 0) return;
    const ready = target.map((row) => ({
      row,
      plan: restorationPlan(row, choices[row.honoreeId] ?? null)
    }));
    const blocked = ready.find((item) => item.plan.blocked);
    if (blocked) {
      setSelected((current) => current.includes(blocked.row.honoreeId) ? current : [...current, blocked.row.honoreeId]);
      setMessageTone("danger");
      setMessage(blocked.plan.message || conflictLead(blocked.row));
      return;
    }

    const confirmed = await confirm(confirmRestorationMessage(ready.map((item) => item.plan)), {
      title: ready.length === 1 ? "Restore this honoree" : "Restore these honorees",
      confirmText: "Restore"
    });
    if (!confirmed) return;

    setBusy(true);
    setMessage("");
    setResults([]);
    try {
      const payload = await inactiveHonoreeApi.reactivate(instance, account, ready.map((item) => ({
        honoreeId: item.row.honoreeId,
        flagGridId: item.plan.flagGridId ?? null
      })));
      setResults(payload.results);
      setSelected([]);
      setChoices({});
      await load(page, submittedQuery);
    } catch (error) {
      setMessageTone("danger");
      const serverMessage = error instanceof ApiError ? error.serverMessage : "";
      setMessage(serverMessage || (error instanceof Error ? error.message : "The honoree could not be restored."));
    } finally {
      setBusy(false);
    }
  }

  const firstShown = total === 0 ? 0 : (page - 1) * pageSize + 1;
  const lastShown = Math.min(page * pageSize, total);

  return (
    <section className="card adminCard" aria-labelledby="inactive-honorees-title">
      <div className="sectionHeader">
        <div>
          <p className="eyebrow">Administrator</p>
          <h2 id="inactive-honorees-title">Inactive honorees</h2>
        </div>
      </div>
      <p>
        These records are inactive in the database. Search by flag, name, or branch. Restoring a person makes that honoree active again. If the old flag is no longer open, choose a free flag. Nothing is emailed.
      </p>
      {message ? <p className={`alert alert-${messageTone}`} role={messageTone === "danger" ? "alert" : "status"}>{message}</p> : null}
      {results.length > 0 ? (
        <div className="alert alert-secondary" role="status">
          <ul className="mb-0">
            {results.map((result) => (
              <li key={result.honoreeId}>{result.message}</li>
            ))}
          </ul>
        </div>
      ) : null}
      <form className="d-flex flex-column flex-md-row gap-2 align-items-md-end mb-3" onSubmit={(event) => { void onSearch(event); }}>
        <div className="flex-grow-1">
          <label className="form-label" htmlFor="inactive-honoree-search">Search by flag, name, or branch</label>
          <input
            id="inactive-honoree-search"
            className="form-control"
            type="search"
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            autoComplete="off"
          />
        </div>
        <button type="submit" className="btn btn-primary" disabled={busy}>Search</button>
      </form>
      <div className="d-flex flex-column flex-md-row gap-2 mb-3">
        <button
          type="button"
          className="btn btn-primary"
          disabled={busy || selectedRows.length === 0 || Boolean(blockedPlan)}
          onClick={() => { void restore(selectedRows); }}
        >
          {selectedRows.length === 1 ? "Restore selected honoree" : "Restore selected honorees"}
        </button>
      </div>
      {conflictRows.length > 0 ? (
        <div className="border rounded p-3 mb-3">
          <h3 className="h6">Choose an open flag</h3>
          <div className="mb-3">
            <label className="form-label" htmlFor="free-flag-search">Search open flags</label>
            <input
              id="free-flag-search"
              className="form-control"
              type="search"
              value={slotQuery}
              onChange={(event) => setSlotQuery(event.target.value)}
              autoComplete="off"
            />
          </div>
          {conflictRows.map((row) => (
            <div key={row.honoreeId} className="mb-3">
              <p className="mb-1" role="status">{conflictLead(row)}</p>
              <label className="form-label" htmlFor={`free-flag-${row.honoreeId}`}>Open flag for {row.name}</label>
              <select
                id={`free-flag-${row.honoreeId}`}
                className="form-select"
                value={choices[row.honoreeId]?.id ?? ""}
                onChange={(event) => {
                  const value = Number(event.target.value);
                  const slot = slots.find((item) => item.id === value) ?? null;
                  setChoices((current) => {
                    const next = { ...current };
                    if (!slot) delete next[row.honoreeId];
                    else next[row.honoreeId] = slot;
                    return next;
                  });
                }}
              >
                <option value="">Choose an open flag</option>
                {slots.map((slot) => (
                  <option key={slot.id} value={slot.id}>{slot.flagGridName}</option>
                ))}
              </select>
            </div>
          ))}
        </div>
      ) : null}
      {!loaded ? <p>Loading inactive honorees.</p> : null}
      {loaded && rows.length === 0 ? (
        <p>{submittedQuery ? "No inactive honorees match this search." : "No inactive honorees are on file."}</p>
      ) : null}
      {rows.length > 0 ? (
        <div className="table-responsive">
          <table className="table table-sm align-middle mb-0">
            <thead>
              <tr>
                <th>
                  <input
                    className="form-check-input"
                    type="checkbox"
                    aria-label="Select all inactive honorees on this page"
                    checked={allSelected}
                    onChange={toggleAll}
                    disabled={selectable.length === 0}
                  />
                </th>
                <th>Name</th>
                <th>Branch</th>
                <th>Flag</th>
                <th>Deactivated</th>
                <th>Reason</th>
                <th>Restore</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => (
                <tr key={row.honoreeId}>
                  <td>
                    <input
                      className="form-check-input"
                      type="checkbox"
                      aria-label={row.canReactivate ? `Select ${row.name}` : `${row.name} cannot be restored from this page`}
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
                  <td>{row.formerFlagGridName || notRecorded}</td>
                  <td>
                    <div>{formatDeactivated(row.deactivatedUtc)}</div>
                    <div>{row.deactivatedBy || notRecorded}</div>
                  </td>
                  <td>
                    {row.reason || notRecorded}
                    {row.slotDetail ? <div>{row.slotDetail}</div> : null}
                  </td>
                  <td>
                    <button
                      type="button"
                      className="btn btn-outline-primary btn-sm"
                      disabled={busy || !row.canReactivate}
                      onClick={() => { void restore([row]); }}
                    >
                      Restore
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : null}
      {total > pageSize ? (
        <div className="d-flex flex-wrap gap-2 align-items-center mt-3">
          <button type="button" className="btn btn-outline-secondary btn-sm" disabled={busy || page <= 1} onClick={() => { void goTo(page - 1); }}>
            Previous page
          </button>
          <span>Showing {firstShown}-{lastShown} of {total}</span>
          <button type="button" className="btn btn-outline-secondary btn-sm" disabled={busy || lastShown >= total} onClick={() => { void goTo(page + 1); }}>
            Next page
          </button>
        </div>
      ) : null}
    </section>
  );
}
