/**
 * @param {{ latestChangeRequest?: { photoFileName?: string | null } | null, honoreeImageUrl?: string | null } | null | undefined} claim
 * @returns {"pending" | "public" | "none"}
 */
export function submissionPhotoKind(claim) {
  const photo = claim?.latestChangeRequest?.photoFileName;
  if (typeof photo === "string") {
    const name = photo.trim().toLowerCase();
    if (name.startsWith("pending/") && !photo.includes("..")) {
      return "pending";
    }
  }

  if (claim?.honoreeImageUrl) return "public";
  return "none";
}

/**
 * @param {string | null | undefined} kind
 */
export function submissionKindLabel(kind) {
  if (kind === "Change") return "Change";
  if (kind === "Withdrawal") return "Removal request";
  if (kind === "Reactivation") return "Reactivation request";
  return "New submission";
}

export function submissionKindClass(kind) {
  if (kind === "Change") return "isChange";
  if (kind === "Withdrawal") return "isWithdrawal";
  if (kind === "Reactivation") return "isReactivation";
  return "isNew";
}

/**
 * @param {{ previous?: string | null, proposed?: string | null } | null | undefined} change
 */
export function fieldChangeText(change) {
  const previous = change?.previous?.trim() ? change.previous : "(blank)";
  const proposed = change?.proposed?.trim() ? change.proposed : "(blank)";
  return `${previous} → ${proposed}`;
}

/**
 * @param {string | null | undefined} flagGridName
 * @param {number | null | undefined} flagGridId
 */
export function flagSpotLabel(flagGridName, flagGridId) {
  const name = flagGridName?.trim();
  if (name) return name;
  if (flagGridId) return `Grid ${flagGridId}`;
  return "Not assigned";
}

/**
 * @template {{ id?: number }} TClaim
 * @template {{ claimId?: number }} TReview
 * @param {TClaim[] | null | undefined} claims
 * @param {TReview[] | null | undefined} reviews
 * @param {number} claimId
 * @returns {{ claims: TClaim[], reviews: TReview[] }}
 */
export function dropWithdrawnSubmission(claims, reviews, claimId) {
  return {
    claims: (claims ?? []).filter((claim) => claim?.id !== claimId),
    reviews: (reviews ?? []).filter((item) => item?.claimId !== claimId)
  };
}
