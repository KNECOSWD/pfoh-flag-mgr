using System.Text.Json;
using Xunit;

namespace PFOH.Snapshot.Tests;

public class SnapshotFieldSpecFileTests
{
    [Fact]
    public void Snapshot_field_spec_matches_the_json_file_exactly()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepoFile("backend", "PFOH.Snapshot", "snapshot-field-spec.json")));
        var root = document.RootElement;
        var tables = root.GetProperty("db");
        var keys = tables.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(SnapshotFieldSpec.TableColumns.Keys.ToHashSet(StringComparer.Ordinal), keys);

        foreach (var property in tables.EnumerateObject())
        {
            var columns = new List<string>();
            var derived = new List<string>();
            foreach (var item in property.Value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    columns.Add(item.GetString()!);
                }
                else
                {
                    derived.Add(item.GetProperty("derived").GetString()!);
                }
            }

            Assert.Equal(SnapshotFieldSpec.TableColumns[property.Name], columns);
            if (property.Name == "dbo.Honorees")
            {
                Assert.Equal(SnapshotFieldSpec.HonoreeDerivedFlags, derived);
            }
            else
            {
                Assert.Empty(derived);
            }
        }

        var directory = root.GetProperty("directory");
        Assert.Equal(SnapshotFieldSpec.DirectoryUserFields, Strings(directory, "users_select"));
        Assert.Equal(SnapshotFieldSpec.IdentityFields, Strings(directory, "identities_keep"));
        Assert.Equal(SnapshotFieldSpec.SignInActivityFields, Strings(directory, "signInActivity_keep"));
        Assert.Equal(string.Join(',', SnapshotFieldSpec.DirectoryUserFields), SnapshotFieldSpec.DirectoryUserSelect);
    }

    private static string[] Strings(JsonElement parent, string name)
    {
        return parent.GetProperty(name).EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VERSION")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }
}
