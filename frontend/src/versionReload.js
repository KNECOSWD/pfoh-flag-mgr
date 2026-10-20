// How often an open tab asks Version/Get. Long enough that a deploy check
// is not a request loop. The watch stops once the reload prompt is showing.
export const VERSION_POLL_MS = 120_000;

// A kiosk does not show the prompt. It reloads after this long with no
// touch or pointer input.
export const KIOSK_VERSION_IDLE_MS = 60_000;

const POINTER_ACTIVITY = ["pointerdown", "pointermove", "touchstart", "touchmove"];

export function listenForPointerAndTouch(note) {
  if (typeof window === "undefined" || typeof window.addEventListener !== "function") return () => {};
  for (const name of POINTER_ACTIVITY) {
    window.addEventListener(name, note, { passive: true });
  }
  return () => {
    for (const name of POINTER_ACTIVITY) window.removeEventListener(name, note);
  };
}

export function versionUrl(apiBase = "") {
  const base = String(apiBase || "").replace(/\/$/, "");
  return `${base}/api/version`;
}

export function versionNeedsReload(loadedVersion, remoteVersion) {
  const loaded = String(loadedVersion ?? "").trim();
  const remote = String(remoteVersion ?? "").trim();
  if (!loaded || !remote) return false;
  return loaded !== remote;
}

/**
 * @param {{
 *   loadedVersion: string,
 *   loadRemote: () => Promise<string>,
 *   onPrompt: () => void,
 *   onReload?: () => void,
 *   kiosk?: boolean | (() => boolean),
 *   idleMs?: number,
 *   now?: () => number,
 *   listenActivity?: (note: () => void) => () => void,
 *   intervalMs?: number,
 *   isHidden?: () => boolean,
 *   setIntervalFn?: (fn: () => void, ms: number) => number,
 *   clearIntervalFn?: (id: number) => void,
 *   setTimeoutFn?: (fn: () => void, ms: number) => number,
 *   clearTimeoutFn?: (id: number) => void
 * }} options
 */
export function startVersionWatch({
  loadedVersion,
  loadRemote,
  onPrompt,
  onReload = () => {},
  kiosk = false,
  idleMs = KIOSK_VERSION_IDLE_MS,
  now = () => Date.now(),
  listenActivity = listenForPointerAndTouch,
  intervalMs = VERSION_POLL_MS,
  isHidden = () => false,
  setIntervalFn = setInterval,
  clearIntervalFn = clearInterval,
  setTimeoutFn = setTimeout,
  clearTimeoutFn = clearTimeout
}) {
  const kioskOn = () => (typeof kiosk === "function" ? Boolean(kiosk()) : Boolean(kiosk));
  let stopped = false;
  let decided = false;
  let reloadArmed = false;
  let lastActivity = now();
  let idleTimer = 0;

  function scheduleIdleReload() {
    clearTimeoutFn(idleTimer);
    idleTimer = 0;
    if (stopped || !reloadArmed || !kioskOn()) return;
    const elapsed = now() - lastActivity;
    if (elapsed >= idleMs) {
      reloadArmed = false;
      stopped = true;
      onReload();
      return;
    }
    idleTimer = setTimeoutFn(scheduleIdleReload, idleMs - elapsed);
  }

  function noteActivity() {
    lastActivity = now();
    if (reloadArmed) scheduleIdleReload();
  }

  const unlisten = listenActivity(noteActivity);

  async function tick() {
    if (stopped || decided || isHidden()) return;
    let remote = "";
    try {
      remote = await loadRemote();
    } catch {
      return;
    }
    if (stopped || decided) return;
    if (!versionNeedsReload(loadedVersion, remote)) return;
    decided = true;
    clearIntervalFn(timer);
    if (kioskOn()) {
      reloadArmed = true;
      scheduleIdleReload();
      return;
    }
    onPrompt();
  }

  const timer = setIntervalFn(() => {
    void tick();
  }, intervalMs);
  void tick();

  return () => {
    stopped = true;
    reloadArmed = false;
    clearIntervalFn(timer);
    clearTimeoutFn(idleTimer);
    unlisten();
  };
}
