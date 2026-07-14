using System.Text.RegularExpressions;

namespace ClaudeSupervisor.Services;

/// <summary>
/// Extracts a usage-limit reset time from OCR text and from user-typed times.
/// All absolute clock times are interpreted and displayed in India Standard Time.
/// </summary>
public static partial class ResetTimeParser
{
    /// <summary>India Standard Time — UTC+5:30, no daylight saving.</summary>
    public static readonly TimeSpan IstOffset = TimeSpan.FromMinutes(330);

    // Relative: "resets in 3 hr 55 min", "resets in 45 min", "resets in 2 hours".
    [GeneratedRegex(
        @"resets?\s+in\s+(?:(?<h>\d+)\s*(?:h|hr|hrs|hour|hours)\b)?\s*(?:(?<m>\d+)\s*(?:m|min|mins|minute|minutes)\b)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RelativeRegex();

    // Absolute: "resets at 3:00 PM", "Resets Mon 2:29 AM", "resets 3pm".
    // The (?!in) guard keeps it from matching the relative "resets in …" form.
    [GeneratedRegex(
        @"resets?\s+(?!in\b)(?:at\s+)?(?:(?:mon|tue|wed|thu|fri|sat|sun)[a-z]*\s+)?(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ap>a\.?m\.?|p\.?m\.?)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AbsoluteRegex();

    // A bare time typed into the field: "3pm", "3:00 PM", "15:00".
    [GeneratedRegex(
        @"^\s*(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ap>a\.?m\.?|p\.?m\.?)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FieldRegex();

    /// <summary>
    /// Scans OCR text for reset times (relative durations and absolute clock times)
    /// and returns the soonest one in the future.
    /// </summary>
    public static bool TryExtractFromOcr(string ocrText, out DateTimeOffset resetAt)
    {
        resetAt = default;
        if (string.IsNullOrWhiteSpace(ocrText))
            return false;

        string t = Regex.Replace(ocrText, @"\s+", " ");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var candidates = new List<DateTimeOffset>();

        foreach (Match m in RelativeRegex().Matches(t))
        {
            int h = m.Groups["h"].Success ? int.Parse(m.Groups["h"].Value) : 0;
            int min = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value) : 0;
            if (h == 0 && min == 0)
                continue; // "resets in" with no readable duration
            candidates.Add(now.AddHours(h).AddMinutes(min));
        }

        foreach (Match m in AbsoluteRegex().Matches(t))
        {
            if (TryBuildAbsolute(m, out DateTimeOffset dto))
                candidates.Add(dto);
        }

        DateTimeOffset? soonest = candidates.Where(c => c > now).OrderBy(c => c).Cast<DateTimeOffset?>().FirstOrDefault();
        if (soonest is null)
            return false;

        resetAt = soonest.Value;
        return true;
    }

    /// <summary>Parses a bare time the user typed, interpreting it as the next IST occurrence.</summary>
    public static bool TryParseField(string field, out DateTimeOffset resetAt)
    {
        resetAt = default;
        if (string.IsNullOrWhiteSpace(field))
            return false;

        Match m = FieldRegex().Match(field.Trim());
        if (!m.Success)
            return false;

        return TryBuildClock(m, requireColonOrAmPm: false, out resetAt);
    }

    /// <summary>Full, unambiguous display: "3:39 PM IST, Tue Jul 14".</summary>
    public static string FormatIst(DateTimeOffset dto) =>
        dto.ToOffset(IstOffset).ToString("h:mm tt 'IST', ddd MMM d");

    /// <summary>Short, round-trippable clock for the input field: "3:39 PM".</summary>
    public static string FormatClock(DateTimeOffset dto) =>
        dto.ToOffset(IstOffset).ToString("h:mm tt");

    private static bool TryBuildAbsolute(Match m, out DateTimeOffset resetAt) =>
        TryBuildClock(m, requireColonOrAmPm: true, out resetAt);

    private static bool TryBuildClock(Match m, bool requireColonOrAmPm, out DateTimeOffset resetAt)
    {
        resetAt = default;

        bool hasColon = m.Groups["m"].Success;
        bool hasAp = m.Groups["ap"].Success;
        if (requireColonOrAmPm && !hasColon && !hasAp)
            return false; // reject bare numbers like a stray "3" so durations aren't read as clocks

        if (!int.TryParse(m.Groups["h"].Value, out int hour))
            return false;
        int minute = hasColon && int.TryParse(m.Groups["m"].Value, out int mm) ? mm : 0;

        string ap = m.Groups["ap"].Value.Replace(".", "").ToLowerInvariant();
        if (ap == "pm" && hour < 12) hour += 12;
        else if (ap == "am" && hour == 12) hour = 0;

        if (hour is < 0 or > 23 || minute is < 0 or > 59)
            return false;

        resetAt = NextOccurrenceIst(hour, minute);
        return true;
    }

    /// <summary>The next instant whose IST wall clock reads hour:minute.</summary>
    private static DateTimeOffset NextOccurrenceIst(int hour, int minute)
    {
        DateTimeOffset nowIst = DateTimeOffset.UtcNow.ToOffset(IstOffset);
        var candidate = new DateTimeOffset(nowIst.Year, nowIst.Month, nowIst.Day, hour, minute, 0, IstOffset);
        if (candidate <= nowIst)
            candidate = candidate.AddDays(1);
        return candidate;
    }
}
