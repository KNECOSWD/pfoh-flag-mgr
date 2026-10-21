import { useEffect, useState } from "react";
import type { AccountInfo, IPublicClientApplication } from "@azure/msal-browser";
import { ApiError, adminApi, type MergeHistoryRecord } from "./api";
import { MergeHistoryScreen } from "./mergeHistory.js";
import { isQuietAuthRejection } from "./tokenRequest.js";

export function MergeHistory({
  instance,
  account,
  onBack
}: {
  instance: IPublicClientApplication;
  account: AccountInfo | null;
  onBack: () => void;
}) {
  const [records, setRecords] = useState<MergeHistoryRecord[]>([]);
  const [loaded, setLoaded] = useState(false);
  const [loadError, setLoadError] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  async function load() {
    if (!account) return;
    setBusy(true);
    try {
      setRecords(await adminApi.mergeHistory(instance, account));
      setLoadError("");
      setLoaded(true);
    } catch (err) {
      if (isQuietAuthRejection(err)) return;
      setRecords([]);
      setLoaded(false);
      setLoadError(err instanceof ApiError ? err.serverMessage || err.message : "Merge history could not be loaded.");
    } finally {
      setBusy(false);
    }
  }

  useEffect(() => {
    void load();
    // History is loaded only when an administrator opens this page.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [account?.homeAccountId]);

  async function undo(id: number) {
    if (!account) return;
    setBusy(true);
    setError("");
    try {
      await adminApi.undoMerge(instance, account, id);
      await load();
    } catch (err) {
      if (isQuietAuthRejection(err)) return;
      setError(err instanceof ApiError ? err.serverMessage || err.message : "The merge could not be undone.");
    } finally {
      setBusy(false);
    }
  }

  if (!account) return null;

  return (
    <MergeHistoryScreen
      records={records}
      loaded={loaded}
      loadError={loadError}
      busy={busy}
      error={error}
      onUndo={(id: number) => void undo(id)}
      onBack={onBack}
      onReload={() => void load()}
    />
  );
}
