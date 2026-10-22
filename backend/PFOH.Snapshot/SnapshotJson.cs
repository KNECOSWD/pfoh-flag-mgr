using System.Text.Json;

namespace PFOH.Snapshot;

public static class SnapshotJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    public static byte[] ToUtf8(object document)
    {
        return JsonSerializer.SerializeToUtf8Bytes(document, Options);
    }
}
