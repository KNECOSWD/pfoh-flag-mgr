import { matchKindLabel, matchReasonSentence, samePersonNoLabel, similarEntryLead } from "./submissionDuplicate.js";

export type SubmissionMatchView = {
  kind: "Approved" | "Pending" | string;
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

export function SubmissionDuplicatePrompt({
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
