using System.Text.RegularExpressions;

namespace ClaudeSupervisor.Services;

/// <summary>
/// Extracts a usage-limit reset time from OCR text, and parses times typed by the user.
/// </summary>
public static partial class ResetTimeParser
{
    // A clock time such as "3pm", "3:00 PM", "15:00", "10 p.m.".
    private const string TimeToken = @"(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ap>a\.?m\.?|p\.?m\.?)?";

    // A reset keyword followed (within a short span) by a time token.
    // Matches "resets 3pm", "your limit will reset at 3:00 PM", "available again at 15:00".
    [GeneratedRegex(
        @"(?:reset|resets|reset at|available|again|back|renews)\D{0,40}?" + TimeToken,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResetRegex();

    [GeneratedRegex(@"^\s*" + TimeToken + @"\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LoneTimeRegex();

    /// <summary>
    /// Scans OCR text for a limit-reset time. On success, returns the next future
    /// occurrence of that clock time (local) and a human display string like "3:00 PM".
    /// </summary>
    public static bool TryExtractFromOcr(string ocrText, out DateTime resetLocal, out string display)
    {
        resetLocal = default;
        display = string.Empty;
        if (string.IsNullOrWhiteSpace(ocrText))
            return false;

        string normalized = Regex.Replace(ocrText, @"\s+", " ");
        Match m = ResetRegex().Match(normalized);
        if (!m.Success)
            return false;

        return Build(m, out resetLocal, out display);
    }

    /// <summary>Parses a bare time the user typed into the field, e.g. "3pm" or "15:00".</summary>
    public static bool TryParseField(string field, out DateTime resetLocal, out string display)
    {
        resetLocal = default;
        display = string.Empty;
        if (string.IsNullOrWhiteSpace(field))
            return false;

        Match m = LoneTimeRegex().Match(field.Trim());
        return m.Success && Build(m, out resetLocal, out display);
    }

    private static bool Build(Match m, out DateTime resetLocal, out string display)
    {
        resetLocal = default;
        display = string.Empty;

        if (!int.TryParse(m.Groups["h"].Value, out int hour))
            return false;

        int minute = m.Groups["m"].Success && int.TryParse(m.Groups["m"].Value, out int mm) ? mm : 0;

        string ap = m.Groups["ap"].Value.Replace(".", "").ToLowerInvariant();
        if (ap == "pm" && hour < 12) hour += 12;
        else if (ap == "am" && hour == 12) hour = 0;

        if (hour is < 0 or > 23 || minute is < 0 or > 59)
            return false;

        resetLocal = NextOccurrence(hour, minute);
        display = resetLocal.ToString("h:mm tt");
        return true;
    }

    /// <summary>The next time today/tomorrow that the clock reads hour:minute (local).</summary>
    private static DateTime NextOccurrence(int hour, int minute)
    {
        DateTime now = DateTime.Now;
        DateTime candidate = new(now.Year, now.Month, now.Day, hour, minute, 0, DateTimeKind.Local);
        if (candidate <= now)
            candidate = candidate.AddDays(1);
        return candidate;
    }
}
