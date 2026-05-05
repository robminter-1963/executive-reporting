using System.Globalization;

namespace TleReportingDashboard.Web.Services;

/// <summary>
/// Applies per-field display formatting at render / export time. Two syntaxes
/// are supported, auto-detected from the format string:
///
///   <list type="bullet">
///     <item>
///       <term>Mask</term>
///       <description>
///         String contains <c>9</c>, <c>A</c>, or <c>*</c>. Raw value is
///         stripped to its characters and walked through the mask:
///         <c>9</c> consumes the next digit, <c>A</c> consumes the next letter,
///         <c>*</c> consumes any character, and anything else is emitted as a
///         literal. Example: <c>(999) 999-9999</c> on <c>"8005551234"</c>
///         produces <c>"(800) 555-1234"</c>.
///       </description>
///     </item>
///     <item>
///       <term>.NET format string</term>
///       <description>
///         Passed to <c>value.ToString(format)</c> when the value is a
///         formattable primitive (number, date). Example: <c>C2</c>,
///         <c>N0</c>, <c>yyyy-MM-dd</c>, <c>MMM d, yyyy h:mm tt</c>.
///       </description>
///     </item>
///   </list>
///
/// A blank or null format returns the raw value's <c>ToString()</c>. Null or
/// DBNull values return empty string. The formatter is intentionally forgiving
/// — if formatting fails, it falls back to the raw string rather than throwing.
/// </summary>
public static class FieldFormatter
{
    // Short-date fallback used when DataType="date" and no explicit Format
    // was configured on the field. Without this, a DATE returned as a
    // DateTime at midnight renders as "1/15/2024 12:00:00 AM" via the
    // default ToString. Admins can still override by setting a Format.
    private const string DefaultDateFormat = "MM/dd/yyyy";

    // Default mask for DataType="phone" when the admin hasn't set a Format.
    // 10-digit US format covers the common case; admins can override with
    // any custom mask (e.g. "999.999.9999") via the field's Format field.
    private const string DefaultPhoneMask = "(999) 999-9999";

    // Back-compat overload for callers that don't know the DataType.
    public static string Format(object? value, string? format) =>
        Format(value, format, dataType: null);

    public static string Format(object? value, string? format, string? dataType)
    {
        if (value is null || value is DBNull) return string.Empty;

        // Phone numbers normalize the raw value before any masking. Source
        // data may include a leading "+1" (US country code) or just "+"
        // (international). Strip those so a 10-digit mask like "(999) 999-9999"
        // lines up with the body of the number — without this normalization
        // "+15551234567" formats as "(155) 512-3456" instead of "(555) 123-4567".
        //
        // Underflow guard: if the raw value carries fewer digits than the
        // mask demands (e.g. mask "(999) 999-9999" needs 10 digits but the
        // raw is "5551234"), masking would emit a half-formed result like
        // "(555) 123-4   ". Skip the mask in that case and return the raw
        // value as-is — typically a partially-entered or extension-style
        // number that the source system wrote without normalization.
        if (string.Equals(dataType, "phone", StringComparison.OrdinalIgnoreCase))
        {
            var raw = NormalizePhoneRaw(value.ToString() ?? string.Empty);
            var mask = string.IsNullOrWhiteSpace(format) ? DefaultPhoneMask : format;
            if (!IsMask(mask)) return raw;
            var digitCount = CountDigits(raw);
            // Underflow guard — see header comment above the dataType
            // branch. Overflow guard: anything beyond a 10-digit US
            // number is almost certainly international, an extension-
            // appended value, or a malformed export — none of which a
            // 10-digit US mask can format correctly. Returning the raw
            // value preserves the data instead of silently truncating
            // to the first 10 digits.
            if (digitCount < CountChar(mask, '9')) return raw;
            if (digitCount > 10) return raw;
            return ApplyMask(raw, mask);
        }

        if (string.IsNullOrWhiteSpace(format))
        {
            // No user format configured — pick a sensible default based on
            // the column's declared DataType. For "date", strip the time.
            // Other types fall through to ToString.
            if (string.Equals(dataType, "date", StringComparison.OrdinalIgnoreCase)
                && TryToDateTime(value, out var dt))
            {
                return dt.ToString(DefaultDateFormat, CultureInfo.CurrentCulture);
            }
            return value.ToString() ?? string.Empty;
        }

        return IsMask(format)
            ? ApplyMask(value.ToString() ?? string.Empty, format)
            : ApplyDotNetFormat(value, format);
    }

    // Strips a leading "+1" (US country code) or a bare "+" (other country
    // code marker) from a phone string. Whitespace is also trimmed since
    // raw exports from external systems sometimes pad with spaces. The
    // remainder still contains separators like "(", ")", "-", "." — those
    // are skipped naturally by the mask walker (which only consumes digits
    // for the '9' positions).
    private static string NormalizePhoneRaw(string raw)
    {
        var s = raw.Trim();
        if (s.StartsWith("+1")) return s[2..];
        if (s.StartsWith("+")) return s[1..];
        return s;
    }

    // Accepts DateTime, DateTimeOffset, DateOnly, and parseable strings —
    // Postgres date columns may surface as DateOnly on modern Npgsql, as
    // DateTime on older versions, or as a string after some round-trips.
    private static bool TryToDateTime(object value, out DateTime result)
    {
        switch (value)
        {
            case DateTime dt:
                result = dt;
                return true;
            case DateTimeOffset dto:
                result = dto.DateTime;
                return true;
            case DateOnly d:
                result = d.ToDateTime(TimeOnly.MinValue);
                return true;
            case string s when DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out var parsed):
                result = parsed;
                return true;
            default:
                result = default;
                return false;
        }
    }

    private static bool IsMask(string format)
    {
        foreach (var c in format)
        {
            if (c == '9' || c == 'A' || c == '*') return true;
        }
        return false;
    }

    private static int CountDigits(string s)
    {
        var count = 0;
        foreach (var c in s) if (c >= '0' && c <= '9') count++;
        return count;
    }

    private static int CountChar(string s, char target)
    {
        var count = 0;
        foreach (var c in s) if (c == target) count++;
        return count;
    }

    private static string ApplyMask(string raw, string mask)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        var result = new System.Text.StringBuilder(mask.Length);
        var rawIndex = 0;

        foreach (var maskChar in mask)
        {
            if (maskChar == '9')
            {
                // Consume the next digit from raw; skip non-digits. If we run
                // out of digits, stop rendering — the caller supplied fewer
                // characters than the mask expects.
                while (rawIndex < raw.Length && !char.IsDigit(raw[rawIndex])) rawIndex++;
                if (rawIndex >= raw.Length) break;
                result.Append(raw[rawIndex++]);
            }
            else if (maskChar == 'A')
            {
                while (rawIndex < raw.Length && !char.IsLetter(raw[rawIndex])) rawIndex++;
                if (rawIndex >= raw.Length) break;
                result.Append(raw[rawIndex++]);
            }
            else if (maskChar == '*')
            {
                if (rawIndex >= raw.Length) break;
                result.Append(raw[rawIndex++]);
            }
            else
            {
                // Literal character — always emitted.
                result.Append(maskChar);
            }
        }

        return result.ToString();
    }

    private static string ApplyDotNetFormat(object value, string format)
    {
        try
        {
            if (value is IFormattable f)
                return f.ToString(format, CultureInfo.CurrentCulture);

            // For string inputs (group-by values land here as strings after
            // dictionary grouping), try to parse to a type that can be formatted.
            if (value is string s)
            {
                if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out var dt))
                    return dt.ToString(format, CultureInfo.CurrentCulture);
                if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.CurrentCulture, out var dec))
                    return dec.ToString(format, CultureInfo.CurrentCulture);
                if (double.TryParse(s, NumberStyles.Any, CultureInfo.CurrentCulture, out var dbl))
                    return dbl.ToString(format, CultureInfo.CurrentCulture);
            }

            return value.ToString() ?? string.Empty;
        }
        catch
        {
            // Malformed format string — don't crash the grid over a typo.
            return value.ToString() ?? string.Empty;
        }
    }
}
