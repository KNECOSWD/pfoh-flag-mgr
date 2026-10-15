export function createTokenCoordinator() {
  let inFlight = null;
  let waiters = 0;

  return {
    acquire({ silent, redirect, isInteractionRequired }) {
      waiters += 1;

      if (!inFlight) {
        const run = (async () => {
          try {
            return await silent();
          } catch (error) {
            if (isInteractionRequired(error) && waiters <= 1) {
              await redirect();
            }

            throw error;
          }
        })();

        inFlight = run.finally(() => {
          if (inFlight === run) inFlight = null;
        });
      }

      const current = inFlight;
      return current.finally(() => {
        waiters -= 1;
      });
    }
  };
}

export const sharedTokenRequest = createTokenCoordinator();

export function isRetryableNetworkError(error) {
  const message = `${error?.message ?? ""} ${error?.name ?? ""}`.toLowerCase();
  if (message.includes("authentication failed")) return false;

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
