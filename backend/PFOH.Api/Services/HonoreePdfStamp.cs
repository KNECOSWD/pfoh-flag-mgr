using System.Security.Cryptography;
using System.Text;
using PFOH.Api.Models;

namespace PFOH.Api.Services;

/// <summary>
/// A stamp is a hash of the card fields, the photo ETag, and
/// <see cref="FormatVersion"/>. Blob metadata stores it. A served PDF whose
/// stamp does not match is rebuilt before it is returned.
/// </summary>
public static class HonoreePdfStamp
{
    public const int FormatVersion = 1;

    public const string MetadataKey = "pfohstamp";

    public static string Compute(
        Honoree honoree,
        string? flagGridName,
        string? serviceBranchName,
        string? sponsorName,
        string? photoEtag)
    {
        var payload = string.Join(
            '\u001f',
            FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            honoree.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            honoree.FirstName ?? "",
            honoree.MiddleName ?? "",
            honoree.LastName ?? "",
            honoree.Suffix ?? "",
            honoree.Nickname ?? "",
            honoree.Rank ?? "",
            honoree.ServiceBranchId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            serviceBranchName ?? "",
            honoree.StartYear?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            honoree.EndYear?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            honoree.DatesUserEntry ?? "",
            honoree.ConflictsServed ?? "",
            honoree.Awards ?? "",
            honoree.Description ?? "",
            honoree.KIA ? "1" : "0",
            honoree.PhotoFileName ?? "",
            photoEtag ?? "",
            honoree.FlagGridId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            flagGridName ?? "",
            honoree.SponsorId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            sponsorName ?? "",
            honoree.PhoneNumber ?? "",
            honoree.EmailAddress ?? "",
            honoree.IsActive ? "1" : "0",
            honoree.DeletedDate?.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "");

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
