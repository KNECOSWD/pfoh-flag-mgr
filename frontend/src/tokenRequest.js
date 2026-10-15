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
