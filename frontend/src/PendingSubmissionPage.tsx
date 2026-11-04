import { useEffect, useRef, useState } from "react";
import { submissionApi, type SubmissionReceipt } from "./api";
import { submissionReference } from "./submissionDuplicate.js";

export function PendingSubmissionPage({
  token,
  onResolved
}: {
  token: string;
  onResolved?: (state: "found" | "missing") => void;
}) {
  const [receipt, setReceipt] = useState<SubmissionReceipt | null>(null);
  const [error, setError] = useState("");
  const onResolvedRef = useRef(onResolved);
  onResolvedRef.current = onResolved;

  useEffect(() => {
    const previous = document.querySelector('meta[name="robots"]')?.getAttribute("content") ?? "";
    let meta = document.querySelector('meta[name="robots"]');
    if (!meta) {
      meta = document.createElement("meta");
      meta.setAttribute("name", "robots");
      document.head.appendChild(meta);
    }
    meta.setAttribute("content", "noindex, nofollow");
    return () => {
      if (previous) meta?.setAttribute("content", previous);
      else meta?.remove();
    };
  }, []);

  useEffect(() => {
    let cancelled = false;
    setError("");
    setReceipt(null);
    submissionApi
      .receipt(token)
      .then((body) => {
        if (cancelled) return;
        setReceipt(body);
        onResolvedRef.current?.("found");
      })
      .catch(() => {
        if (cancelled) return;
        setError("This submission link is not valid.");
        onResolvedRef.current?.("missing");
      });
    return () => {
      cancelled = true;
    };
  }, [token]);

  if (error) {
    return (
      <section className="card formCard" aria-label="Submission">
        <h2>Submission not found</h2>
        <p className="message error" role="alert">{error}</p>
      </section>
    );
  }

  if (!receipt) {
    return (
      <section className="card formCard" aria-label="Submission">
        <p>Loading this submission.</p>
      </section>
    );
  }

  return (
    <section className="card formCard" aria-label="Submission received">
      <div className="sectionHeader">
        <div>
          <p className="eyebrow">Submission received</p>
          <h2>Pending review</h2>
        </div>
      </div>
      <div className="row g-3">
        <div className="col-12 col-md-6">
          <p className="mb-1 fw-semibold">Status</p>
          <p>{receipt.status}</p>
        </div>
        <div className="col-12 col-md-6">
          <p className="mb-1 fw-semibold">Reference</p>
          <p className="text-break">{submissionReference(receipt.reference)}</p>
        </div>
        <div className="col-12">
          <p className="mb-1 fw-semibold">What happens next</p>
          <p>{receipt.nextSteps}</p>
        </div>
        <div className="col-12">
          <h3 className="h5">What you submitted</h3>
          <div className="row g-2">
            <div className="col-12 col-sm-4 fw-semibold">Name</div>
            <div className="col-12 col-sm-8">{receipt.name}</div>
            {receipt.rank ? (
              <>
                <div className="col-12 col-sm-4 fw-semibold">Rank</div>
                <div className="col-12 col-sm-8">{receipt.rank}</div>
              </>
            ) : null}
            {receipt.branch ? (
              <>
                <div className="col-12 col-sm-4 fw-semibold">Branch</div>
                <div className="col-12 col-sm-8">{receipt.branch}</div>
              </>
            ) : null}
            {receipt.flagLocation ? (
              <>
                <div className="col-12 col-sm-4 fw-semibold">Flag location</div>
                <div className="col-12 col-sm-8">{receipt.flagLocation}</div>
              </>
            ) : null}
            {receipt.tribute ? (
              <>
                <div className="col-12 col-sm-4 fw-semibold">Tribute</div>
                <div className="col-12 col-sm-8">{receipt.tribute}</div>
              </>
            ) : null}
          </div>
        </div>
      </div>
    </section>
  );
}
