import { useEffect, useMemo, useRef, useState } from "react";
import type { AccountInfo, IPublicClientApplication } from "@azure/msal-browser";
import {
  ApiError,
  adminApi,
  type DuplicateClaimant,
  type DuplicateGroup,
  type DuplicateHonoree,
  type DuplicateMergeResult
} from "./api";
import { duplicateListState } from "./duplicateListState.js";
import {
  DuplicateReviewBoard,
  MergeActions,
  ResultingRecord,
  ReviewStepActions,
  SharedFieldChoices,
  StillToChoose,
  UseMatchingButton,
  applyMatchingValues,
  blankChoice,
  buildMergeRequest,
  canConfirmMerge,
  changedPreviewKeys,
  choicesAfterIncludedChange,
  previewRows,
  survivorIdFromChoices
} from "./duplicateReview.js";
import { isQuietAuthRejection, PLEASE_SIGN_IN_AGAIN } from "./tokenRequest.js";

type FieldChoice = {
  useCustom: boolean;
  sourceId: number | null;
  custom: string;
  explicit?: boolean;
  noPhoto?: boolean;
  combine?: boolean;
};

export type DuplicateMergeBody = {
  survivorId: number;
  duplicateIds: number[];
  fields: Record<string, number>;
  values?: Record<string, string>;
  combineAwards?: boolean;
};

function claimantLabel(claimant: DuplicateClaimant) {
  const name = claimant.name?.trim() || claimant.email;
  return claimant.email && claimant.name ? `${name} (${claimant.email})` : name;
}

function uniqueClaimants(honorees: DuplicateHonoree[]) {
  const seen = new Set<string>();
  const rows: DuplicateClaimant[] = [];
  for (const honoree of honorees) {
    for (const claimant of honoree.claimants) {
      if (seen.has(claimant.userId)) continue;
      seen.add(claimant.userId);
      rows.push(claimant);
    }
  }
  return rows;
}

export function DuplicateMergeScreen({
  groups,
  loaded,
  loadError,
  busy,
  onMerge,
  onPreviewCard,
  onNotDuplicates,
  onReload,
  onSignIn,
  onOpenHistory
}: {
  groups: DuplicateGroup[];
  loaded: boolean;
  loadError: string;
  busy: boolean;
  onMerge: (body: DuplicateMergeBody) => Promise<DuplicateMergeResult>;
  onPreviewCard: (body: DuplicateMergeBody) => Promise<Blob>;
  onNotDuplicates: (leftId: number, rightId: number) => Promise<void>;
  onReload: () => void;
  onSignIn?: () => void;
  onOpenHistory?: () => void;
}) {
  const [openKey, setOpenKey] = useState<string | null>(null);
  const [included, setIncluded] = useState<number[]>([]);
  const [step, setStep] = useState<"list" | "review" | "confirm" | "done">("list");
  const [choices, setChoices] = useState<Record<string, FieldChoice>>({});
  const [highlight, setHighlight] = useState<{ token: number; keys: string[] }>({ token: 0, keys: [] });
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");
  const [result, setResult] = useState<DuplicateMergeResult | null>(null);
  const [pendingPair, setPendingPair] = useState<{ leftId: number; rightId: number } | null>(null);

  const group = groups.find((item) => item.groupKey === openKey) ?? null;
  const listState = duplicateListState({ loaded, loadError, count: groups.length });
  const includedHonorees = useMemo(
    () => (group ? group.honorees.filter((honoree) => included.includes(honoree.id)) : []),
    [group, included]
  );
  const keptClaimants = useMemo(() => uniqueClaimants(includedHonorees), [includedHonorees]);
  const survivorId = survivorIdFromChoices(choices, included);
  const ready = canConfirmMerge(included, group?.honorees ?? [], choices);
  const preview = group ? previewRows(includedHonorees, choices) : null;
  const choicesRef = useRef(choices);
  choicesRef.current = choices;

  function flashChanges(
    before: Record<string, FieldChoice>,
    after: Record<string, FieldChoice>,
    records: DuplicateHonoree[]
  ) {
    const keys = changedPreviewKeys(records, before, after);
    if (!keys.length) return;
    setHighlight((current) => ({ token: current.token + 1, keys }));
  }

  function openGroup(next: DuplicateGroup) {
    const ids = next.honorees.map((honoree) => honoree.id);
    setOpenKey(next.groupKey);
    setIncluded(ids);
    setChoices(choicesAfterIncludedChange(next.honorees, {}) as Record<string, FieldChoice>);
    setHighlight({ token: 0, keys: [] });
    setResult(null);
    setError("");
    setMessage("");
    setPendingPair(null);
    setStep("review");
  }

  function toggleIncluded(id: number) {
    setIncluded((current) => {
      const removing = current.includes(id);
      const next = removing ? current.filter((item) => item !== id) : [...current, id];
      const includedNow = (group?.honorees ?? []).filter((honoree) => next.includes(honoree.id));
      setChoices((existing) => choicesAfterIncludedChange(includedNow, existing) as Record<string, FieldChoice>);
      return next;
    });
  }

  function chooseSource(key: string, sourceId: number) {
    const before = choicesRef.current;
    const nextChoices = {
      ...before,
      [key]: { ...blankChoice(), sourceId, explicit: true }
    };
    const nextIncluded = included.includes(sourceId) ? included : [...included, sourceId];
    const records = (group?.honorees ?? []).filter((honoree) => nextIncluded.includes(honoree.id));
    setIncluded(nextIncluded);
    setChoices(nextChoices);
    flashChanges(before, nextChoices, records);
  }

  function chooseNoPhoto() {
    const before = choicesRef.current;
    const nextChoices = {
      ...before,
      photo: { ...blankChoice(), explicit: true, noPhoto: true }
    };
    setChoices(nextChoices);
    flashChanges(before, nextChoices, includedHonorees);
  }

  function chooseCombine() {
    const before = choicesRef.current;
    const nextChoices = {
      ...before,
      awards: { ...blankChoice(), explicit: true, combine: true }
    };
    setChoices(nextChoices);
    flashChanges(before, nextChoices, includedHonorees);
  }

  function editPreview(key: string, custom: string) {
    const before = choicesRef.current;
    const nextChoices = {
      ...before,
      [key]: {
        ...(before[key] ?? blankChoice()),
        useCustom: true,
        sourceId: null,
        custom,
        explicit: true,
        noPhoto: false,
        combine: false
      }
    };
    setChoices(nextChoices);
    flashChanges(before, nextChoices, includedHonorees);
  }

  function editGrid(custom: string) {
    const before = choicesRef.current;
    const prior = before.flagGrid ?? blankChoice();
    const source = includedHonorees.find((honoree) => honoree.id === prior.sourceId);
    const normalized = custom.replace(/\s+/g, " ").trim();
    const sourceText = (source?.flagGridName || "").replace(/\s+/g, " ").trim();
    const changed = !source || normalized !== sourceText;
    const nextChoices = {
      ...before,
      flagGrid: {
        ...prior,
        useCustom: changed,
        custom,
        explicit: true
      }
    };
    setChoices(nextChoices);
    flashChanges(before, nextChoices, includedHonorees);
  }

  function useMatching() {
    const before = choicesRef.current;
    const nextChoices = applyMatchingValues(includedHonorees, before) as Record<string, FieldChoice>;
    setChoices(nextChoices);
    flashChanges(before, nextChoices, includedHonorees);
  }

  async function confirmNotDuplicates() {
    if (!pendingPair) return;
    setError("");
    try {
      await onNotDuplicates(pendingPair.leftId, pendingPair.rightId);
      setPendingPair(null);
      setStep("list");
      setOpenKey(null);
      setMessage("Those two records will no longer be listed as duplicates of each other. Other pairs stay under review.");
      onReload();
    } catch (err) {
      setError(err instanceof ApiError ? err.serverMessage || err.message : "The decision could not be saved.");
    }
  }

  async function merge() {
    if (!group || !ready || survivorId == null) {
      setError("Choose a value for every field. The record whose flag grid you select is the one that is kept.");
      return;
    }
    setError("");
    const body = buildMergeRequest(includedHonorees, choices);
    try {
      const merged = await onMerge(body);
      setResult(merged);
      setStep("done");
      setMessage(`${merged.survivorName} now has the chosen values. The other confirmed records are inactive and their flag spots were released.`);
    } catch (err) {
      setError(err instanceof ApiError ? err.serverMessage || err.message : "The merge could not be saved.");
    }
  }

  async function previewCard() {
    if (!group || !ready) {
      setError("Choose a value for every field before previewing the card.");
      return;
    }
    setError("");
    const tab = window.open("", "_blank");
    try {
      const blob = await onPreviewCard(buildMergeRequest(includedHonorees, choices));
      const url = URL.createObjectURL(blob);
      if (tab && !tab.closed) tab.location.href = url;
      else window.open(url, "_blank", "noopener");
    } catch (err) {
      tab?.close();
      setError(err instanceof ApiError ? err.serverMessage || err.message : "The card preview could not be created.");
    }
  }

  const claimants = (
    <section className="survivorClaimants" aria-label="Claimants the merged record will keep">
      <h3>Claimants the merged record will keep</h3>
      {keptClaimants.length === 0 ? (
        <p>None of the included records is claimed.</p>
      ) : (
        <ul>
          {keptClaimants.map((claimant) => (
            <li key={claimant.userId}>{claimantLabel(claimant)}</li>
          ))}
        </ul>
      )}
    </section>
  );

  return (
    <section className="card adminCard duplicateMergeCard" aria-label="Duplicate honorees">
      <div className="sectionHeader">
        <div>
          <p className="eyebrow">Administrator</p>
          <h2>
            Duplicate honorees
            {listState.showCount ? <span className="countBadge">{groups.length}</span> : null}
          </h2>
          <p className="helperText">
            Likely duplicates are listed for review. Only active records are included. Open a group to compare the records side by side. Nothing is merged until every field has a value and you confirm. A pair you mark as not duplicates is left out of this list.
          </p>
        </div>
        <div className="adminHeaderActions">
          {onOpenHistory ? (
            <button type="button" className="secondary" onClick={onOpenHistory}>
              Merge history
            </button>
          ) : null}
          <button type="button" className="secondary" onClick={onReload} disabled={busy}>
            {busy ? "Loading..." : "Refresh"}
          </button>
        </div>
      </div>

      {listState.showError ? (
        <div className="duplicateLoadFailure">
          <p className="message error" role="alert">{loadError}</p>
          {listState.showRetry ? (
            <button type="button" onClick={onReload}>Retry</button>
          ) : null}
          {loadError === PLEASE_SIGN_IN_AGAIN && onSignIn ? (
            <button type="button" onClick={onSignIn}>Sign in</button>
          ) : null}
        </div>
      ) : null}
      {error ? <p className="message error" role="alert">{error}</p> : null}
      {message ? <p className="message" role="status">{message}</p> : null}

      {listState.showEmpty ? (
        <p className="compactEmptyState">No likely duplicate honorees were found.</p>
      ) : null}

      {listState.showList && step === "list" ? (
          <div className="duplicateGroupList">
            {groups.map((item, index) => (
              <article key={item.groupKey} className="duplicateGroup">
                <header>
                  <h3>Group {index + 1}</h3>
                  <p className="duplicateLikelihood">How likely: {item.likelihood}</p>
                </header>
                <p className="duplicateWhy">{item.explanation}</p>
                <ul className="duplicateMemberList">
                  {item.honorees.map((honoree) => (
                    <li key={honoree.id}>
                      <strong>{honoree.name}</strong>
                      <span>#{honoree.id}</span>
                      <span>{honoree.serviceBranchName || "No branch"}</span>
                      <span>{honoree.flagGridName || "No flag grid"}</span>
                    </li>
                  ))}
                </ul>
                <button type="button" onClick={() => openGroup(item)}>
                  Review side by side
                </button>
              </article>
            ))}
          </div>
      ) : null}

      {step === "review" && group ? (
        <div className="duplicateMergeStep">
          <button type="button" className="secondary" onClick={() => setStep("list")}>
            Back to groups
          </button>
          <p className="duplicateLikelihood">How likely: {group.likelihood}</p>
          <p className="duplicateWhy">{group.explanation}</p>
          <h3>Why these may be duplicates</h3>
          <ul className="duplicatePairList">
            {group.pairs.map((pair) => {
              const confirming = pendingPair?.leftId === pair.leftId && pendingPair.rightId === pair.rightId;
              return (
                <li key={`${pair.leftId}-${pair.rightId}`}>
                  <p>Records {pair.leftId} and {pair.rightId}: {pair.explanation}</p>
                  {confirming ? (
                    <div className="notDuplicateConfirm" role="group" aria-label="Confirm not duplicates">
                      <p>
                        Mark records {pair.leftId} and {pair.rightId} as not duplicates? They will not be listed together again. Other pairs in this group stay under review.
                      </p>
                      <button type="button" disabled={busy} onClick={() => void confirmNotDuplicates()}>
                        Confirm not duplicates
                      </button>
                      <button type="button" className="secondary" onClick={() => setPendingPair(null)}>
                        Cancel
                      </button>
                    </div>
                  ) : (
                    <button
                      type="button"
                      className="secondary"
                      disabled={busy}
                      onClick={() => setPendingPair({ leftId: pair.leftId, rightId: pair.rightId })}
                    >
                      Not duplicates
                    </button>
                  )}
                </li>
              );
            })}
          </ul>
          <h3>Choose each field</h3>
          <p className="helperText">
            Fields that match exactly are preselected. Everything else, including the flag grid, needs a pick. Choose a value on each included record. Use all matching values fills only the fields that are the same. The record whose flag grid you select is the one that is kept, even when that grid is blank.
          </p>
          <UseMatchingButton disabled={includedHonorees.length < 2} onClick={useMatching} />
          <DuplicateReviewBoard
            honorees={group.honorees}
            includedIds={included}
            choices={choices}
            onToggleIncluded={toggleIncluded}
            onChooseSource={chooseSource}
          />
          <SharedFieldChoices
            includedCount={includedHonorees.length}
            choices={choices}
            onNoPhoto={chooseNoPhoto}
            onCombine={chooseCombine}
          />
          <ResultingRecord
            honorees={includedHonorees}
            choices={choices}
            onEdit={editPreview}
            onGrid={editGrid}
            highlightKeys={highlight.keys}
            highlightToken={highlight.token}
          />
          {claimants}
          <ReviewStepActions
            ready={ready}
            busy={busy}
            onPreview={() => void previewCard()}
            onContinue={() => setStep("confirm")}
          />
          <StillToChoose includedIds={included} choices={choices} />
        </div>
      ) : null}

      {step === "confirm" && group && preview ? (
        <div className="duplicateMergeStep">
          <button type="button" className="secondary" onClick={() => setStep("review")}>
            Back to review
          </button>
          <h3>Confirm the merged record</h3>
          <p className="helperText">
            These values will be written onto the record whose flag grid you selected. You can still edit a text field here. An edited field is marked custom and replaces that field's pick. The other included records will be set inactive and their flag spots released. Claimants move to the kept record.
          </p>
          <ResultingRecord
            honorees={includedHonorees}
            choices={choices}
            onEdit={editPreview}
            onGrid={editGrid}
            highlightKeys={highlight.keys}
            highlightToken={highlight.token}
          />
          {claimants}
          <MergeActions
            ready={ready}
            busy={busy}
            onMerge={() => void merge()}
            onPreview={() => void previewCard()}
          />
          <StillToChoose includedIds={included} choices={choices} />
        </div>
      ) : null}

      {step === "done" && result ? (
        <section className="survivorClaimants" aria-label="Survivor claimants">
          <h3>{result.survivorName} claimants</h3>
          {result.claimants.length === 0 ? (
            <p>The surviving record is not claimed.</p>
          ) : (
            <ul>
              {result.claimants.map((claimant) => (
                <li key={claimant.userId}>{claimantLabel(claimant)}</li>
              ))}
            </ul>
          )}
          <button
            type="button"
            onClick={() => {
              setStep("list");
              setOpenKey(null);
              onReload();
            }}
          >
            Back to groups
          </button>
        </section>
      ) : null}
    </section>
  );
}

export function DuplicateMerge({
  instance,
  account,
  onSignIn,
  onOpenHistory
}: {
  instance: IPublicClientApplication;
  account: AccountInfo | null;
  onSignIn?: () => void;
  onOpenHistory?: () => void;
}) {
  const [groups, setGroups] = useState<DuplicateGroup[]>([]);
  const [loaded, setLoaded] = useState(false);
  const [loadError, setLoadError] = useState("");
  const [busy, setBusy] = useState(false);

  async function load() {
    if (!account) return;
    setBusy(true);
    try {
      setGroups(await adminApi.duplicateGroups(instance, account));
      setLoadError("");
      setLoaded(true);
    } catch (err) {
      if (isQuietAuthRejection(err)) return;
      setGroups([]);
      setLoaded(false);
      setLoadError(err instanceof ApiError ? err.serverMessage || err.message : "The duplicate list could not be loaded.");
    } finally {
      setBusy(false);
    }
  }

  useEffect(() => {
    void load();
    // The list is loaded when the signed-in administrator opens this page.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [account?.homeAccountId]);

  if (!account) return null;

  return (
    <DuplicateMergeScreen
      groups={groups}
      loaded={loaded}
      loadError={loadError}
      busy={busy}
      onReload={() => void load()}
      onSignIn={onSignIn}
      onMerge={(body) => adminApi.mergeDuplicates(instance, account, body)}
      onPreviewCard={(body) => adminApi.previewMergeCard(instance, account, body)}
      onNotDuplicates={async (leftId, rightId) => {
        await adminApi.markNotDuplicates(instance, account, leftId, rightId);
      }}
      onOpenHistory={onOpenHistory}
    />
  );
}
