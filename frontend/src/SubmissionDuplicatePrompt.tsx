import { useEffect, useId, useRef } from "react";
import { matchKindLabel, matchReasonSentence, publicDuplicateHeading, samePersonLabel, samePersonNoLabel, similarEntryLead, submitAsNewLabel } from "./submissionDuplicate.js";

export type SubmissionMatchView = {
  kind?: "Approved" | "Pending" | string;
  honoreeId?: number | null;
  changeRequestId?: number | null;
  matchToken?: string | null;
  name: string;
  rank?: string | null;
  branch?: string | null;
  flagLocation?: string | null;
  likelihood?: string;
  reason?: string;
  canUpdateDirectly?: boolean;
};

function PublicDuplicateCard({
  matches,
  onYes,
  onNo
}: {
  matches: SubmissionMatchView[];
  onYes: (match: SubmissionMatchView) => void;
  onNo: () => void;
}) {
  const match = matches[0];
  const regionRef = useRef<HTMLDivElement>(null);
  const headingId = useId();
  useEffect(() => {
    regionRef.current?.focus();
  }, []);

  if (!match) return null;

  const details = [match.rank, match.branch].filter(Boolean).join(", ");
  return (
    <div
      className="col-12"
      role="region"
      aria-labelledby={headingId}
      tabIndex={-1}
      ref={regionRef}
    >
      <h3 id={headingId} className="fs-5 fw-semibold mb-3">{publicDuplicateHeading}</h3>
      <article className="border rounded p-3 mb-3 bg-white">
        <strong className="d-block fw-semibold mb-2">{match.name}</strong>
        {details ? <p className="mb-1">{details}</p> : null}
        {match.flagLocation ? <p className="mb-1">Flag location: {match.flagLocation}</p> : null}
        {match.reason ? <p className="mb-0">{matchReasonSentence(match.reason)}</p> : null}
      </article>
      <div className="row g-2" role="group" aria-label="Choose how to submit">
        <div className="col-12 col-sm-6 d-grid">
          <button type="button" className="btn btn-outline-primary" onClick={() => onYes(match)}>
            {samePersonLabel}
          </button>
        </div>
        <div className="col-12 col-sm-6 d-grid">
          <button type="button" className="btn btn-outline-primary" onClick={onNo}>
            {submitAsNewLabel}
          </button>
        </div>
      </div>
    </div>
  );
}

function AdminDuplicatePrompt({
  matches,
  onYes,
  onNo
}: {
  matches: SubmissionMatchView[];
  onYes: (match: SubmissionMatchView) => void;
  onNo: () => void;
}) {
  const many = matches.length > 1;
  return (
    <div className="col-12" role="region" aria-label="Similar honoree">
      <p className="mb-3 fw-semibold">{similarEntryLead}</p>
      {matches.map((match) => {
        const details = [match.rank, match.branch].filter(Boolean).join(", ");
        return (
          <article
            className="border rounded p-3 mb-3 bg-white"
            key={`${match.kind}-${match.honoreeId ?? "new"}-${match.changeRequestId ?? match.matchToken ?? "none"}`}
          >
            <div className="d-flex flex-wrap justify-content-between align-items-start gap-2 mb-2">
              <h3 className="h5 mb-0">{match.name}</h3>
              <span className="badge text-bg-primary">{matchKindLabel(match.kind)}</span>
            </div>
            {details ? <p className="mb-1">{details}</p> : null}
            {match.flagLocation ? <p className="mb-1">Flag location: {match.flagLocation}</p> : null}
            {match.reason ? <p className="mb-3">{matchReasonSentence(match.reason)}</p> : null}
            <button
              type="button"
              className={many ? "btn btn-outline-primary" : "btn btn-primary"}
              onClick={() => onYes(match)}
            >
              Yes
            </button>
          </article>
        );
      })}
      <button type="button" className={many ? "btn btn-primary" : "btn btn-outline-primary"} onClick={onNo}>
        {samePersonNoLabel(matches.length)}
      </button>
    </div>
  );
}

export function SubmissionDuplicatePrompt({
  matches,
  variant,
  onYes,
  onNo
}: {
  matches: SubmissionMatchView[];
  variant: "public" | "admin";
  onYes: (match: SubmissionMatchView) => void;
  onNo: () => void;
}) {
  if (variant === "admin") {
    return <AdminDuplicatePrompt matches={matches} onYes={onYes} onNo={onNo} />;
  }

  return <PublicDuplicateCard matches={matches} onYes={onYes} onNo={onNo} />;
}
