export const similarEntryLead = "A similar honoree is already on file.";
export const similarEntryHelp = "You can update that entry, or submit this one as a new entry.";

/**
 * @param {string | null | undefined} kind
 */
export function matchKindLabel(kind) {
  if (kind === "Approved") return "Approved";
  if (kind === "Pending") return "Pending review";
  return "On file";
}

/**
 * @param {{ kind?: string | null, canUpdateDirectly?: boolean, isAdmin?: boolean }} match
 */
export function updateChoiceHelp(match) {
  if (match?.isAdmin) {
    return "As an administrator, you can update this record directly.";
  }

  if (match?.kind === "Approved") {
    return "Updating an approved entry sends a correction to the administrators. The public record stays as it is until they approve it.";
  }

  if (match?.canUpdateDirectly) {
    return "This pending entry uses the same submitter email, so you can update it.";
  }

  return "This pending entry was submitted from a different email. Your update will be sent to the administrators as a correction request.";
}

/**
 * @param {"nominate" | "admin-edit" | string | null | undefined} mode
 */
export function proceedLabel(mode) {
  return mode === "admin-edit" ? "Save this record anyway" : "Submit as a new entry";
}

/**
 * @param {string | null | undefined} token
 */
export function submissionReference(token) {
  return String(token || "").trim().toLowerCase();
}
