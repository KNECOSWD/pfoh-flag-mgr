/**
 * @param {Array<{ name: string }>} rows
 */
export function confirmReviewQueueMessage(rows) {
  const names = rows.map((row) => row.name).filter((name) => name);
  if (names.length === 0) return "Select at least one person.";
  if (names.length === 1) return `Add ${names[0]} to the review queue?`;
  if (names.length === 2) return `Add ${names[0]} and ${names[1]} to the review queue?`;
  const leading = names.slice(0, -1).join(", ");
  return `Add ${leading}, and ${names[names.length - 1]} to the review queue?`;
}
