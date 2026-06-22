import { useEffect, useMemo, useState } from "react";
import { useIsAuthenticated, useMsal } from "@azure/msal-react";
import {
  AdminPrintQueueItem,
  AdminReviewItem,
  ApiError,
  FlagClaim,
  HonoreeSearchResult,
  SaveHonoreeChangeRequest,
  ServiceBranch,
  ServiceBranchCategory,
  adminApi,
  flagClaimApi,
  honoreeApi,
  lookupApi
} from "./api";
import { loginRequest } from "./authConfig";

const blankForm: SaveHonoreeChangeRequest = {
  firstName: "",
  middleName: "",
  lastName: "",
  suffix: "",
  nickname: "",
  rank: "",
  serviceBranchId: null,
  serviceBranchCategoryId: null,
  startYear: null,
  endYear: null,
  datesUserEntry: "",
  conflictsServed: "",
  awards: "",
  description: "",
  kia: false,
  submitterPhoneNumber: "",
  submitterEmailAddress: ""
};

function formatDate(value?: string | null) {
  if (!value) return "—";

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

function nullableNumber(value: string) {
  if (value.trim() === "") return null;
  const parsed = Number(value);
  return Number.isNaN(parsed) ? null : parsed;
}

function statusClass(status: string) {
  return `status status-${status.toLowerCase()}`;
}

export default function App() {
  const isAuthenticated = useIsAuthenticated();
  const { instance, accounts } = useMsal();
  const account = instance.getActiveAccount() ?? accounts[0];

  const [myClaims, setMyClaims] = useState<FlagClaim[]>([]);
  const [serviceBranches, setServiceBranches] = useState<ServiceBranch[]>([]);
  const [serviceBranchCategories, setServiceBranchCategories] = useState<ServiceBranchCategory[]>([]);
  const [selectedClaim, setSelectedClaim] = useState<FlagClaim | null>(null);
  const [form, setForm] = useState<SaveHonoreeChangeRequest>(blankForm);

  const [honoreeSearchText, setHonoreeSearchText] = useState("");
  const [honoreeResults, setHonoreeResults] = useState<HonoreeSearchResult[]>([]);
  const [honoreeSearchPerformed, setHonoreeSearchPerformed] = useState(false);
  const [searchLoading, setSearchLoading] = useState(false);

  const [isAdmin, setIsAdmin] = useState(false);
  const [pendingReviews, setPendingReviews] = useState<AdminReviewItem[]>([]);
  const [printQueue, setPrintQueue] = useState<AdminPrintQueueItem[]>([]);
  const [selectedPrintIds, setSelectedPrintIds] = useState<number[]>([]);
  const [adminBusyId, setAdminBusyId] = useState<number | null>(null);

  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");

  const displayName = useMemo(
    () => account?.name || account?.username || "Supporter",
    [account]
  );

  const submittedClaimIds = useMemo(
    () =>
      new Set(
        myClaims
          .filter((claim) => claim.claimStatus === "Submitted" || claim.claimStatus === "Approved")
          .map((claim) => claim.id)
      ),
    [myClaims]
  );

  const allPrintQueueIds = useMemo(
    () => printQueue.map((item) => item.changeRequestId),
    [printQueue]
  );

  const allPrintItemsSelected =
    allPrintQueueIds.length > 0 &&
    allPrintQueueIds.every((id) => selectedPrintIds.includes(id));

  async function signIn() {
    await instance.loginRedirect(loginRequest);
  }

  async function signOut() {
    await instance.logoutRedirect();
  }

  async function loadAdminData() {
    if (!account) return;

    try {
      const [pending, queue] = await Promise.all([
        adminApi.pending(instance, account),
        adminApi.printQueue(instance, account)
      ]);

      setIsAdmin(true);
      setPendingReviews(pending);
      setPrintQueue(queue);
      setSelectedPrintIds((current) =>
        current.filter((id) => queue.some((item) => item.changeRequestId === id))
      );
    } catch (err) {
      if (err instanceof ApiError && err.status === 403) {
        setIsAdmin(false);
        setPendingReviews([]);
        setPrintQueue([]);
        return;
      }

      throw err;
    }
  }

  async function loadData() {
    if (!account) return;

    setLoading(true);
    setError("");

    try {
      const [claims, branches, categories] = await Promise.all([
        flagClaimApi.mine(instance, account),
        lookupApi.serviceBranches(instance, account),
        lookupApi.serviceBranchCategories(instance, account)
      ]);

      setMyClaims(claims);
      setServiceBranches(branches);
      setServiceBranchCategories(categories);

      await loadAdminData();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to load flag data.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    if (isAuthenticated && account) {
      loadData();
    }

    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isAuthenticated, account?.homeAccountId]);

  useEffect(() => {
    const timers: number[] = [];

    if (error) {
      timers.push(window.setTimeout(() => setError(""), 8000));
    }

    if (notice) {
      timers.push(window.setTimeout(() => setNotice(""), 5000));
    }

    return () => {
      timers.forEach((timer) => window.clearTimeout(timer));
    };
  }, [error, notice]);

  async function searchHonorees(event: React.FormEvent) {
    event.preventDefault();

    if (!account) return;

    setSearchLoading(true);
    setError("");
    setNotice("");

    try {
      const results = await honoreeApi.search(instance, account, honoreeSearchText, 25);
      setHonoreeResults(results);
      setHonoreeSearchPerformed(true);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to search honorees.");
    } finally {
      setSearchLoading(false);
    }
  }

  function clearHonoreeSearch() {
    setHonoreeSearchText("");
    setHonoreeResults([]);
    setHonoreeSearchPerformed(false);
  }

  function beginEdit(claim: FlagClaim) {
    const draft = claim.latestChangeRequest;

    setSelectedClaim(claim);
    setForm({
      firstName: draft?.firstName ?? "",
      middleName: draft?.middleName ?? "",
      lastName: draft?.lastName ?? "",
      suffix: draft?.suffix ?? "",
      nickname: draft?.nickname ?? "",
      rank: draft?.rank ?? "",
      serviceBranchId: draft?.serviceBranchId ?? null,
      serviceBranchCategoryId: draft?.serviceBranchCategoryId ?? null,
      startYear: draft?.startYear ?? null,
      endYear: draft?.endYear ?? null,
      datesUserEntry: draft?.datesUserEntry ?? "",
      conflictsServed: draft?.conflictsServed ?? "",
      awards: draft?.awards ?? "",
      description: draft?.description ?? "",
      kia: draft?.kia ?? false,
      submitterPhoneNumber: draft?.submitterPhoneNumber ?? "",
      submitterEmailAddress: draft?.submitterEmailAddress ?? account?.username ?? ""
    });

    setNotice("");
    setError("");
    window.scrollTo({ top: 0, behavior: "smooth" });
  }

  async function claimSearchResult(honoree: HonoreeSearchResult) {
    if (!account) return;

    const ok = window.confirm(
      `Claim ${honoree.fullName}'s flag record? You will be able to submit corrections or updates for review.`
    );

    if (!ok) return;

    setSaving(true);
    setError("");
    setNotice("");

    try {
      const claim = await flagClaimApi.claimHonoree(instance, account, honoree.id);
      await loadData();
      setNotice(`${honoree.fullName}'s flag record has been claimed. Review the prefilled details below and submit any changes.`);
      beginEdit(claim);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to claim this honoree's flag record.");
    } finally {
      setSaving(false);
    }
  }

  async function saveDraft() {
    if (!account || !selectedClaim) return;

    setSaving(true);
    setError("");
    setNotice("");

    try {
      await flagClaimApi.saveDraft(instance, account, selectedClaim.id, form);
      await loadData();
      setNotice("Draft saved.");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to save draft.");
    } finally {
      setSaving(false);
    }
  }

  async function submitClaim(event: React.FormEvent) {
    event.preventDefault();

    if (!account || !selectedClaim) return;

    setSaving(true);
    setError("");
    setNotice("");

    try {
      await flagClaimApi.saveDraft(instance, account, selectedClaim.id, form);
      await flagClaimApi.submit(instance, account, selectedClaim.id);
      await loadData();
      setNotice("Honoree information submitted for review.");
      setSelectedClaim(null);
      setForm(blankForm);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to submit claim.");
    } finally {
      setSaving(false);
    }
  }

  async function approveReview(item: AdminReviewItem, requiresCardReprint: boolean) {
    if (!account) return;

    setAdminBusyId(item.changeRequestId);
    setError("");
    setNotice("");

    try {
      await adminApi.approve(instance, account, item.changeRequestId, requiresCardReprint);
      await loadData();
      setNotice(`${item.honoreeName} approved${requiresCardReprint ? " and added to the card reprint queue" : ""}.`);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to approve item.");
    } finally {
      setAdminBusyId(null);
    }
  }

  async function rejectReview(item: AdminReviewItem) {
    if (!account) return;

    const reviewNotes = window.prompt(`Why are you rejecting ${item.honoreeName}?`);
    if (!reviewNotes) return;

    setAdminBusyId(item.changeRequestId);
    setError("");
    setNotice("");

    try {
      await adminApi.reject(instance, account, item.changeRequestId, reviewNotes);
      await loadData();
      setNotice(`${item.honoreeName} rejected.`);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to reject item.");
    } finally {
      setAdminBusyId(null);
    }
  }

  async function downloadSelectedPrintPdf() {
    if (!account) return;

    if (selectedPrintIds.length === 0) {
      setError("Select at least one item to print.");
      return;
    }

    setSaving(true);
    setError("");
    setNotice("");

    try {
      await adminApi.downloadMergedPrintPdf(instance, account, selectedPrintIds);
      setNotice("Merged PDF downloaded. Send the downloaded PDF to the printer.");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to download merged PDF.");
    } finally {
      setSaving(false);
    }
  }

  async function markSelectedPrinted() {
    if (!account) return;

    if (selectedPrintIds.length === 0) {
      setError("Select at least one item to mark printed.");
      return;
    }

    const ok = window.confirm("Mark the selected card reprints as printed?");
    if (!ok) return;

    setSaving(true);
    setError("");
    setNotice("");

    try {
      const result = await adminApi.markPrinted(instance, account, selectedPrintIds);
      await loadData();
      setNotice(`${result.count} item(s) marked printed.`);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to mark printed.");
    } finally {
      setSaving(false);
    }
  }

  function togglePrintSelection(changeRequestId: number) {
    setSelectedPrintIds((current) =>
      current.includes(changeRequestId)
        ? current.filter((id) => id !== changeRequestId)
        : [...current, changeRequestId]
    );
  }

  function toggleSelectAllPrintItems() {
    if (allPrintItemsSelected) {
      setSelectedPrintIds([]);
    } else {
      setSelectedPrintIds(allPrintQueueIds);
    }
  }

  function update<K extends keyof SaveHonoreeChangeRequest>(
    key: K,
    value: SaveHonoreeChangeRequest[K]
  ) {
    setForm((current) => ({ ...current, [key]: value }));
  }

  return (
    <main>
      <header className="hero">
        <div>
          <p className="eyebrow">Plano Flags of Honor</p>
          <h1>Flag Manager</h1>
          <p>
            Search existing honorees, claim a flag record, and submit honoree information for review.
          </p>
        </div>

        <div className="authBox">
          {isAuthenticated ? (
            <>
              <strong>{displayName}</strong>
              <button type="button" onClick={signOut}>
                Sign out
              </button>
            </>
          ) : (
            <button type="button" onClick={signIn}>
              Register / sign in
            </button>
          )}
        </div>
      </header>

      {!isAuthenticated ? (
        <section className="card">
          <h2>Welcome</h2>
          <p>
            Sign in to search existing honorees and submit corrections for review.
          </p>
        </section>
      ) : (
        <>
          {(error || notice) && (
            <section className="messageStack" aria-live="polite" aria-atomic="true">
              {error ? (
                <div className="message error" role="alert">
                  <span>{error}</span>
                  <button
                    type="button"
                    className="messageClose"
                    aria-label="Dismiss error message"
                    onClick={() => setError("")}
                  >
                    ×
                  </button>
                </div>
              ) : null}

              {notice ? (
                <div className="message success" role="status">
                  <span>{notice}</span>
                  <button
                    type="button"
                    className="messageClose"
                    aria-label="Dismiss success message"
                    onClick={() => setNotice("")}
                  >
                    ×
                  </button>
                </div>
              ) : null}
            </section>
          )}

          <section className="card">
            <div className="sectionHeader">
              <div>
                <p className="eyebrow">Find an existing honoree</p>
                <h2>Honoree search</h2>
              </div>
            </div>

            <p>
              Search first to see whether an honoree is already in the Plano Flags of Honor database. If you find the honoree, claim that existing record instead of creating a duplicate.
            </p>

            <form className="searchBar" onSubmit={searchHonorees}>
              <input
                type="search"
                placeholder="Search by honoree name, nickname, rank, branch, sponsor, or flag grid"
                value={honoreeSearchText}
                onChange={(e) => setHonoreeSearchText(e.target.value)}
              />
              <button type="submit" disabled={searchLoading}>
                {searchLoading ? "Searching..." : "Search"}
              </button>
              <button type="button" className="secondary" onClick={clearHonoreeSearch}>
                Clear
              </button>
            </form>

            {honoreeSearchPerformed ? (
              honoreeResults.length === 0 ? (
                <p className="emptyState">
                  No honorees found. Contact the Plano Flags of Honor team if this honoree needs to be added.
                </p>
              ) : (
                <div className="honoreeResults">
                  {honoreeResults.map((honoree) => (
                    <article key={honoree.id} className="honoreeCard">
                      {honoree.imageUrl ? (
                        <img src={honoree.imageUrl} alt={honoree.fullName} />
                      ) : (
                        <div className="honoreePlaceholder">No photo</div>
                      )}

                      <div>
                        <div className="honoreeTitleRow">
                          <h3>{honoree.fullName}</h3>
                          {honoree.kia ? <span className="status status-submitted">KIA</span> : null}
                        </div>

                        <p>
                          {[honoree.rank, honoree.serviceBranchName].filter(Boolean).join(" • ") || "Service details unavailable"}
                        </p>

                        <dl className="detailGrid">
                          <div>
                            <dt>Flag grid</dt>
                            <dd>{honoree.flagGrid || "—"}</dd>
                          </div>
                          <div>
                            <dt>Sponsor</dt>
                            <dd>{honoree.sponsorName || "—"}</dd>
                          </div>
                          {honoree.nickname ? (
                            <div>
                              <dt>Nickname</dt>
                              <dd>{honoree.nickname}</dd>
                            </div>
                          ) : null}
                        </dl>

                        <div className="cardActions">
                          {honoree.pdfUrl ? (
                            <a className="textLink" href={honoree.pdfUrl} target="_blank" rel="noreferrer">
                              Open honoree PDF
                            </a>
                          ) : null}

                          <button
                            type="button"
                            onClick={() => claimSearchResult(honoree)}
                            disabled={saving}
                          >
                            Claim this flag
                          </button>
                        </div>
                      </div>
                    </article>
                  ))}
                </div>
              )
            ) : null}
          </section>

          {isAdmin ? (
            <section className="card adminCard">
              <div className="sectionHeader">
                <div>
                  <p className="eyebrow">Administrator</p>
                  <h2>Review submitted changes</h2>
                </div>
                <button type="button" className="secondary" onClick={loadData} disabled={loading}>
                  {loading ? "Loading..." : "Refresh"}
                </button>
              </div>

              {pendingReviews.length === 0 ? (
                <p>No submitted changes are waiting for review.</p>
              ) : (
                <div className="tableWrap">
                  <table>
                    <thead>
                      <tr>
                        <th>Honoree</th>
                        <th>Flag grid</th>
                        <th>Submitted by</th>
                        <th>Submitted</th>
                        <th>Actions</th>
                      </tr>
                    </thead>
                    <tbody>
                      {pendingReviews.map((item) => (
                        <tr key={item.changeRequestId}>
                          <td>
                            <strong>{item.honoreeName}</strong>
                            <br />
                            <span>{[item.rank, item.serviceBranchName].filter(Boolean).join(" • ")}</span>
                          </td>
                          <td>{item.flagGridName || item.flagGridId}</td>
                          <td>
                            {item.claimantName || item.claimantEmail}
                            <br />
                            <span>{item.claimantEmail}</span>
                          </td>
                          <td>{formatDate(item.submittedUtc)}</td>
                          <td className="rowActions stackedActions">
                            <button
                              type="button"
                              disabled={adminBusyId === item.changeRequestId}
                              onClick={() => approveReview(item, true)}
                            >
                              Approve + reprint
                            </button>
                            <button
                              type="button"
                              className="secondary"
                              disabled={adminBusyId === item.changeRequestId}
                              onClick={() => approveReview(item, false)}
                            >
                              Approve only
                            </button>
                            <button
                              type="button"
                              className="danger"
                              disabled={adminBusyId === item.changeRequestId}
                              onClick={() => rejectReview(item)}
                            >
                              Reject
                            </button>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}

              <div className="sectionHeader printHeader">
                <div>
                  <p className="eyebrow">Card printing</p>
                  <h2>Reprint queue</h2>
                </div>
                <div className="actions printActions">
                  <label className="selectAllPrint">
                    <input
                      type="checkbox"
                      checked={allPrintItemsSelected}
                      onChange={toggleSelectAllPrintItems}
                      disabled={printQueue.length === 0}
                    />
                    Select all
                  </label>

                  <button
                    type="button"
                    className="secondary"
                    onClick={downloadSelectedPrintPdf}
                    disabled={saving || selectedPrintIds.length === 0}
                  >
                    Download merged PDF
                  </button>

                  <button
                    type="button"
                    onClick={markSelectedPrinted}
                    disabled={saving || selectedPrintIds.length === 0}
                  >
                    Mark printed
                  </button>
                </div>
              </div>

              {printQueue.length === 0 ? (
                <p>No approved cards are waiting for reprint.</p>
              ) : (
                <div className="tableWrap">
                  <table>
                    <thead>
                      <tr>
                        <th></th>
                        <th>Honoree</th>
                        <th>Flag grid</th>
                        <th>Approved</th>
                        <th>PDF</th>
                      </tr>
                    </thead>
                    <tbody>
                      {printQueue.map((item) => (
                        <tr key={item.changeRequestId}>
                          <td>
                            <input
                              type="checkbox"
                              checked={selectedPrintIds.includes(item.changeRequestId)}
                              onChange={() => togglePrintSelection(item.changeRequestId)}
                            />
                          </td>
                          <td>
                            <strong>{item.honoreeName}</strong>
                            <br />
                            <span>{item.serviceBranchName}</span>
                          </td>
                          <td>{item.flagGridName}</td>
                          <td>{formatDate(item.approvedUtc)}</td>
                          <td>
                            {item.pdfUrl ? (
                              <a className="textLink" href={item.pdfUrl} target="_blank" rel="noreferrer">
                                Open PDF
                              </a>
                            ) : (
                              <span>Missing PDF</span>
                            )}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </section>
          ) : null}

          {selectedClaim ? (
            <section className="card formCard">
              <div className="sectionHeader">
                <div>
                  <p className="eyebrow">Claim #{selectedClaim.id}</p>
                  <h2>Honoree information for {selectedClaim.flagGridName || `grid ${selectedClaim.flagGridId}`}</h2>
                </div>
                <button
                  type="button"
                  className="secondary"
                  onClick={() => {
                    setSelectedClaim(null);
                    setForm(blankForm);
                  }}
                >
                  Close
                </button>
              </div>

              <form className="gridForm" onSubmit={submitClaim}>
                <label>
                  First name
                  <input
                    required
                    value={form.firstName}
                    onChange={(e) => update("firstName", e.target.value)}
                  />
                </label>

                <label>
                  Middle name
                  <input
                    value={form.middleName ?? ""}
                    onChange={(e) => update("middleName", e.target.value)}
                  />
                </label>

                <label>
                  Last name
                  <input
                    required
                    value={form.lastName}
                    onChange={(e) => update("lastName", e.target.value)}
                  />
                </label>

                <label>
                  Suffix
                  <input
                    placeholder="Jr., Sr., III"
                    value={form.suffix ?? ""}
                    onChange={(e) => update("suffix", e.target.value)}
                  />
                </label>

                <label>
                  Nickname
                  <input
                    value={form.nickname ?? ""}
                    onChange={(e) => update("nickname", e.target.value)}
                  />
                </label>

                <label>
                  Rank / title
                  <input
                    value={form.rank ?? ""}
                    onChange={(e) => update("rank", e.target.value)}
                  />
                </label>

                <label>
                  Service branch category
                  <select
                    value={form.serviceBranchCategoryId ?? ""}
                    onChange={(e) =>
                      update("serviceBranchCategoryId", nullableNumber(e.target.value))
                    }
                  >
                    <option value="">Select category</option>
                    {serviceBranchCategories.map((category) => (
                      <option key={category.id} value={category.id}>
                        {category.serviceBranchCategoryName}
                      </option>
                    ))}
                  </select>
                </label>

                <label>
                  Service branch
                  <select
                    value={form.serviceBranchId ?? ""}
                    onChange={(e) => update("serviceBranchId", nullableNumber(e.target.value))}
                  >
                    <option value="">Select branch</option>
                    {serviceBranches.map((branch) => (
                      <option key={branch.id} value={branch.id}>
                        {branch.serviceBranchName}
                      </option>
                    ))}
                  </select>
                </label>

                <label>
                  Start year
                  <input
                    type="number"
                    inputMode="numeric"
                    value={form.startYear ?? ""}
                    onChange={(e) => update("startYear", nullableNumber(e.target.value))}
                  />
                </label>

                <label>
                  End year
                  <input
                    type="number"
                    inputMode="numeric"
                    value={form.endYear ?? ""}
                    onChange={(e) => update("endYear", nullableNumber(e.target.value))}
                  />
                </label>

                <label className="wide">
                  Dates as written by user
                  <input
                    placeholder="Example: 1968–1972, Vietnam Era"
                    value={form.datesUserEntry ?? ""}
                    onChange={(e) => update("datesUserEntry", e.target.value)}
                  />
                </label>

                <label className="wide">
                  Conflicts served
                  <textarea
                    rows={3}
                    value={form.conflictsServed ?? ""}
                    onChange={(e) => update("conflictsServed", e.target.value)}
                  />
                </label>

                <label className="wide">
                  Awards
                  <textarea
                    rows={3}
                    value={form.awards ?? ""}
                    onChange={(e) => update("awards", e.target.value)}
                  />
                </label>

                <label className="wide">
                  Honoree description / tribute
                  <textarea
                    rows={6}
                    value={form.description ?? ""}
                    onChange={(e) => update("description", e.target.value)}
                  />
                </label>

                <label>
                  Submitter phone
                  <input
                    value={form.submitterPhoneNumber ?? ""}
                    onChange={(e) => update("submitterPhoneNumber", e.target.value)}
                  />
                </label>

                <label>
                  Submitter email
                  <input
                    type="email"
                    value={form.submitterEmailAddress ?? ""}
                    onChange={(e) => update("submitterEmailAddress", e.target.value)}
                  />
                </label>

                <label className="checkRow wide">
                  <input
                    type="checkbox"
                    checked={form.kia}
                    onChange={(e) => update("kia", e.target.checked)}
                  />
                  Killed in action / Gold Star recognition
                </label>

                <div className="actions wide">
                  <button type="button" className="secondary" disabled={saving} onClick={saveDraft}>
                    {saving ? "Saving..." : "Save draft"}
                  </button>
                  <button type="submit" disabled={saving}>
                    {saving ? "Submitting..." : "Submit for review"}
                  </button>
                </div>
              </form>
            </section>
          ) : null}

          <section className="card">
            <div className="sectionHeader">
              <div>
                <p className="eyebrow">Your account</p>
                <h2>My claimed flags</h2>
              </div>
              <button type="button" className="secondary" onClick={loadData} disabled={loading}>
                {loading ? "Loading..." : "Refresh"}
              </button>
            </div>

            {myClaims.length === 0 ? (
              <p>You have not claimed a flag record yet.</p>
            ) : (
              <div className="tableWrap">
                <table>
                  <thead>
                    <tr>
                      <th>Flag grid</th>
                      <th>Status</th>
                      <th>Claimed</th>
                      <th>Submitted</th>
                      <th></th>
                    </tr>
                  </thead>
                  <tbody>
                    {myClaims.map((claim) => (
                      <tr key={claim.id}>
                        <td>
                          <strong>{claim.flagGridName || `Grid ${claim.flagGridId}`}</strong>
                          <br />
                          <span>Claim #{claim.id}</span>
                        </td>
                        <td>
                          <span className={statusClass(claim.claimStatus)}>{claim.claimStatus}</span>
                          {claim.latestChangeRequest ? (
                            <>
                              <br />
                              <span>Draft: {claim.latestChangeRequest.requestStatus}</span>
                            </>
                          ) : null}
                        </td>
                        <td>{formatDate(claim.createdUtc)}</td>
                        <td>{formatDate(claim.submittedUtc)}</td>
                        <td className="rowActions">
                          <button
                            type="button"
                            onClick={() => beginEdit(claim)}
                            disabled={submittedClaimIds.has(claim.id)}
                          >
                            {submittedClaimIds.has(claim.id) ? "Submitted" : "Edit / submit"}
                          </button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>
        </>
      )}
    </main>
  );
}
