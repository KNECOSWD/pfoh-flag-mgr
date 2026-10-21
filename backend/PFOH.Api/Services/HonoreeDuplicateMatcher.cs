using System.Text.RegularExpressions;

namespace PFOH.Api.Services;

/// <summary>
/// Ports the duplicate grouping from the read-only analyze script:
/// name normalization, branch aliases, adjacent flag grids, and the H1–H5 and
/// GRID labels. This only groups records. It does not edit them.
/// </summary>
public static class HonoreeDuplicateMatcher
{
    private static readonly HashSet<string> Suffixes = new(StringComparer.Ordinal)
    {
        "jr", "sr", "ii", "iii", "iv", "v"
    };

    private static readonly Dictionary<string, string> BranchAliases = new(StringComparer.Ordinal)
    {
        ["usmc"] = "united states marine corps",
        ["marines"] = "united states marine corps",
        ["us marine corps"] = "united states marine corps",
        ["usaf"] = "united states air force",
        ["us air force"] = "united states air force",
        ["usn"] = "united states navy",
        ["us navy"] = "united states navy",
        ["usa"] = "united states army",
        ["us army"] = "united states army",
        ["uscg"] = "united states coast guard",
        ["us coast guard"] = "united states coast guard"
    };

    private static readonly string[] ReasonOrder = ["H1", "H2", "H3", "H4", "H5", "GRID"];

    public static readonly IReadOnlyDictionary<string, string> ReasonText = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["H1"] = "same full name and same service branch",
        ["H2"] = "same first and last name (middle name, nickname or suffix differ) and same branch",
        ["H3"] = "same full name but a different service branch",
        ["H4"] = "same name on the same or an adjacent flag grid",
        ["H5"] = "same name submitted by the same sponsor",
        ["GRID"] = "assigned to the same flag grid, which holds only one flag"
    };

    public static IReadOnlyList<DuplicateMatchGroup> Match(IReadOnlyList<DuplicateCandidate> records)
    {
        var prepared = records.Select(Prepare).ToList();
        var edges = new Dictionary<(int, int), HashSet<string>>();

        for (var i = 0; i < prepared.Count; i++)
        {
            for (var j = i + 1; j < prepared.Count; j++)
            {
                var a = prepared[i];
                var b = prepared[j];
                if (a.Full.Length == 0 || a.Id == b.Id)
                {
                    continue;
                }

                void Link(string why)
                {
                    var key = a.Id < b.Id ? (a.Id, b.Id) : (b.Id, a.Id);
                    if (!edges.TryGetValue(key, out var reasons))
                    {
                        reasons = new HashSet<string>(StringComparer.Ordinal);
                        edges[key] = reasons;
                    }

                    reasons.Add(why);
                }

                if (a.Full == b.Full && a.Branch == b.Branch)
                {
                    Link("H1");
                }
                else if (a.FirstLast == b.FirstLast && a.Branch == b.Branch && a.FirstLast.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 2)
                {
                    Link("H2");
                }
                else if (a.Full == b.Full)
                {
                    Link("H3");
                }

                if ((a.Full == b.Full || a.FirstLast == b.FirstLast) &&
                    a.Grid.Length > 0 &&
                    b.Grid.Length > 0 &&
                    (a.Grid == b.Grid || Near(a.Grid, b.Grid)))
                {
                    Link("H4");
                }

                if (a.Full == b.Full && a.Submitter.Length > 0 && a.Submitter == b.Submitter)
                {
                    Link("H5");
                }

                if (a.Grid.Length > 0 && a.Grid == b.Grid)
                {
                    Link("GRID");
                }
            }
        }

        var parent = new Dictionary<int, int>();
        int Find(int id)
        {
            if (!parent.ContainsKey(id))
            {
                parent[id] = id;
            }

            var root = id;
            while (parent[root] != root)
            {
                parent[root] = parent[parent[root]];
                root = parent[root];
            }

            return root;
        }

        foreach (var pair in edges.Keys)
        {
            parent[Find(pair.Item1)] = Find(pair.Item2);
        }

        var members = new Dictionary<int, HashSet<int>>();
        foreach (var pair in edges.Keys)
        {
            var root = Find(pair.Item1);
            if (!members.TryGetValue(root, out var set))
            {
                set = new HashSet<int>();
                members[root] = set;
            }

            set.Add(pair.Item1);
            set.Add(pair.Item2);
        }

        var byId = prepared.ToDictionary(item => item.Id);
        var groups = new List<DuplicateMatchGroup>();
        foreach (var memberIds in members.Values)
        {
            var ids = memberIds.OrderBy(id => id).ToArray();
            var codes = new HashSet<string>(StringComparer.Ordinal);
            var pairs = new List<DuplicateMatchPair>();
            foreach (var (key, reasons) in edges)
            {
                if (!memberIds.Contains(key.Item1))
                {
                    continue;
                }

                codes.UnionWith(reasons);
                if (memberIds.Contains(key.Item2))
                {
                    var ordered = ReasonOrder.Where(reasons.Contains).ToArray();
                    pairs.Add(new DuplicateMatchPair(key.Item1, key.Item2, ordered, JoinReasons(ordered)));
                }
            }

            var orderedCodes = ReasonOrder.Where(codes.Contains).ToArray();
            var likelihood = Likelihood(codes);
            var explanation = $"These {ids.Length} active records have {JoinReasons(orderedCodes)}.";
            pairs.Sort((left, right) => left.LeftId != right.LeftId ? left.LeftId.CompareTo(right.LeftId) : left.RightId.CompareTo(right.RightId));
            groups.Add(new DuplicateMatchGroup(likelihood, ids, orderedCodes, explanation, pairs));
        }

        groups.Sort((left, right) =>
        {
            var rank = Rank(left.Likelihood).CompareTo(Rank(right.Likelihood));
            if (rank != 0)
            {
                return rank;
            }

            var last = string.Compare(byId[left.Ids[0]].Last, byId[right.Ids[0]].Last, StringComparison.Ordinal);
            return last != 0 ? last : left.Ids[0].CompareTo(right.Ids[0]);
        });

        return groups;
    }

    public static string JoinReasons(IReadOnlyList<string> codes) =>
        string.Join("; ", codes.Select(code => ReasonText[code]));

    private static int Rank(string likelihood) => likelihood switch
    {
        "High" => 0,
        "Medium" => 1,
        _ => 2
    };

    private static string Likelihood(HashSet<string> codes)
    {
        if (codes.Contains("H1") ||
            (codes.Contains("H2") && (codes.Contains("H4") || codes.Contains("H5"))) ||
            (codes.Contains("GRID") && (codes.Contains("H1") || codes.Contains("H2"))))
        {
            return "High";
        }

        if (codes.Contains("H2") || codes.Contains("GRID") || codes.Contains("H4") || codes.Contains("H5"))
        {
            return "Medium";
        }

        return "Low";
    }

    private static Prepared Prepare(DuplicateCandidate record)
    {
        var tokens = Normalize(record.Name).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !Suffixes.Contains(token))
            .ToArray();
        var full = string.Join(' ', tokens);
        var firstLast = tokens.Length >= 2 ? $"{tokens[0]} {tokens[^1]}" : full;
        return new Prepared(
            record.Id,
            full,
            firstLast,
            tokens.Length == 0 ? string.Empty : tokens[^1],
            Branch(record.ServiceBranch),
            (record.FlagGrid ?? string.Empty).Trim().ToUpperInvariant(),
            Normalize(string.IsNullOrWhiteSpace(record.SubmitterEmail) ? record.Submitter : record.SubmitterEmail));
    }

    internal static string Normalize(string? value)
    {
        var stripped = Regex.Replace(value ?? string.Empty, @"\(.*?\)|"".*?""", string.Empty);
        stripped = stripped.ToLowerInvariant();
        var spaced = Regex.Replace(stripped, @"[^\w\s]", " ");
        return Regex.Replace(spaced, @"\s+", " ").Trim();
    }

    private static string Branch(string? value)
    {
        var normalized = Normalize(value);
        return BranchAliases.TryGetValue(normalized, out var alias) ? alias : normalized;
    }

    internal static bool Near(string left, string right)
    {
        var a = GridPosition(left);
        var b = GridPosition(right);
        if (a is null || b is null)
        {
            return false;
        }

        if (a.Value.Section == b.Value.Section && Math.Abs(a.Value.Row - b.Value.Row) == 1)
        {
            return true;
        }

        if (a.Value.Row != b.Value.Row || a.Value.Section.Length != b.Value.Section.Length || a.Value.Section.Length == 0)
        {
            return false;
        }

        var prefixLength = a.Value.Section.Length - 1;
        return string.CompareOrdinal(a.Value.Section, 0, b.Value.Section, 0, prefixLength) == 0 &&
            Math.Abs(a.Value.Section[^1] - b.Value.Section[^1]) == 1;
    }

    private static (string Section, int Row)? GridPosition(string grid)
    {
        var match = Regex.Match(grid, @"^([A-Z]+)-?(\d+)$");
        if (!match.Success || !int.TryParse(match.Groups[2].Value, out var row))
        {
            return null;
        }

        return (match.Groups[1].Value, row);
    }

    private sealed record Prepared(
        int Id,
        string Full,
        string FirstLast,
        string Last,
        string Branch,
        string Grid,
        string Submitter);
}

public sealed record DuplicateCandidate(
    int Id,
    string Name,
    string? ServiceBranch,
    string? FlagGrid,
    string? Submitter,
    string? SubmitterEmail);

public sealed record DuplicateMatchGroup(
    string Likelihood,
    IReadOnlyList<int> Ids,
    IReadOnlyList<string> ReasonCodes,
    string Explanation,
    IReadOnlyList<DuplicateMatchPair> Pairs);

public sealed record DuplicateMatchPair(
    int LeftId,
    int RightId,
    IReadOnlyList<string> ReasonCodes,
    string Explanation);
