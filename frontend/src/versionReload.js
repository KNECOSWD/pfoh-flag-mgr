// How often an open tab asks Version/Get. Long enough that a deploy check
// is not a request loop. The watch stops once the reload prompt is showing.
export const VERSION_POLL_MS = 120_000;

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

export function startVersionWatch({
  loadedVersion,
  loadRemote,
  onPrompt,
  intervalMs = VERSION_POLL_MS,
  isHidden = () => false,
  setIntervalFn = setInterval,
  clearIntervalFn = clearInterval
}) {
  let stopped = false;
  let prompted = false;

  async function tick() {
    if (stopped || prompted || isHidden()) return;
    let remote = "";
    try {
      remote = await loadRemote();
    } catch {
      return;
    }
    if (stopped || prompted) return;
    if (!versionNeedsReload(loadedVersion, remote)) return;
    prompted = true;
    clearIntervalFn(timer);
    onPrompt();
  }

  const timer = setIntervalFn(() => {
    void tick();
  }, intervalMs);
  void tick();

  return () => {
    stopped = true;
    clearIntervalFn(timer);
  };
}
