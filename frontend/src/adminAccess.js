/**
 * @typedef {{ name: string, ok: boolean, status: number, message: string }} AdminPanelResult
 */

/**
 * A 401 or 403 means this account is not an administrator.
 * Any other status is a panel failure, and the administrator tabs stay available.
 * @param {number} status
 */
export function isAdminAuthFailure(status) {
  return status === 401 || status === 403;
}

/**
 * @param {AdminPanelResult[]} panels
 * @returns {{ isAdmin: boolean, errors: Record<string, string> }}
 */
export function decideAdminLoad(panels) {
  const notAdmin = panels.some((panel) => !panel.ok && isAdminAuthFailure(panel.status));
  /** @type {Record<string, string>} */
  const errors = {};

  for (const panel of panels) {
    errors[panel.name] = notAdmin || panel.ok ? "" : panel.message;
  }

  return {
    isAdmin: !notAdmin,
    errors
  };
}
