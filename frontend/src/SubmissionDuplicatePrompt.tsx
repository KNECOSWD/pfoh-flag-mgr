import { matchKindLabel, similarEntryHelp, similarEntryLead, updateChoiceHelp } from "./submissionDuplicate.js";

export type SubmissionMatchView = {
  kind: "Approved" | "Pending" | string;
  honoreeId?: number | null;
  changeRequestId?: number | null;
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
  proceedText,
  isAdmin,
  onUpdate,
  onProceed,
  onCancel
}: {
  matches: SubmissionMatchView[];
  proceedText: string;
  isAdmin: boolean;
  onUpdate: (match: SubmissionMatchView) => void;
  onProceed: () => void;
  onCancel: () => void;
}) {
  return (
    <div className="col-12" role="region" aria-label="Similar honoree">
      <div className="alert alert-warning" role="status">
        <p className="mb-1 fw-semibold">{similarEntryLead}</p>
        <p className="mb-0">{similarEntryHelp}</p>
      </div>
      {matches.map((match) => {
        const details = [match.rank, match.branch].filter(Boolean).join(", ");
        return (
          <article
            className="border rounded p-3 mb-3 bg-white"
            key={`${match.kind}-${match.honoreeId ?? "new"}-${match.changeRequestId ?? "none"}`}
          >
            <div className="d-flex flex-wrap justify-content-between align-items-start gap-2 mb-2">
              <h3 className="h5 mb-0">{match.name}</h3>
              <span className="badge text-bg-primary">{matchKindLabel(match.kind)}</span>
            </div>
            {details ? <p className="mb-1">{details}</p> : null}
            {match.flagLocation ? <p className="mb-1">Flag location: {match.flagLocation}</p> : null}
            {match.reason ? <p className="mb-2">{match.reason}</p> : null}
            <p className="mb-3">
              {updateChoiceHelp({
                kind: match.kind,
                canUpdateDirectly: match.canUpdateDirectly,
                isAdmin
              })}
            </p>
            <button type="button" className="btn btn-primary" onClick={() => onUpdate(match)}>
              Update this entry
            </button>
          </article>
        );
      })}
      <div className="d-flex flex-wrap gap-2">
        <button type="button" className="btn btn-outline-primary" onClick={onProceed}>
          {proceedText}
        </button>
        <button type="button" className="btn btn-link" onClick={onCancel}>
          Back to the form
        </button>
      </div>
    </div>
  );
}
