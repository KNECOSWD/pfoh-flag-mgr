// 1. Add these imports to the existing import from "./api":
// HonoreeSearchResult,
// honoreeApi,

// 2. Add these useState lines inside App():

const [honoreeSearchText, setHonoreeSearchText] = useState("");
const [honoreeResults, setHonoreeResults] = useState<HonoreeSearchResult[]>([]);
const [honoreeSearchPerformed, setHonoreeSearchPerformed] = useState(false);
const [searchLoading, setSearchLoading] = useState(false);

// 3. Add these functions inside App():

async function searchHonorees(event: React.FormEvent) {
  event.preventDefault();

  if (!account) return;

  setSearchLoading(true);
  setError("");
  setNotice("");

  try {
    const results = await honoreeApi.search(instance, account, honoreeSearchText, 25);
    setHonoreeResults(results);
    setHonoreeSearchPerformed(true);
  } catch (err) {
    setError(err instanceof Error ? err.message : "Unable to search honorees.");
  } finally {
    setSearchLoading(false);
  }
}

function clearHonoreeSearch() {
  setHonoreeSearchText("");
  setHonoreeResults([]);
  setHonoreeSearchPerformed(false);
}

// 4. Add this section in the signed-in UI before "My claimed flags":

<section className="card">
  <div className="sectionHeader">
    <div>
      <p className="eyebrow">Find an existing honoree</p>
      <h2>Honoree search</h2>
    </div>
  </div>

  <p>
    Search first to see whether an honoree is already in the Plano Flags of Honor database.
  </p>

  <form className="searchBar" onSubmit={searchHonorees}>
    <input
      type="search"
      placeholder="Search by honoree name, nickname, rank, branch, sponsor, or flag grid"
      value={honoreeSearchText}
      onChange={(e) => setHonoreeSearchText(e.target.value)}
    />
    <button type="submit" disabled={searchLoading}>
      {searchLoading ? "Searching..." : "Search"}
    </button>
    <button type="button" className="secondary" onClick={clearHonoreeSearch}>
      Clear
    </button>
  </form>

  {honoreeSearchPerformed ? (
    honoreeResults.length === 0 ? (
      <p className="emptyState">
        No honorees found. If this is a new honoree, claim an available flag grid below and submit their information.
      </p>
    ) : (
      <div className="honoreeResults">
        {honoreeResults.map((honoree) => (
          <article key={honoree.id} className="honoreeCard">
            {honoree.imageUrl ? (
              <img src={honoree.imageUrl} alt={honoree.fullName} />
            ) : (
              <div className="honoreePlaceholder">No photo</div>
            )}

            <div>
              <div className="honoreeTitleRow">
                <h3>{honoree.fullName}</h3>
                {honoree.kia ? <span className="status status-submitted">KIA</span> : null}
              </div>

              <p>
                {[honoree.rank, honoree.serviceBranchName].filter(Boolean).join(" • ") || "Service details unavailable"}
              </p>

              <dl className="detailGrid">
                <div>
                  <dt>Flag grid</dt>
                  <dd>{honoree.flagGrid || "—"}</dd>
                </div>
                <div>
                  <dt>Sponsor</dt>
                  <dd>{honoree.sponsorName || "—"}</dd>
                </div>
                {honoree.nickname ? (
                  <div>
                    <dt>Nickname</dt>
                    <dd>{honoree.nickname}</dd>
                  </div>
                ) : null}
              </dl>

              {honoree.pdfUrl ? (
                <a className="textLink" href={honoree.pdfUrl} target="_blank" rel="noreferrer">
                  Open honoree PDF
                </a>
              ) : null}
            </div>
          </article>
        ))}
      </div>
    )
  ) : null}
</section>
