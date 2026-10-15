export const HONOR_PHOTO_MAX_EDGE = 1600;
export const HONOR_PHOTO_MAX_BYTES = 1_200_000;

/**
 * @param {{ size?: number } | null | undefined} file
 * @param {number} width
 * @param {number} height
 */
export function shouldCompressHonoreePhoto(file, width, height) {
  const size = file?.size ?? 0;
  const edge = Math.max(width || 0, height || 0);
  return size > HONOR_PHOTO_MAX_BYTES || edge > HONOR_PHOTO_MAX_EDGE;
}

/**
 * @template T
 * @param {() => Promise<T>} work
 * @param {number} ms
 * @param {string} message
 * @returns {Promise<T>}
 */
export function withDeadline(work, ms, message) {
  let timer;
  const timeout = new Promise((_, reject) => {
    timer = setTimeout(() => {
      reject(new Error(message));
    }, ms);
  });

  return Promise.race([Promise.resolve().then(work), timeout]).finally(() => {
    clearTimeout(timer);
  });
}
