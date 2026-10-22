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

export const PRINT_EVENT_COMMENT_MAX = 500;

const centralDateTime = new Intl.DateTimeFormat("en-US", {
  timeZone: "America/Chicago",
  year: "numeric",
  month: "short",
  day: "numeric",
  hour: "numeric",
  minute: "2-digit",
  timeZoneName: "short"
});

/**
 * Print history time in US Central (America/Chicago).
 * The platform label is CDT during daylight time and CST during standard time.
 * The merged PDF filename uses this same label for its date.
 * @param {string | number | Date} value
 */
export function formatPrintEventTime(value) {
  return centralDateTime.format(new Date(value));
}

/**
 * Title of the merged Print Center PDF, also its download name without ".pdf".
 * The date is the Central-time label from {@link formatPrintEventTime}.
 * @param {number} count
 * @param {Date} [date]
 */
export function mergedPrintName(count, date = new Date()) {
  const cards = Math.max(0, Number(count) || 0);
  return `PFOH Reprint Cards - ${formatPrintEventTime(date)} - ${cards} card${cards === 1 ? "" : "s"}`;
}
