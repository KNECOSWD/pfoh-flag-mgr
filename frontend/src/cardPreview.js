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

/**
 * @param {Date | string} [date]
 */
export function mergedPrintDownloadName(date = new Date()) {
  const iso = date instanceof Date ? date.toISOString() : String(date);
  return `pfoh-card-reprints-${iso.slice(0, 10)}.pdf`;
}
