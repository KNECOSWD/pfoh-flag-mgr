type SignInListener = (message: string) => void;

let currentMessage = "";
const listeners = new Set<SignInListener>();

export function signInFailureMessage(error: unknown) {
  const text = errorText(error);
  if (/AADSTS50146/i.test(text)) {
    return "Sign-in did not complete (AADSTS50146). Mapped claims need api.acceptMappedClaims set to true on the pfoh-flags-spa and pfoh-flags-api app registrations, with requestedAccessTokenVersion 2. Search and the public pages still work.";
  }

  if (text) {
    return `Sign-in did not complete. Search and the public pages still work. Details: ${text}`;
  }

  return "Sign-in did not complete. Search and the public pages still work.";
}

export function readAuthErrorFromLocation() {
  if (typeof window === "undefined") return "";

  const url = new URL(window.location.href);
  const fromQuery = url.searchParams.get("error_description") || url.searchParams.get("error") || "";
  const rawHash = url.hash.startsWith("#") ? url.hash.slice(1) : url.hash;
  const hashParams = rawHash ? new URLSearchParams(rawHash) : null;
  const fromHash = hashParams?.get("error_description") || hashParams?.get("error") || "";
  return (fromQuery || fromHash).trim();
}

export function publishSignInError(error: unknown) {
  currentMessage = signInFailureMessage(error);
  clearAuthErrorFromLocation();
  for (const listener of listeners) {
    listener(currentMessage);
  }
  return currentMessage;
}

export function subscribeSignInError(listener: SignInListener) {
  listener(currentMessage);
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

function errorText(error: unknown) {
  if (typeof error === "string") {
    return error.trim();
  }

  if (error instanceof Error) {
    const details = error as Error & { errorCode?: string; errorMessage?: string };
    return [details.errorCode, details.errorMessage, details.message].filter(Boolean).join(" ").trim();
  }

  return "";
}

function clearAuthErrorFromLocation() {
  if (typeof window === "undefined") return;

  const url = new URL(window.location.href);
  let changed = false;
  for (const key of ["error", "error_description", "error_uri"]) {
    if (url.searchParams.has(key)) {
      url.searchParams.delete(key);
      changed = true;
    }
  }

  const rawHash = url.hash.startsWith("#") ? url.hash.slice(1) : url.hash;
  if (rawHash && (rawHash.includes("error=") || rawHash.includes("error_description="))) {
    const hashParams = new URLSearchParams(rawHash);
    hashParams.delete("error");
    hashParams.delete("error_description");
    hashParams.delete("error_uri");
    const next = hashParams.toString();
    url.hash = next ? `#${next}` : "";
    changed = true;
  }

  if (changed) {
    window.history.replaceState(window.history.state, "", `${url.pathname}${url.search}${url.hash}`);
  }
}
