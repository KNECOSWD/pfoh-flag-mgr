using System.Globalization;
using System.Text;

namespace PFOH.Api.Services;

public static class ReasonText
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var buffer = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.Format)
            {
                continue;
            }

            if (char.IsControl(character) && character is not '\n' and not '\r' and not '\t')
            {
                continue;
            }

            buffer.Append(character);
        }

        return buffer.ToString().Trim();
    }
}
