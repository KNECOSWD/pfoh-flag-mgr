export const honoreeCardHeading = "Honoree Card";
export const printCenterHeading = "Print Center";
export const honoreeCardDownloadName = "honoree-card.pdf";

/**
 * Subtitle under the Print Center heading. One selected card stays singular.
 * @param {number} count
 */
export function printSelectionSubtitle(count) {
  const cards = Math.max(0, Number(count) || 0);
  return `${cards} card${cards === 1 ? "" : "s"} selected`;
}

/**
 * @param {string} heading
 */
export function cardPreviewLoadingMessage(heading) {
  if (heading === printCenterHeading) return "Loading the print PDF…";
  return "Loading the honoree card…";
}

const bogotaDate = new Intl.DateTimeFormat("en-CA", {
  timeZone: "America/Bogota",
  year: "numeric",
  month: "2-digit",
  day: "2-digit"
});

/**
 * Title of the merged Print Center PDF, also its download name without ".pdf".
 * The date is today in Bogotá time (COT).
 * @param {number} count
 * @param {Date} [date]
 */
export function mergedPrintName(count, date = new Date()) {
  const cards = Math.max(0, Number(count) || 0);
  return `PFOH Reprint Cards - ${bogotaDate.format(date)} - ${cards} card${cards === 1 ? "" : "s"}`;
}
