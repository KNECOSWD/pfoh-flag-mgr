import { useEffect, useMemo, useState } from "react";
import type { AccountInfo, IPublicClientApplication } from "@azure/msal-browser";
import {
  ApiError,
  adminApi,
  type DuplicateClaimant,
  type DuplicateGroup,
  type DuplicateHonoree,
  type DuplicateMergeResult
} from "./api";

const fields: { key: string; label: string; value: (honoree: DuplicateHonoree) => string }[] = [
  { key: "firstName", label: "First name", value: (honoree) => honoree.firstName },
  { key: "middleName", label: "Middle name", value: (honoree) => honoree.middleName || "" },
  { key: "lastName", label: "Last name", value: (honoree) => honoree.lastName },
  { key: "suffix", label: "Suffix", value: (honoree) => honoree.suffix || "" },
  { key: "nickname", label: "Nickname", value: (honoree) => honoree.nickname || "" },
  { key: "rank", label: "Rank", value: (honoree) => honoree.rank },
  { key: "serviceBranch", label: "Service branch", value: (honoree) => honoree.serviceBranchName || "" },
  { key: "serviceYears", label: "Service years", value: (honoree) => honoree.serviceYears },
  { key: "conflictsServed", label: "Conflicts served", value: (honoree) => honoree.conflictsServed },
  { key: "awards", label: "Awards", value: (honoree) => honoree.awards },
  { key: "description", label: "Bio", value: (honoree) => honoree.description },
  { key: "kia", label: "Killed in action", value: (honoree) => (honoree.kia ? "Yes" : "No") },
  { key: "photo", label: "Photo", value: (honoree) => honoree.photoFileName || "No photo" },
  { key: "flagGrid", label: "Flag grid", value: (honoree) => honoree.flagGridName || "No flag grid" },
  { key: "phone", label: "Phone", value: (honoree) => honoree.phoneNumber },
  { key: "email", label: "Email", value: (honoree) => honoree.emailAddress },
  { key: "sponsor", label: "Sponsor", value: (honoree) => honoree.sponsorName }
];

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
  loadError,
  busy,
  onMerge,
  onReload
}: {
  groups: DuplicateGroup[];
  loadError: string;
  busy: boolean;
  onMerge: (body: { survivorId: number; duplicateIds: number[]; fields: Record<string, number> }) => Promise<DuplicateMergeResult>;
  onReload: () => void;
}) {
  const [openKey, setOpenKey] = useState<string | null>(null);
  const [confirmed, setConfirmed] = useState<number[]>([]);
  const [step, setStep] = useState<"list" | "confirm" | "merge" | "done">("list");
  const [survivorId, setSurvivorId] = useState<number | null>(null);
  const [choices, setChoices] = useState<Record<string, number | null>>({});
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");
  const [result, setResult] = useState<DuplicateMergeResult | null>(null);

  const group = groups.find((item) => item.groupKey === openKey) ?? null;
  const confirmedHonorees = useMemo(
    () => (group ? group.honorees.filter((honoree) => confirmed.includes(honoree.id)) : []),
    [group, confirmed]
  );
  const keptClaimants = useMemo(() => uniqueClaimants(confirmedHonorees), [confirmedHonorees]);

  function openGroup(next: DuplicateGroup) {
    setOpenKey(next.groupKey);
    setConfirmed([]);
    setSurvivorId(null);
    setChoices({});
    setResult(null);
    setError("");
    setMessage("");
    setStep("confirm");
  }

  function toggleConfirmed(id: number) {
    setConfirmed((current) => (current.includes(id) ? current.filter((item) => item !== id) : [...current, id]));
  }

  async function merge() {
    if (!group || survivorId == null) {
      setError("Choose the surviving record from the confirmed duplicates. Nothing is selected automatically.");
      return;
    }
    const missing = fields.some((field) => choices[field.key] == null);
    if (missing) {
      setError("Choose a value for every field before merging.");
      return;
    }
    setError("");
    const body = {
      survivorId,
      duplicateIds: confirmedHonorees.map((honoree) => honoree.id),
      fields: Object.fromEntries(fields.map((field) => [field.key, choices[field.key] as number]))
    };
    try {
      const merged = await onMerge(body);
      setResult(merged);
      setStep("done");
      setMessage(`${merged.survivorName} now has the chosen values. The other confirmed records are inactive and their flag spots were released.`);
    } catch (err) {
      setError(err instanceof ApiError ? err.serverMessage || err.message : "The merge could not be saved.");
    }
  }

  return (
    <section className="card adminCard duplicateMergeCard" aria-label="Duplicate honorees">
      <div className="sectionHeader">
        <div>
          <p className="eyebrow">Administrator</p>
          <h2>
            Duplicate honorees
            <span className="countBadge">{groups.length}</span>
          </h2>
          <p className="helperText">
            Likely duplicates are listed for review. Nothing is merged, removed, or edited until you confirm a group and choose the values to keep.
          </p>
        </div>
        <button type="button" className="secondary" onClick={onReload} disabled={busy}>
          {busy ? "Loading..." : "Refresh"}
        </button>
      </div>

      {loadError ? <p className="message error" role="alert">{loadError}</p> : null}
      {error ? <p className="message error" role="alert">{error}</p> : null}
      {message ? <p className="message" role="status">{message}</p> : null}

      {step === "list" ? (
        groups.length === 0 ? (
          <p className="compactEmptyState">No likely duplicate honorees were found.</p>
        ) : (
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
                  Open group
                </button>
              </article>
            ))}
          </div>
        )
      ) : null}

      {step === "confirm" && group ? (
        <div className="duplicateConfirm">
          <button type="button" className="secondary" onClick={() => setStep("list")}>
            Back to groups
          </button>
          <p className="duplicateLikelihood">How likely: {group.likelihood}</p>
          <p className="duplicateWhy">{group.explanation}</p>
          <h3>Why these may be duplicates</h3>
          <ul className="duplicatePairList">
            {group.pairs.map((pair) => (
              <li key={`${pair.leftId}-${pair.rightId}`}>
                Records {pair.leftId} and {pair.rightId}: {pair.explanation}
              </li>
            ))}
          </ul>
          <fieldset>
            <legend>Confirm the records that are actual duplicates</legend>
            {group.honorees.map((honoree) => (
              <label key={honoree.id} className="duplicateCheck">
                <input
                  type="checkbox"
                  checked={confirmed.includes(honoree.id)}
                  onChange={() => toggleConfirmed(honoree.id)}
                />
                <span>
                  <strong>{honoree.name}</strong> #{honoree.id}
                  {honoree.claimants.length > 0
                    ? ` · Claimed by ${honoree.claimants.map(claimantLabel).join(", ")}`
                    : " · Not claimed"}
                </span>
              </label>
            ))}
          </fieldset>
          <button
            type="button"
            disabled={confirmed.length < 2}
            onClick={() => {
              setSurvivorId(null);
              setChoices({});
              setStep("merge");
            }}
          >
            Continue to merge
          </button>
          {confirmed.length < 2 ? (
            <p className="helperText">Confirm at least two records before merging.</p>
          ) : null}
        </div>
      ) : null}

      {step === "merge" && group ? (
        <div className="duplicateMergeStep">
          <button type="button" className="secondary" onClick={() => setStep("confirm")}>
            Back to confirm
          </button>
          <h3>Choose the surviving record</h3>
          <p className="helperText">The chosen values are written into that existing record. No new record is created.</p>
          <div className="mergeBoard">
            {confirmedHonorees.map((honoree) => (
              <article key={honoree.id} className="mergeColumn">
                <label className="duplicateCheck">
                  <input
                    type="radio"
                    name="duplicate-survivor"
                    checked={survivorId === honoree.id}
                    onChange={() => setSurvivorId(honoree.id)}
                  />
                  <span>Keep record #{honoree.id}</span>
                </label>
                <h4>{honoree.name}</h4>
                {fields.map((field) => (
                  <label key={field.key} className="mergeField">
                    <input
                      type="radio"
                      name={`field-${field.key}`}
                      checked={choices[field.key] === honoree.id}
                      onChange={() => setChoices((current) => ({ ...current, [field.key]: honoree.id }))}
                    />
                    <span>
                      <strong>{field.label}</strong>
                      <em>{field.value(honoree) || "Blank"}</em>
                    </span>
                  </label>
                ))}
                <p className="duplicateClaimants">
                  <strong>Claimants</strong>
                  {honoree.claimants.length === 0
                    ? " None"
                    : ` ${honoree.claimants.map(claimantLabel).join(", ")}`}
                </p>
              </article>
            ))}
          </div>
          <section className="survivorClaimants" aria-label="Claimants the survivor will keep">
            <h3>Claimants the survivor will keep</h3>
            {keptClaimants.length === 0 ? (
              <p>None of the confirmed records is claimed.</p>
            ) : (
              <ul>
                {keptClaimants.map((claimant) => (
                  <li key={claimant.userId}>{claimantLabel(claimant)}</li>
                ))}
              </ul>
            )}
          </section>
          <button type="button" onClick={() => void merge()} disabled={busy || survivorId == null}>
            Merge into the surviving record
          </button>
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
  account
}: {
  instance: IPublicClientApplication;
  account: AccountInfo | null;
}) {
  const [groups, setGroups] = useState<DuplicateGroup[]>([]);
  const [loadError, setLoadError] = useState("");
  const [busy, setBusy] = useState(false);

  async function load() {
    if (!account) return;
    setBusy(true);
    try {
      setGroups(await adminApi.duplicateGroups(instance, account));
      setLoadError("");
    } catch (err) {
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
      loadError={loadError}
      busy={busy}
      onReload={() => void load()}
      onMerge={(body) => adminApi.mergeDuplicates(instance, account, body)}
    />
  );
}
