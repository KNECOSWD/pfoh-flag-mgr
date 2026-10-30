import { FormEvent, useEffect, useState } from "react";
import type { AccountInfo, IPublicClientApplication } from "@azure/msal-browser";
import {
  reactivationApi,
  type ReactivationLinkState,
  type ReactivationSummary
} from "./api";

export type ReactivationPreview = "invalid" | "sign-in" | "mismatch" | "ready" | "done";

const previewSummary: ReactivationSummary = {
  name: "TEST Reactivation Honoree",
  rank: "Sergeant",
  serviceBranch: "Army",
  years: "1968-1970",
  conflictsServed: "Example conflict",
  awards: "Example award",
  description: "A clearly fake tribute used only to preview this screen.",
  kia: false
};

function previewState(preview: ReactivationPreview): ReactivationLinkState {
  if (preview === "invalid") {
    return { status: "invalid", message: "This reactivation link cannot be used.", honoree: null };
  }
  if (preview === "sign-in") {
    return {
      status: "sign-in",
      message: "Sign in with the email address this link was sent to, or create an account with that email.",
      honoree: null
    };
  }
  if (preview === "mismatch") {
    return {
      status: "email-mismatch",
      message: "This link was sent to a different email address. Sign in with that address to request reactivation.",
      honoree: null
    };
  }
  if (preview === "done") {
    return {
      status: "submitted",
      message: "Your reactivation request was submitted. An administrator will review it before the honoree can return to the field.",
      honoree: null
    };
  }
  return {
    status: "ready",
    message: "Review the honoree below, then request reactivation. An administrator reviews the request before the honoree can return to the field.",
    honoree: previewSummary
  };
}

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
    <dl className="reactivationFacts">
      {rows.map((row) => (
        <div key={row.label}>
          <dt>{row.label}</dt>
          <dd>{row.value}</dd>
        </div>
      ))}
    </dl>
  );
}

export function ReactivationPage({
  token,
  instance,
  account,
  preview,
  onSignIn,
  onCreateAccount,
  onUseDifferentAccount
}: {
  token: string;
  instance: IPublicClientApplication;
  account: AccountInfo | null;
  preview?: ReactivationPreview | null;
  onSignIn: () => void;
  onCreateAccount: () => void;
  onUseDifferentAccount: () => void;
}) {
  const [state, setState] = useState<ReactivationLinkState | null>(preview ? previewState(preview) : null);
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const [formError, setFormError] = useState("");

  useEffect(() => {
    if (preview) {
      setState(previewState(preview));
      return;
    }

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
  }, [token, account, instance, preview, account?.homeAccountId]);

  async function onSubmit(event: FormEvent) {
    event.preventDefault();
    if (!account || preview) {
      if (preview === "ready") {
        setState(previewState("done"));
      }
      return;
    }

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
    <section className="reactivationScreen" aria-labelledby="reactivation-title">
      <div className="reactivationCard">
        <h2 id="reactivation-title">Request reactivation</h2>
        {!state ? <p role="status">Checking this link.</p> : null}
        {state ? <p>{state.message}</p> : null}
        {state?.status === "sign-in" ? (
          <div className="reactivationActions">
            <button type="button" onClick={onSignIn}>Sign in</button>
            <button type="button" className="secondary" onClick={onCreateAccount}>Create account</button>
          </div>
        ) : null}
        {state?.status === "email-mismatch" ? (
          <div className="reactivationActions">
            <button type="button" className="secondary" onClick={onUseDifferentAccount}>Use a different account</button>
          </div>
        ) : null}
        {state?.status === "ready" && state.honoree ? (
          <form onSubmit={(event) => { void onSubmit(event); }}>
            <Summary honoree={state.honoree} />
            <label className="reactivationNote">
              Note for the administrator (optional)
              <textarea
                value={note}
                maxLength={2000}
                onChange={(event) => setNote(event.target.value)}
              />
            </label>
            {formError ? <p className="reactivationMessage" role="alert">{formError}</p> : null}
            <div className="reactivationActions">
              <button type="submit" disabled={busy}>{busy ? "Submitting..." : "Request reactivation"}</button>
            </div>
          </form>
        ) : null}
        {state?.status === "invalid" ? (
          <p>
            <a href="/find">Back to search</a>
          </p>
        ) : null}
      </div>
    </section>
  );
}
