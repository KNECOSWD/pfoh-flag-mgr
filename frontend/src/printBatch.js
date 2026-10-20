export const PRINT_BATCH_CONCURRENCY = 3;

/**
 * @typedef {{ changeRequestId: number, honoreeId: number, honoreeName: string }} PrintBatchCard
 * @typedef {{ card: PrintBatchCard, step: string, message: string }} PrintBatchFailure
 * @typedef {{ phase: "preparing" | "merging", done: number, total: number, current?: PrintBatchCard, generated: number, failed: number }} PrintBatchProgress
 */

/** @param {PrintBatchCard} card */
export function printCardLabel(card) {
  const name = String(card?.honoreeName ?? "").trim();
  return name ? `${name} (honoree #${card.honoreeId})` : `Honoree #${card.honoreeId}`;
}

/** @param {PrintBatchProgress} progress */
export function printBatchProgressText(progress) {
  const verb = progress.phase === "merging" ? "Merging card" : "Preparing card";
  return `${verb} ${progress.done}/${progress.total}`;
}

/** @param {PrintBatchFailure[]} failures */
export function printBatchFailureSummary(failures, total) {
  const count = failures.length;
  return `${count} of ${total} card${total === 1 ? "" : "s"} could not be prepared, so the merged PDF was not built. Fix or remove ${count === 1 ? "this card" : "these cards"} and try again.`;
}

function abortError() {
  const error = new Error("The print batch was canceled.");
  error.name = "AbortError";
  return error;
}

function errorMessage(error) {
  if (error instanceof Error && error.message.trim()) return error.message.trim();
  return String(error || "Unknown error");
}

/**
 * Prepares every card one request at a time (with small parallelism), generating
 * any card whose stored PDF is missing. No card is skipped: each one either yields
 * PDF bytes or a named failure.
 *
 * @param {{
 *   cards: PrintBatchCard[],
 *   loadStoredPdf: (card: PrintBatchCard, signal?: AbortSignal) => Promise<Uint8Array | ArrayBuffer | null>,
 *   generatePdf: (card: PrintBatchCard, signal?: AbortSignal) => Promise<void>,
 *   onProgress?: (progress: PrintBatchProgress) => void,
 *   signal?: AbortSignal,
 *   concurrency?: number
 * }} options
 */
export async function preparePrintBatch({
  cards,
  loadStoredPdf,
  generatePdf,
  onProgress,
  signal,
  concurrency = PRINT_BATCH_CONCURRENCY
}) {
  const total = cards.length;
  /** @type {(Uint8Array | ArrayBuffer | null)[]} */
  const pdfs = new Array(total).fill(null);
  /** @type {{ index: number, failure: PrintBatchFailure }[]} */
  const failed = [];
  let done = 0;
  let generated = 0;
  let next = 0;

  const report = (current) =>
    onProgress?.({ phase: "preparing", done, total, current, generated, failed: failed.length });

  async function prepare(card) {
    let step = "check";
    try {
      let pdf = await loadStoredPdf(card, signal);
      if (!pdf) {
        step = "generate";
        await generatePdf(card, signal);
        generated += 1;
        step = "reload";
        pdf = await loadStoredPdf(card, signal);
        if (!pdf) throw new Error("The card PDF was generated but is still not in storage.");
      }
      return { pdf };
    } catch (error) {
      if (signal?.aborted) throw abortError();
      return { failure: { card, step, message: errorMessage(error) } };
    }
  }

  async function worker() {
    while (next < total) {
      if (signal?.aborted) throw abortError();
      const index = next++;
      const card = cards[index];
      const result = await prepare(card);
      if (result.failure) failed.push({ index, failure: result.failure });
      else pdfs[index] = result.pdf;
      done += 1;
      report(card);
    }
  }

  report(undefined);
  const workers = Array.from({ length: Math.max(1, Math.min(concurrency, total)) }, () => worker());
  await Promise.all(workers);

  failed.sort((left, right) => left.index - right.index);
  return { pdfs, failures: failed.map((entry) => entry.failure), generated };
}

/**
 * @param {PrintBatchCard[]} cards
 * @param {(Uint8Array | ArrayBuffer)[]} pdfs
 * @param {{ onProgress?: (progress: PrintBatchProgress) => void, signal?: AbortSignal, generated?: number }} [options]
 */
export async function mergePrintBatch(cards, pdfs, { onProgress, signal, generated = 0 } = {}) {
  const total = cards.length;
  const { PDFDocument } = await import("pdf-lib");
  const output = await PDFDocument.create();
  /** @type {PrintBatchFailure[]} */
  const failures = [];

  for (let index = 0; index < total; index += 1) {
    if (signal?.aborted) throw abortError();
    const card = cards[index];
    try {
      const source = await PDFDocument.load(pdfs[index], { ignoreEncryption: true });
      const pages = await output.copyPages(source, source.getPageIndices());
      if (pages.length === 0) throw new Error("The card PDF has no pages.");
      pages.forEach((page) => output.addPage(page));
    } catch (error) {
      failures.push({ card, step: "merge", message: errorMessage(error) });
    }
    onProgress?.({ phase: "merging", done: index + 1, total, current: card, generated, failed: failures.length });
    if (index % 10 === 9) await new Promise((resolve) => setTimeout(resolve, 0));
  }

  if (failures.length > 0) return { failures };
  return { bytes: await output.save(), failures };
}

/** @param {PrintBatchFailure} failure */
export function printFailureStepLabel(failure) {
  switch (failure.step) {
    case "check":
      return "Checking the stored card PDF failed";
    case "generate":
      return "Generating the missing card PDF failed";
    case "reload":
      return "Loading the newly generated card PDF failed";
    case "merge":
      return "Adding the card PDF to the merged file failed";
    case "queue":
      return "The card is no longer in the reprint queue";
    default:
      return "Preparing the card failed";
  }
}
