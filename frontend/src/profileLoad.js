function normalizeKey(accountKey) {
  return String(accountKey || "");
}

export function createProfileLoadGate() {
  let attemptedKey = "";
  let inFlightKey = "";

  return {
    claim(accountKey) {
      const key = normalizeKey(accountKey);
      if (!key || attemptedKey === key || inFlightKey === key) {
        return false;
      }

      inFlightKey = key;
      return true;
    },

    settle(accountKey) {
      const key = normalizeKey(accountKey);
      if (!key) return;
      if (inFlightKey === key) inFlightKey = "";
      attemptedKey = key;
    },

    release(accountKey) {
      const key = normalizeKey(accountKey);
      if (inFlightKey === key) inFlightKey = "";
    },

    reset() {
      attemptedKey = "";
      inFlightKey = "";
    }
  };
}
