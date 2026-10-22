/**
 * Attention counts are requested once for a signed-in administrator.
 * Signed-out visitors and non-administrators never reach the pending endpoint.
 * @param {{ isAuthenticated?: boolean, isAdmin?: boolean, loadSignedInData?: boolean }} gate
 */
export function canRequestAdminAttention(gate) {
  return Boolean(gate?.loadSignedInData) && Boolean(gate?.isAuthenticated) && Boolean(gate?.isAdmin);
}

/**
 * @param {{ isAuthenticated?: boolean, isAdmin?: boolean, loadSignedInData?: boolean }} gate
 * @param {() => Promise<{ items?: unknown[], openDuplicateGroupCount?: number } | null | undefined>} loadPending
 */
export async function requestAdminAttentionCounts(gate, loadPending) {
  if (!canRequestAdminAttention(gate)) {
    return { requested: false, pendingReview: 0, duplicatesForReview: 0, pending: null };
  }

  const pending = await loadPending();
  const items = Array.isArray(pending?.items) ? pending.items : [];
  const raw = Number(pending?.openDuplicateGroupCount);
  return {
    requested: true,
    pendingReview: items.length,
    duplicatesForReview: Number.isFinite(raw) && raw >= 0 ? raw : 0,
    pending
  };
}
