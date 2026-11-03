export const similarEntryLead = "Is this the same person?";

/**
 * @param {"loading" | "found" | "missing" | string | null | undefined} lookup
 */
export function submissionHero(lookup) {
  if (lookup === "found") {
    return {
      title: "SUBMISSION RECEIVED",
      subtitle: "Your reference number is on this page. An administrator reviews it next."
    };
  }

  if (lookup === "missing") {
    return {
      title: "SUBMISSION",
      subtitle: "This link does not match a submission."
    };
  }

  return {
    title: "SUBMISSION",
    subtitle: "Loading this page."
  };
}

/**
 * @param {string | null | undefined} kind
 */
export function matchKindLabel(kind) {
  if (kind === "Approved") return "Approved";
  if (kind === "Pending") return "Pending review";
  return "On file";
}

/**
 * @param {number} count
 */
export function samePersonNoLabel(count) {
  return count > 1 ? "No, none of these" : "No";
}

/**
 * Shown on the pending review page. This is not the address token.
 * @param {string | null | undefined} code
 */
export function submissionReference(code) {
  return String(code || "").trim();
}

/**
 * @param {string | null | undefined} reason
 */
export function matchReasonSentence(reason) {
  const text = String(reason || "").trim().replace(/\.+$/, "");
  if (!text) return "";
  return `Matched on: ${text}.`;
}
