import { useEffect, useMemo, useRef, useState } from "react";
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
  honoreePdfUrl,
  honoreePhotoUrl,
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

function latestRequestStatus(claim: FlagClaim) {
  return claim.latestChangeRequest?.requestStatus ?? claim.claimStatus;
}

function ownershipStatusLabel(claim: FlagClaim) {
  const latest = claim.latestChangeRequest?.requestStatus;

  if (latest === "Submitted") return "Awaiting admin review";
  if (latest === "Approved") return "Approved";
  if (latest === "Rejected") return "Needs revision";
  if (latest === "Draft") return "Draft saved";

  return "Managed by you";
}


export default function App() {
  const isAuthenticated = useIsAuthenticated();
  const { instance, accounts } = useMsal();
  const account = instance.getActiveAccount() ?? accounts[0];

  const [myClaims, setMyClaims] = useState<FlagClaim[]>([]);
  const [serviceBranches, setServiceBranches] = useState<ServiceBranch[]>([]);
  const [serviceBranchCategories, setServiceBranchCategories] = useState<ServiceBranchCategory[]>([]);
  const [selectedClaim, setSelectedClaim] = useState<FlagClaim | null>(null);
  const [isNominating, setIsNominating] = useState(false);
  const [form, setForm] = useState<SaveHonoreeChangeRequest>(blankForm);
  const [selectedPhoto, setSelectedPhoto] = useState<File | null>(null);
  const [selectedPhotoPreviewUrl, setSelectedPhotoPreviewUrl] = useState("");
  const formCardRef = useRef<HTMLElement | null>(null);
  const firstFormInputRef = useRef<HTMLInputElement | null>(null);

  const [honoreeSearchText, setHonoreeSearchText] = useState("");
  const [honoreeResults, setHonoreeResults] = useState<HonoreeSearchResult[]>([]);
  const [honoreeSearchPerformed, setHonoreeSearchPerformed] = useState(false);
  const [searchLoading, setSearchLoading] = useState(false);

  const [isAdmin, setIsAdmin] = useState(false);
  const [pendingReviews, setPendingReviews] = useState<AdminReviewItem[]>([]);
  const [printQueue, setPrintQueue] = useState<AdminPrintQueueItem[]>([]);
  const [selectedPrintIds, setSelectedPrintIds] = useState<number[]>([]);
  const [adminBusyId, setAdminBusyId] = useState<number | null>(null);
  const [regeneratingPdfHonoreeId, setRegeneratingPdfHonoreeId] = useState<number | null>(null);

  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");

  const displayName = useMemo(
    () => account?.name || account?.username || "Supporter",
    [account]
  );

  const allPrintQueueIds = useMemo(
    () => printQueue.map((item) => item.changeRequestId),
    [printQueue]
  );

  const allPrintItemsSelected =
    allPrintQueueIds.length > 0 &&
    allPrintQueueIds.every((id) => selectedPrintIds.includes(id));

  const filteredServiceBranches = useMemo(() => {
    if (!form.serviceBranchCategoryId) return [];

    return serviceBranches.filter(
      (branch) => branch.serviceBranchCategoryId === form.serviceBranchCategoryId
    );
  }, [form.serviceBranchCategoryId, serviceBranches]);

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

  useEffect(() => {
    if (!selectedClaim && !isNominating) return;

    const timer = window.setTimeout(() => {
      formCardRef.current?.scrollIntoView({ behavior: "smooth", block: "start" });
      firstFormInputRef.current?.focus({ preventScroll: true });
    }, 75);

    return () => window.clearTimeout(timer);
  }, [selectedClaim, isNominating]);

  useEffect(() => {
    if (!selectedPhoto) {
      setSelectedPhotoPreviewUrl("");
      return;
    }

    const previewUrl = URL.createObjectURL(selectedPhoto);
    setSelectedPhotoPreviewUrl(previewUrl);

    return () => URL.revokeObjectURL(previewUrl);
  }, [selectedPhoto]);

  async function searchHonorees(event: React.FormEvent) {
    event.preventDefault();

    setSearchLoading(true);
    setError("");
    setNotice("");

    try {
      const results = await honoreeApi.search(honoreeSearchText, 25);
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

  function setFormFromClaim(claim: FlagClaim) {
    const draft = claim.latestChangeRequest;

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
  }

  function beginEdit(claim: FlagClaim) {
    setIsNominating(false);
    setSelectedPhoto(null);
    setSelectedClaim(claim);
    setFormFromClaim(claim);
    setNotice("");
    setError("");
  }

  function beginNomination() {
    if (!account) {
      void signIn();
      return;
    }

    setSelectedPhoto(null);
    setSelectedClaim(null);
    setIsNominating(true);
    setForm({
      ...blankForm,
      submitterEmailAddress: account.username ?? ""
    });
    setNotice("");
    setError("");
  }

  async function claimSearchResult(honoree: HonoreeSearchResult) {
    if (!account) {
      await signIn();
      return;
    }

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
      const coClaimNotice = claim.claimNotice
        ? ` ${claim.claimNotice}`
        : "";
      setNotice(`${honoree.fullName}'s flag record has been claimed. Review the prefilled details below and submit any changes.${coClaimNotice}`);
      beginEdit(claim);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to claim this honoree's flag record.");
    } finally {
      setSaving(false);
    }
  }

  async function beginAdminDirectEdit(honoree: HonoreeSearchResult) {
    if (!account) {
      await signIn();
      return;
    }

    if (!isAdmin) {
      setError("Only PFOH administrators can directly edit and queue a reprint.");
      return;
    }

    const ok = window.confirm(
      `Edit ${honoree.fullName}'s flag record directly and queue a card reprint after saving?`
    );

    if (!ok) return;

    setSaving(true);
    setError("");
    setNotice("");

    try {
      const claim = await flagClaimApi.startAdminEdit(instance, account, honoree.id);
      beginEdit(claim);
      setNotice("Admin edit started. Save changes and queue the reprint when ready.");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to start admin edit.");
    } finally {
      setSaving(false);
    }
  }

  async function regenerateHonoreePdf(honoree: HonoreeSearchResult) {
    if (!account) {
      await signIn();
      return;
    }

    if (!isAdmin) {
      setError("Only PFOH administrators can regenerate honoree PDFs.");
      return;
    }

    const ok = window.confirm(
      `Generate a new PDF for ${honoree.fullName} and overwrite the existing stored PDF?`
    );

    if (!ok) return;

    setRegeneratingPdfHonoreeId(honoree.id);
    setError("");
    setNotice("");

    try {
      const result = await adminApi.regenerateHonoreePdf(instance, account, honoree.id);

      if (result.uploaded) {
        setNotice(result.message || `PDF regenerated for ${honoree.fullName}: ${result.fileName}`);
      } else {
        setError(result.message || "PDF was generated but could not be saved.");
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to regenerate PDF.");
    } finally {
      setRegeneratingPdfHonoreeId(null);
    }
  }

  async function submitNomination(event: React.FormEvent) {
    event.preventDefault();

    if (!account) return;

    setSaving(true);
    setError("");
    setNotice("");

    try {
      await flagClaimApi.nominate(instance, account, form, selectedPhoto);
      await loadData();
      setNotice("Nomination submitted for admin review and claimed under your account.");
      setIsNominating(false);
      setSelectedPhoto(null);
      setForm(blankForm);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to submit nomination.");
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
      await flagClaimApi.saveDraft(instance, account, selectedClaim.id, form, selectedPhoto);
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

    const isAdminDirectEdit = selectedClaim.claimStatus === "AdminDirectEdit";

    setSaving(true);
    setError("");
    setNotice("");

    try {
      await flagClaimApi.saveDraft(instance, account, selectedClaim.id, form, selectedPhoto);

      if (isAdminDirectEdit) {
        await flagClaimApi.applyAdminEditReprint(instance, account, selectedClaim.id);
        await loadData();
        setNotice("Admin edit applied and added to the reprint queue.");
      } else {
        await flagClaimApi.submit(instance, account, selectedClaim.id);
        await loadData();
        setNotice("Honoree information submitted for review.");
      }

      setSelectedClaim(null);
      setSelectedPhoto(null);
      setForm(blankForm);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to submit changes.");
    } finally {
      setSaving(false);
    }
  }

  async function approveReview(item: AdminReviewItem, requiresCardReprint: boolean) {
    if (!account) {
      setError("You must be signed in as an administrator to approve review items.");
      return;
    }

    if (!item.changeRequestId) {
      setError("This review item is missing its change request ID. Refresh the page and try again.");
      return;
    }

    setAdminBusyId(item.changeRequestId);
    setError("");
    setNotice(`${requiresCardReprint ? "Approving and queuing reprint" : "Approving"} ${item.honoreeName}...`);

    try {
      await adminApi.approve(instance, account, item.changeRequestId, requiresCardReprint);
      await loadData();
      setNotice(`${item.honoreeName} approved${requiresCardReprint ? " and added to the card reprint queue" : ""}.`);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to approve item.");
      setNotice("");
    } finally {
      setAdminBusyId(null);
    }
  }

  async function rejectReview(item: AdminReviewItem) {
    if (!account) {
      setError("You must be signed in as an administrator to reject review items.");
      return;
    }

    if (!item.changeRequestId) {
      setError("This review item is missing its change request ID. Refresh the page and try again.");
      return;
    }

    const reviewNotes = window.prompt(`Why are you rejecting ${item.honoreeName}?`, "Rejected by administrator.");
    if (!reviewNotes || !reviewNotes.trim()) {
      setNotice("Reject canceled. A rejection reason is required.");
      return;
    }

    setAdminBusyId(item.changeRequestId);
    setError("");
    setNotice(`Rejecting ${item.honoreeName}...`);

    try {
      await adminApi.reject(instance, account, item.changeRequestId, reviewNotes.trim());
      await loadData();
      setNotice(`${item.honoreeName} rejected.`);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to reject item.");
      setNotice("");
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
          <h1>Find a Flag</h1>
          <p>
            Search Plano Flags of Honor honoree records and view flag details.
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


          <section className="card searchCard">
            <div className="sectionHeader searchHeader">
              <div>
                <p className="eyebrow">Find an existing honoree</p>
                <h2>Honoree search</h2>
              </div>

              {isAuthenticated ? (
                <button type="button" className="secondary" onClick={beginNomination}>
                  Nominate honoree
                </button>
              ) : null}
            </div>

            <form className="searchBar" onSubmit={searchHonorees}>
              <input
                type="search"
                placeholder="Search by honoree name, nickname, rank, branch, submitter, or flag grid"
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
                  No honorees found. A signed-in user can nominate a veteran or first responder for admin review.
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
                            <dt>Submitter</dt>
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
                          <a className="textLink" href={honoreePdfUrl(honoree.id)} target="_blank" rel="noreferrer">
                            Open honoree PDF
                          </a>

                          <div className="honoreeActionButtons">
                            <button
                              type="button"
                              onClick={() => claimSearchResult(honoree)}
                              disabled={saving}
                            >
                              {isAuthenticated ? "Claim this flag" : "Sign in to claim"}
                            </button>

                            {isAdmin ? (
                              <>
                                <button
                                  type="button"
                                  className="secondary compactButton"
                                  onClick={() => beginAdminDirectEdit(honoree)}
                                  disabled={saving || regeneratingPdfHonoreeId === honoree.id}
                                >
                                  Edit + reprint
                                </button>

                                <button
                                  type="button"
                                  className="secondary compactButton"
                                  onClick={() => regenerateHonoreePdf(honoree)}
                                  disabled={saving || regeneratingPdfHonoreeId === honoree.id}
                                >
                                  {regeneratingPdfHonoreeId === honoree.id ? "Generating..." : "Regenerate PDF"}
                                </button>
                              </>
                            ) : null}
                          </div>
                        </div>
                      </div>
                    </article>
                  ))}
                </div>
              )
            ) : null}
          </section>


          {!isAuthenticated ? (
            <section className="card guestNotice">
              <h2>Search is open to everyone</h2>
              <p>
                You can search and view honoree flag records without signing in. Sign in or register when you are ready to claim a flag record, submit updates, or nominate a veteran or first responder.
              </p>
              <button type="button" onClick={signIn}>
                Register / sign in
              </button>
            </section>
          ) : null}

          {isAuthenticated ? (
            <>
          <section className="card ownershipCard">
            <div className="sectionHeader">
              <div>
                <p className="eyebrow">Your account</p>
                <h2>My claimed flags</h2>
                <p className="helperText">
                  These are the flag records you manage, including nominations you submitted. You can submit updates at any time; admins review and approve changes before they are published or reprinted.
                </p>
              </div>
              <button type="button" className="secondary" onClick={loadData} disabled={loading}>
                {loading ? "Loading..." : "Refresh"}
              </button>
            </div>

            {myClaims.length === 0 ? (
              <p className="emptyState">You have not claimed a flag record yet.</p>
            ) : (
              <div className="ownedFlagGrid">
                {myClaims.map((claim) => {
                  const status = latestRequestStatus(claim);

                  return (
                    <article key={claim.id} className="ownedFlagCard">
                      {claim.honoreeImageUrl ? (
                        <img
                          className="ownedFlagImage"
                          src={claim.honoreeImageUrl}
                          alt={`${claim.honoreeName || "Honoree"} photo`}
                        />
                      ) : (
                        <div className="ownedFlagImage ownedFlagImagePlaceholder">No photo</div>
                      )}

                      <div>
                        <p className="eyebrow">Honoree</p>
                        <h3>{claim.honoreeName || "Honoree details pending"}</h3>
                        <p>
                          Flag grid {claim.flagGridName || `Grid ${claim.flagGridId}`} • Claim #{claim.id}
                        </p>
                      </div>

                      <div className="ownedFlagMeta">
                        <span className={statusClass(status)}>{ownershipStatusLabel(claim)}</span>
                        <span>Claimed {formatDate(claim.createdUtc)}</span>
                        {claim.submittedUtc ? <span>Last submitted {formatDate(claim.submittedUtc)}</span> : null}
                      </div>

                      {claim.claimNotice ? (
                        <p className="claimNotice">{claim.claimNotice}</p>
                      ) : null}

                      <div className="ownedFlagActions">
                        <button type="button" onClick={() => beginEdit(claim)}>
                          Manage flag
                        </button>
                      </div>
                    </article>
                  );
                })}
              </div>
            )}
          </section>

          {isAdmin ? (
            <section className="card adminCard">
              <div className="sectionHeader">
                <div>
                  <p className="eyebrow">Administrator</p>
                  <h2>
                    Review submitted changes
                    <span className="countBadge">{pendingReviews.length}</span>
                  </h2>
                </div>
                <button type="button" className="secondary" onClick={loadData} disabled={loading}>
                  {loading ? "Loading..." : "Refresh"}
                </button>
              </div>

              <div className="adminStats" aria-label="Administrator dashboard summary">
                <div className="statCard">
                  <strong>{pendingReviews.length}</strong>
                  <span>Pending review</span>
                </div>
                <div className="statCard">
                  <strong>{printQueue.length}</strong>
                  <span>Ready to print</span>
                </div>
                <div className="statCard">
                  <strong>{selectedPrintIds.length}</strong>
                  <span>Selected</span>
                </div>
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
                              {adminBusyId === item.changeRequestId ? "Approving..." : "Approve + reprint"}
                            </button>
                            <button
                              type="button"
                              className="secondary"
                              disabled={adminBusyId === item.changeRequestId}
                              onClick={() => approveReview(item, false)}
                            >
                              {adminBusyId === item.changeRequestId ? "Approving..." : "Approve only"}
                            </button>
                            <button
                              type="button"
                              className="danger"
                              disabled={adminBusyId === item.changeRequestId}
                              onClick={() => rejectReview(item)}
                            >
                              {adminBusyId === item.changeRequestId ? "Rejecting..." : "Reject"}
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
                  <h2>
                    Reprint queue
                    <span className="countBadge">{printQueue.length}</span>
                  </h2>
                </div>
                <div className="actions printActions">
                  <span className="selectedCount">{selectedPrintIds.length} selected</span>

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
                        <th>
                          <label className="tableSelectAll">
                            <input
                              type="checkbox"
                              checked={allPrintItemsSelected}
                              onChange={toggleSelectAllPrintItems}
                              disabled={printQueue.length === 0}
                              aria-label="Select all cards for printing"
                            />
                            <span>Select all</span>
                          </label>
                        </th>
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
                            {item.honoreeId ? (
                              <a className="textLink" href={honoreePdfUrl(item.honoreeId)} target="_blank" rel="noreferrer">
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

          {selectedClaim || isNominating ? (
            <section ref={formCardRef} className="card formCard">
              <div className="sectionHeader">
                <div>
                  <p className="eyebrow">
                    {isNominating
                      ? "New nomination"
                      : selectedClaim?.claimStatus === "AdminDirectEdit"
                        ? "Administrator edit"
                        : `Claim #${selectedClaim?.id}`}
                  </p>
                  <h2>
                    {isNominating
                      ? "Nominate a veteran or first responder"
                      : selectedClaim?.claimStatus === "AdminDirectEdit"
                        ? `Directly edit ${selectedClaim?.honoreeName || "honoree record"}`
                        : `Manage flag record for ${selectedClaim?.flagGridName || `grid ${selectedClaim?.flagGridId}`}`}
                  </h2>
                </div>
                <button
                  type="button"
                  className="secondary"
                  onClick={() => {
                    setSelectedClaim(null);
                    setIsNominating(false);
                    setSelectedPhoto(null);
                    setForm(blankForm);
                  }}
                >
                  Close
                </button>
              </div>

              <p className="helperText">
                {isNominating
                  ? "Submit the honoree information for admin review. The nomination will be listed under your claimed flags, and you will be recorded as the submitter."
                  : selectedClaim?.claimStatus === "AdminDirectEdit"
                    ? "As an administrator, you can save these changes directly and add the card to the reprint queue without claiming the flag."
                    : "You can submit updates to this flag record whenever needed. Changes go to the Plano Flags of Honor admin team for approval before publishing and reprinting."}
              </p>

              <p className="requiredFieldsNote">
                Fields marked <span className="requiredMark" aria-hidden="true">*</span> are required.
              </p>

              <form className="gridForm" onSubmit={isNominating ? submitNomination : submitClaim}>
                <label>
                  <span className="fieldLabelText">
                    First name <span className="requiredMark" aria-hidden="true">*</span>
                  </span>
                  <input
                    ref={firstFormInputRef}
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
                  <span className="fieldLabelText">
                    Last name <span className="requiredMark" aria-hidden="true">*</span>
                  </span>
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
                  <span className="fieldLabelText">
                    Service branch category <span className="requiredMark" aria-hidden="true">*</span>
                  </span>
                  <select
                    required
                    value={form.serviceBranchCategoryId ?? ""}
                    onChange={(e) => {
                      const categoryId = nullableNumber(e.target.value);
                      setForm((current) => ({
                        ...current,
                        serviceBranchCategoryId: categoryId,
                        serviceBranchId: null
                      }));
                    }}
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
                  <span className="fieldLabelText">
                    Service branch <span className="requiredMark" aria-hidden="true">*</span>
                  </span>
                  <select
                    required
                    disabled={!form.serviceBranchCategoryId}
                    value={form.serviceBranchId ?? ""}
                    onChange={(e) => update("serviceBranchId", nullableNumber(e.target.value))}
                  >
                    <option value="">
                      {form.serviceBranchCategoryId ? "Select branch" : "Select category first"}
                    </option>
                    {filteredServiceBranches.map((branch) => (
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
                  <span className="fieldLabelText">
                    Honoree description / tribute <span className="requiredMark" aria-hidden="true">*</span>
                  </span>
                  <textarea
                    required
                    rows={6}
                    value={form.description ?? ""}
                    onChange={(e) => update("description", e.target.value)}
                  />
                </label>
                <label className="wide">
                  Honoree photo
                  {(selectedPhotoPreviewUrl ||
                    (!isNominating && selectedClaim?.honoreeId
                      ? honoreePhotoUrl(selectedClaim.honoreeId)
                      : selectedClaim?.honoreeImageUrl)) ? (
                    <img
                      className="honoreePhotoPreview"
                      src={
                        selectedPhotoPreviewUrl ||
                        (!isNominating && selectedClaim?.honoreeId
                          ? honoreePhotoUrl(selectedClaim.honoreeId)
                          : selectedClaim?.honoreeImageUrl ?? "")
                      }
                      alt={`${selectedClaim?.honoreeName || "Current honoree"} photo`}
                      onError={(event) => {
                        event.currentTarget.style.display = "none";
                      }}
                    />
                  ) : null}
                  <input
                    type="file"
                    accept="image/*"
                    onChange={(e) => setSelectedPhoto(e.target.files?.[0] ?? null)}
                  />
                  <span className="helperText">
                    {selectedPhoto
                      ? `Selected: ${selectedPhoto.name}`
                      : !isNominating && (selectedClaim?.honoreeId || selectedClaim?.honoreeImageUrl)
                        ? "Current photo is shown above and will be kept unless a new one is uploaded."
                        : "Optional. JPG or PNG is best for the printed honoree card."}
                  </span>
                </label>


                <label>
                  <span className="fieldLabelText">
                    Submitter phone <span className="requiredMark" aria-hidden="true">*</span>
                  </span>
                  <input
                    required
                    value={form.submitterPhoneNumber ?? ""}
                    onChange={(e) => update("submitterPhoneNumber", e.target.value)}
                  />
                </label>

                <label>
                  <span className="fieldLabelText">
                    Submitter email <span className="requiredMark" aria-hidden="true">*</span>
                  </span>
                  <input
                    required
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
                  {!isNominating ? (
                    <button type="button" className="secondary" disabled={saving} onClick={saveDraft}>
                      {saving ? "Saving..." : "Save draft"}
                    </button>
                  ) : null}
                  <button type="submit" disabled={saving}>
                    {saving
                      ? "Submitting..."
                      : isNominating
                        ? "Submit nomination"
                        : selectedClaim?.claimStatus === "AdminDirectEdit"
                          ? "Save and queue reprint"
                          : "Submit changes for review"}
                  </button>
                </div>
              </form>
            </section>
          ) : null}


            </>
          ) : null}
    </main>
  );
}
