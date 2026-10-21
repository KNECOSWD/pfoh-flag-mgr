export function duplicateListState({ loaded, loadError, count }) {
  const failed = Boolean(String(loadError || "").trim());
  const ready = Boolean(loaded) && !failed;
  const size = Number(count) || 0;
  return {
    showCount: ready,
    showEmpty: ready && size === 0,
    showList: ready && size > 0,
    showError: failed,
    showRetry: failed
  };
}
