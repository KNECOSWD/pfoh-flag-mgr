export const PLEASE_SIGN_IN_AGAIN = "Please sign in again.";
export const AUTH_REDIRECT_GUARD_KEY = "pfoh.authRedirectOnce";

const RECOVERABLE_AUTH_CODES = [
  "interaction_required",
  "login_required",
  "consent_required",
  "authorization_code_missing_from_server_response",
  "monitor_window_timeout",
  "no_network_connectivity",
  "no_token_request_cache_error",
  "hash_empty_error"
];

export class QuietAuthRejection extends Error {
  constructor() {
    super("Authentication is being refreshed.");
    this.name = "QuietAuthRejection";
  }
}

export class SignInAgainError extends Error {
  constructor() {
    super(PLEASE_SIGN_IN_AGAIN);
    this.name = "SignInAgainError";
  }
}

export function isQuietAuthRejection(error) {
  return error instanceof QuietAuthRejection || error?.name === "QuietAuthRejection";
}

export function isSignInAgainError(error) {
  return error instanceof SignInAgainError || error?.name === "SignInAgainError";
}

function recoverableText(text) {
  const value = String(text || "");
  if (/AADSTS160021/i.test(value)) return true;
  return RECOVERABLE_AUTH_CODES.some((code) => value.includes(code));
}

export function isRecoverableAuthError(error) {
  if (error == null || error === false) return false;
  if (typeof error === "string") return recoverableText(error);
  return recoverableText(`${error.errorCode ?? ""} ${error.message ?? ""} ${error.errorMessage ?? ""}`);
}

export function bannerForTokenError(error) {
  if (isQuietAuthRejection(error)) return "";
  if (isSignInAgainError(error)) return PLEASE_SIGN_IN_AGAIN;
  return null;
}

function readGuard(storage) {
  try {
    return storage?.getItem(AUTH_REDIRECT_GUARD_KEY) === "1";
  } catch {
    return false;
  }
}

function writeGuard(storage) {
  try {
    storage?.setItem(AUTH_REDIRECT_GUARD_KEY, "1");
  } catch {
    // The in-memory flag still stops a second redirect in this page.
  }
}

export function createTokenCoordinator({ storage } = {}) {
  let inFlight = null;
  let redirectClaimed = false;

  return {
    acquire({ silent, redirect, isInteractionRequired }) {
      if (!inFlight) {
        const run = (async () => {
          try {
            return await silent();
          } catch (error) {
            const recoverable =
              (typeof isInteractionRequired === "function" && isInteractionRequired(error)) ||
              isRecoverableAuthError(error);
            if (!recoverable) throw error;

            if (redirectClaimed || readGuard(storage)) {
              throw new SignInAgainError();
            }

            redirectClaimed = true;
            writeGuard(storage);

            try {
              await redirect();
            } catch {
              throw new SignInAgainError();
            }

            throw new QuietAuthRejection();
          }
        })();

        let tracked;
        tracked = run.finally(() => {
          if (inFlight === tracked) inFlight = null;
        });
        inFlight = tracked;
      }

      return inFlight;
    }
  };
}

function browserSessionStorage() {
  try {
    if (typeof sessionStorage !== "undefined") return sessionStorage;
  } catch {
    return undefined;
  }
  return undefined;
}

export const sharedTokenRequest = createTokenCoordinator({
  storage: browserSessionStorage()
});

export function isRetryableNetworkError(error) {
  const message = `${error?.message ?? ""} ${error?.name ?? ""}`.toLowerCase();
  if (message.includes("authentication failed")) return false;
  if (isQuietAuthRejection(error) || isSignInAgainError(error)) return false;

  const status = Number(error?.status);
  if (status === 401 || status === 403 || status === 500) return false;
  if (Number.isFinite(status) && status !== 0) return false;

  return (
    message.includes("failed to fetch") ||
    message.includes("network") ||
    message.includes("abort") ||
    message.includes("cancel") ||
    message.includes("timeout")
  );
}

export async function withNetworkRetry(work) {
  try {
    return await work();
  } catch (error) {
    if (!isRetryableNetworkError(error)) throw error;
    return work();
  }
}
