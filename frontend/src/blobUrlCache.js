/**
 * @param {{ createObjectURL: (blob: Blob) => string, revokeObjectURL: (url: string) => void }} urls
 */
export function createBlobUrlCache(urls) {
  /** @type {Map<string, { url: string, owners: number }>} */
  const entries = new Map();

  return {
    /**
     * @param {string} key
     * @param {Blob} blob
     */
    hold(key, blob) {
      const existing = entries.get(key);
      if (existing) {
        existing.owners += 1;
        return existing.url;
      }

      const url = urls.createObjectURL(blob);
      entries.set(key, { url, owners: 1 });
      return url;
    },

    /**
     * @param {string} key
     */
    release(key) {
      const existing = entries.get(key);
      if (!existing) return;

      existing.owners -= 1;
      if (existing.owners > 0) return;

      urls.revokeObjectURL(existing.url);
      entries.delete(key);
    }
  };
}
