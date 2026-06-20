import { useEffect, useMemo, useState } from "react";
import { useIsAuthenticated, useMsal } from "@azure/msal-react";
import { flagsApi, FlagRecord, UpsertFlagRequest } from "./api";
import { loginRequest } from "./authConfig";

const blankFlag: UpsertFlagRequest = {
  honoreeName: "",
  serviceBranch: "",
  rankOrTitle: "",
  flagNumber: "",
  gridLocation: "",
  tributeText: "",
  status: "Draft"
};

export default function App() {
  const isAuthenticated = useIsAuthenticated();
  const { instance, accounts } = useMsal();
  const account = instance.getActiveAccount() ?? accounts[0];
  const [flags, setFlags] = useState<FlagRecord[]>([]);
  const [form, setForm] = useState<UpsertFlagRequest>(blankFlag);
  const [editingId, setEditingId] = useState<number | null>(null);
  const [error, setError] = useState<string>("");
  const [loading, setLoading] = useState(false);

  const displayName = useMemo(() => account?.name || account?.username || "Supporter", [account]);

  async function signIn() {
    await instance.loginRedirect(loginRequest);
  }

  async function signOut() {
    await instance.logoutRedirect();
  }

  async function loadFlags() {
    if (!account) return;
    setLoading(true);
    setError("");
    try {
      const data = await flagsApi.list(instance, account);
      setFlags(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to load flags.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    if (isAuthenticated && account) loadFlags();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isAuthenticated, account?.homeAccountId]);

  async function saveFlag(event: React.FormEvent) {
    event.preventDefault();
    if (!account) return;
    setError("");

    try {
      if (editingId) {
        await flagsApi.update(instance, account, editingId, form);
      } else {
        await flagsApi.create(instance, account, form);
      }
      setForm(blankFlag);
      setEditingId(null);
      await loadFlags();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to save flag.");
    }
  }

  function editFlag(flag: FlagRecord) {
    setEditingId(flag.id);
    setForm({
      honoreeName: flag.honoreeName,
      serviceBranch: flag.serviceBranch ?? "",
      rankOrTitle: flag.rankOrTitle ?? "",
      flagNumber: flag.flagNumber ?? "",
      gridLocation: flag.gridLocation ?? "",
      tributeText: flag.tributeText ?? "",
      status: flag.status ?? "Draft"
    });
    window.scrollTo({ top: 0, behavior: "smooth" });
  }

  async function deleteFlag(id: number) {
    if (!account || !confirm("Delete this flag record?")) return;
    await flagsApi.delete(instance, account, id);
    await loadFlags();
  }

  function update<K extends keyof UpsertFlagRequest>(key: K, value: UpsertFlagRequest[K]) {
    setForm((current) => ({ ...current, [key]: value }));
  }

  return (
    <main>
      <header className="hero">
        <div>
          <p className="eyebrow">Plano Flags of Honor</p>
          <h1>Flag Manager</h1>
          <p>Register, sign in with MFA, and manage only your own flag records.</p>
        </div>
        <div className="authBox">
          {isAuthenticated ? (
            <>
              <strong>{displayName}</strong>
              <button onClick={signOut}>Sign out</button>
            </>
          ) : (
            <button onClick={signIn}>Register / sign in</button>
          )}
        </div>
      </header>

      {!isAuthenticated ? (
        <section className="card">
          <h2>Welcome</h2>
          <p>Select Register / sign in to create an account through Microsoft Entra External ID. MFA is enforced by Conditional Access in Azure, not custom code.</p>
        </section>
      ) : (
        <>
          <section className="card">
            <h2>{editingId ? "Edit flag" : "Add a flag"}</h2>
            <form onSubmit={saveFlag} className="gridForm">
              <label>Honoree name<input required value={form.honoreeName} onChange={(e) => update("honoreeName", e.target.value)} /></label>
              <label>Service branch<input value={form.serviceBranch} onChange={(e) => update("serviceBranch", e.target.value)} /></label>
              <label>Rank / title<input value={form.rankOrTitle} onChange={(e) => update("rankOrTitle", e.target.value)} /></label>
              <label>Flag number<input value={form.flagNumber} onChange={(e) => update("flagNumber", e.target.value)} /></label>
              <label>Grid location<input value={form.gridLocation} onChange={(e) => update("gridLocation", e.target.value)} /></label>
              <label>Status<select value={form.status} onChange={(e) => update("status", e.target.value)}><option>Draft</option><option>Submitted</option><option>Approved</option></select></label>
              <label className="wide">Tribute text<textarea rows={5} value={form.tributeText} onChange={(e) => update("tributeText", e.target.value)} /></label>
              <div className="actions">
                <button type="submit">{editingId ? "Save changes" : "Add flag"}</button>
                {editingId && <button type="button" className="secondary" onClick={() => { setEditingId(null); setForm(blankFlag); }}>Cancel</button>}
              </div>
            </form>
            {error && <p className="error">{error}</p>}
          </section>

          <section className="card">
            <div className="sectionHeader">
              <h2>My flags</h2>
              <button className="secondary" onClick={loadFlags}>{loading ? "Loading..." : "Refresh"}</button>
            </div>
            {flags.length === 0 ? <p>No flags yet.</p> : (
              <div className="tableWrap">
                <table>
                  <thead><tr><th>Honoree</th><th>Branch</th><th>Flag #</th><th>Grid</th><th>Status</th><th></th></tr></thead>
                  <tbody>{flags.map((flag) => (
                    <tr key={flag.id}>
                      <td><strong>{flag.honoreeName}</strong><br /><span>{flag.rankOrTitle}</span></td>
                      <td>{flag.serviceBranch}</td>
                      <td>{flag.flagNumber}</td>
                      <td>{flag.gridLocation}</td>
                      <td>{flag.status}</td>
                      <td className="rowActions"><button onClick={() => editFlag(flag)}>Edit</button><button className="danger" onClick={() => deleteFlag(flag.id)}>Delete</button></td>
                    </tr>
                  ))}</tbody>
                </table>
              </div>
            )}
          </section>
        </>
      )}
    </main>
  );
}
