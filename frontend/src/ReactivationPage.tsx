import { FormEvent, useEffect, useState } from "react";
import type { AccountInfo, IPublicClientApplication } from "@azure/msal-browser";
import {
  reactivationApi,
  type ReactivationLinkState,
  type ReactivationSummary
} from "./api";

function Summary({ honoree }: { honoree: ReactivationSummary }) {
  const rows: { label: string; value: string }[] = [
    { label: "Name", value: honoree.name }
  ];
  if (honoree.rank) rows.push({ label: "Rank", value: honoree.rank });
  if (honoree.serviceBranch) rows.push({ label: "Service branch", value: honoree.serviceBranch });
  if (honoree.years) rows.push({ label: "Years", value: honoree.years });
  if (honoree.conflictsServed) rows.push({ label: "Conflicts served", value: honoree.conflictsServed });
  if (honoree.awards) rows.push({ label: "Awards", value: honoree.awards });
  if (honoree.description) rows.push({ label: "Description", value: honoree.description });
  rows.push({ label: "Killed in action", value: honoree.kia ? "Yes" : "No" });

  return (
    <div className="mb-3">
      {rows.map((row) => (
        <div className="row g-2 mb-2" key={row.label}>
          <div className="col-12 col-sm-4 fw-semibold">{row.label}</div>
          <div className="col-12 col-sm-8">{row.value}</div>
        </div>
      ))}
    </div>
  );
}

export function ReactivationPage({
  token,
  instance,
  account,
  onSignIn,
  onCreateAccount,
  onUseDifferentAccount
}: {
  token: string;
  instance: IPublicClientApplication;
  account: AccountInfo | null;
  onSignIn: () => void;
  onCreateAccount: () => void;
  onUseDifferentAccount: () => void;
}) {
  const [state, setState] = useState<ReactivationLinkState | null>(null);
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const [formError, setFormError] = useState("");

  useEffect(() => {
    let cancelled = false;
    setFormError("");
    const load = account
      ? reactivationApi.inspect(instance, account, token)
      : reactivationApi.inspectAnonymous(token);
    load
      .then((next) => {
        if (!cancelled) setState(next);
      })
      .catch(() => {
        if (!cancelled) {
          setState({
            status: "invalid",
            message: "This reactivation link cannot be used.",
            honoree: null
          });
        }
      });
    return () => {
      cancelled = true;
    };
  }, [token, account, instance, account?.homeAccountId]);

  async function onSubmit(event: FormEvent) {
    event.preventDefault();
    if (!account) return;

    setBusy(true);
    setFormError("");
    try {
      const next = await reactivationApi.submit(instance, account, token, note);
      setState(next);
    } catch (error) {
      const message = error instanceof Error ? error.message : "";
      setFormError(message.includes("2000") ? "The note must be 2000 characters or fewer." : "The request could not be submitted. Try again.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="card card-body" aria-labelledby="reactivation-title">
      <h2 id="reactivation-title" className="h4">Request reactivation</h2>
      {!state ? <p role="status">Checking this link.</p> : null}
      {state ? <p>{state.message}</p> : null}
      {state?.status === "sign-in" ? (
        <div className="d-flex flex-column flex-sm-row gap-2">
          <button type="button" className="btn btn-primary" onClick={onSignIn}>Sign in</button>
          <button type="button" className="secondary" onClick={onCreateAccount}>Create account</button>
        </div>
      ) : null}
      {state?.status === "email-mismatch" ? (
        <div>
          <button type="button" className="secondary" onClick={onUseDifferentAccount}>Use a different account</button>
        </div>
      ) : null}
      {state?.status === "ready" && state.honoree ? (
        <form onSubmit={(event) => { void onSubmit(event); }}>
          <Summary honoree={state.honoree} />
          <div className="mb-3">
            <label className="form-label" htmlFor="reactivation-note">Note for the administrator (optional)</label>
            <textarea
              id="reactivation-note"
              className="form-control"
              value={note}
              maxLength={2000}
              rows={4}
              onChange={(event) => setNote(event.target.value)}
            />
          </div>
          {formError ? <p className="alert alert-danger" role="alert">{formError}</p> : null}
          <button type="submit" className="btn btn-primary" disabled={busy}>
            {busy ? "Submitting..." : "Request reactivation"}
          </button>
        </form>
      ) : null}
      {state?.status === "invalid" ? (
        <p className="mb-0">
          <a href="/find">Back to search</a>
        </p>
      ) : null}
    </section>
  );
}
