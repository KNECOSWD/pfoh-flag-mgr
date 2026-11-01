using System.Text.Json;
using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

public class UtcDateTimeJsonConverterTests
{
    [Fact]
    public void Unspecified_and_utc_values_are_written_with_a_z()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new UtcDateTimeJsonConverter());
        options.Converters.Add(new UtcNullableDateTimeJsonConverter());

        var unspecified = new DateTime(2026, 11, 1, 21, 4, 0, DateTimeKind.Unspecified);
        var utc = new DateTime(2026, 11, 1, 21, 4, 0, DateTimeKind.Utc);
        var payload = JsonSerializer.Serialize(new Sample(unspecified, utc, null), options);

        Assert.Contains("2026-11-01T21:04:00.0000000Z", payload, StringComparison.Ordinal);
        Assert.Contains("\"Optional\":null", payload, StringComparison.Ordinal);

        var read = JsonSerializer.Deserialize<Sample>(payload, options);
        Assert.Equal(DateTimeKind.Utc, read!.When.Kind);
        Assert.Equal(utc, read.Also);
        Assert.Null(read.Optional);
    }

    private sealed record Sample(DateTime When, DateTime Also, DateTime? Optional);
}