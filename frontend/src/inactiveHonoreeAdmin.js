export const notRecorded = "Not recorded";

/**
 * @param {string | null | undefined} value
 */
export function formatDeactivated(value) {
  if (!value) return notRecorded;
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return notRecorded;
  return date.toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
}

/**
 * @param {{ name: string }} row
 */
export function approvalQueueSentence(row) {
  return `Send ${row.name} to the approval queue with no flag assigned.`;
}

/**
 * @param {Array<{ name: string }>} rows
 */
export function confirmReactivationMessage(rows) {
  return rows.map((row) => approvalQueueSentence(row)).join(" ");
}
