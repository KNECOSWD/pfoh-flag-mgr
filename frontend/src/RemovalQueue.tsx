import { useEffect, useState } from "react";
import type { AccountInfo, IPublicClientApplication } from "@azure/msal-browser";
import { ApiError, adminApi, type RemovalQueueItem } from "./api";
import { RemovalQueueScreen, toggleRemovalSelection, toggleSelectAllRemovalItems } from "./removalQueue.js";
import { isQuietAuthRejection } from "./tokenRequest.js";

export function RemovalQueue({
  instance,
  account,
  onChanged,
  onNavigate
}: {
  instance: IPublicClientApplication;
  account: AccountInfo | null;
  onChanged?: () => void;
  onNavigate: (href: string) => void;
}) {
  const [status, setStatus] = useState<"open" | "done">("open");
  const [openItems, setOpenItems] = useState<RemovalQueueItem[]>([]);
  const [doneItems, setDoneItems] = useState<RemovalQueueItem[]>([]);
  const [selectedIds, setSelectedIds] = useState<number[]>([]);
  const [loaded, setLoaded] = useState(false);
  const [loadError, setLoadError] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  async function load() {
    if (!account) return;
    setBusy(true);
    try {
      const [openRows, doneRows] = await Promise.all([
        adminApi.removalQueue(instance, account, "open"),
        adminApi.removalQueue(instance, account, "done")
      ]);
      setOpenItems(openRows);
      setDoneItems(doneRows);
      setSelectedIds((current) => current.filter((id) => openRows.some((item) => item.id === id)));
      setLoadError("");
      setLoaded(true);
    } catch (err) {
      if (isQuietAuthRejection(err)) return;
      setOpenItems([]);
      setDoneItems([]);
      setLoaded(false);
      setLoadError(err instanceof ApiError ? err.serverMessage || err.message : "The removal queue could not be loaded.");
    } finally {
      setBusy(false);
    }
  }

  useEffect(() => {
    void load();
    // The queue is loaded when an administrator opens this page.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [account?.homeAccountId]);

  async function markDone(ids: number[]) {
    if (!account || ids.length === 0) return;
    setBusy(true);
    setError("");
    try {
      await adminApi.markRemovalDone(instance, account, ids);
      await load();
      onChanged?.();
    } catch (err) {
      if (isQuietAuthRejection(err)) return;
      setError(err instanceof ApiError ? err.serverMessage || err.message : "The cards could not be marked done.");
    } finally {
      setBusy(false);
    }
  }

  if (!account) return null;

  return (
    <RemovalQueueScreen
      status={status}
      openItems={openItems}
      doneItems={doneItems}
      loaded={loaded}
      loadError={loadError}
      busy={busy}
      error={error}
      selectedIds={selectedIds}
      onToggle={(id: number) => setSelectedIds((current) => toggleRemovalSelection(current, id))}
      onToggleAll={() =>
        setSelectedIds((current) => toggleSelectAllRemovalItems(openItems.map((item) => item.id), current))
      }
      onMarkDone={(ids: number[]) => void markDone(ids)}
      onRefresh={() => void load()}
      onShowOpen={() => setStatus("open")}
      onShowDone={() => setStatus("done")}
      onNavigate={onNavigate}
    />
  );
}
