using System.Text;
using System.Text.RegularExpressions;

namespace TdsProxy.Sql;

/// <summary>
/// Dates and times: the parts T-SQL counts differently from DuckDB, CONVERT's styles, FORMAT's
/// .NET patterns and ISDATE.
///
/// Several of these have to behave differently for a date, a time and a timestamp, and the type of
/// an argument is not known until DuckDB binds the statement. They are written as
/// list_transform([x], lambda v: CASE WHEN typeof(v) = ... END)[1], which evaluates x once and lets
/// DuckDB pick the branch from the bound type; every branch is written so that it binds whatever
/// the type is, and only the one that matches is ever run.
/// </summary>
internal static partial class TSqlTranslator
{
    /// <summary>T-SQL date-part keywords and their abbreviations, as the names used below.</summary>
    private static string DatePart(string part)
    {
        string name = part.Trim().Trim('\'').ToLowerInvariant();
        return name switch
        {
            "year" or "yy" or "yyyy" => "year",
            "quarter" or "qq" or "q" => "quarter",
            "month" or "mm" or "m" => "month",
            "dayofyear" or "dy" or "y" => "dayofyear",
            "day" or "dd" or "d" => "day",
            "week" or "wk" or "ww" => "week",
            "iso_week" or "isowk" or "isoww" => "isoweek",
            "weekday" or "dw" or "w" => "dayofweek",
            "hour" or "hh" => "hour",
            "minute" or "mi" or "n" => "minute",
            "second" or "ss" or "s" => "second",
            "millisecond" or "ms" => "millisecond",
            "microsecond" or "mcs" => "microsecond",
            "nanosecond" or "ns" => "nanosecond",
            _ => name,
        };
    }

    /// <summary>A string literal standing for a date is cast to one; DuckDB will not guess.</summary>
    private static string DateValue(string argument, Func<string, string?> literal) =>
        literal(argument) is null ? argument : $"CAST({argument} AS TIMESTAMP)";

    /// <summary>
    /// DATEPART as T-SQL counts, as an int. Three parts count differently in DuckDB: its weekday
    /// starts at 0 on Sunday rather than 1; its week is the ISO week, where T-SQL's weeks start on
    /// Sunday and 1 January is always in week 1; and its sub-second parts run across the whole
    /// minute rather than within the second.
    /// </summary>
    private static string DatePartOf(string part, string date) => part switch
    {
        "dayofweek" => $"CAST(dayofweek({date}) + 1 AS INTEGER)",
        "week" => $"CAST(list_transform([{date}], lambda __w: " +
                  $"(dayofyear(__w) + dayofweek(date_trunc('year', __w)) - 1) // 7 + 1)[1] AS INTEGER)",
        "isoweek" => $"CAST(date_part('week', {date}) AS INTEGER)",
        "millisecond" => $"CAST(date_part('millisecond', {date}) % 1000 AS INTEGER)",
        "microsecond" => $"CAST(date_part('microsecond', {date}) % 1000000 AS INTEGER)",
        "nanosecond" => $"CAST(date_part('microsecond', {date}) % 1000000 * 1000 AS INTEGER)",
        _ => $"CAST(date_part('{part}', {date}) AS INTEGER)",
    };

    /// <summary>DATENAME returns text; only month and weekday differ from the numeric part.</summary>
    private static string DateName(string part, string date) => part switch
    {
        "month" => $"monthname({date})",
        "dayofweek" => $"dayname({date})",
        _ => $"CAST({DatePartOf(part, date)} AS VARCHAR)",
    };

    /// <summary>
    /// DATEDIFF counts the boundaries crossed between two dates, as DuckDB's date_diff does -
    /// except for weeks, where DuckDB counts whole seven-day spans and T-SQL counts the Sundays in
    /// between. Shifting both dates a day on makes DuckDB's Monday-based week start fall on T-SQL's
    /// Sunday, and the two week starts are then a whole number of weeks apart.
    /// </summary>
    private static string DateDiff(string part, string start, string end) => part switch
    {
        "week" => $"(date_diff('day', date_trunc('week', CAST({start} AS DATE) + 1), " +
                  $"date_trunc('week', CAST({end} AS DATE) + 1)) // 7)",
        "dayofyear" or "dayofweek" => $"date_diff('day', {start}, {end})",
        _ => $"date_diff('{part}', {start}, {end})",
    };

    /// <summary>
    /// DATEADD becomes interval arithmetic. The to_*() helpers are used instead of an INTERVAL
    /// literal because the amount is an arbitrary expression, not a constant.
    /// </summary>
    private static string? DateAdd(string part, string amount, string date) => part switch
    {
        "year" => $"({date} + to_years(CAST({amount} AS INTEGER)))",
        "quarter" => $"({date} + to_months(CAST({amount} AS INTEGER) * 3))",
        "month" => $"({date} + to_months(CAST({amount} AS INTEGER)))",
        "week" => $"({date} + to_days(CAST({amount} AS INTEGER) * 7))",
        "day" or "dayofyear" or "dayofweek" => $"({date} + to_days(CAST({amount} AS INTEGER)))",
        "hour" => $"({date} + to_hours(CAST({amount} AS BIGINT)))",
        "minute" => $"({date} + to_minutes(CAST({amount} AS BIGINT)))",
        "second" => $"({date} + to_seconds(CAST({amount} AS BIGINT)))",
        "millisecond" => $"({date} + to_milliseconds(CAST({amount} AS BIGINT)))",
        "microsecond" => $"({date} + to_microseconds(CAST({amount} AS BIGINT)))",
        _ => null,
    };

    /// <summary>
    /// One CONVERT style for dates and times: the date half, the time half, and what joins them.
    /// A date is written with the date half alone and a time with the time half alone; a style
    /// with no half for the value is an error, as it is in SQL Server.
    ///
    /// The patterns are strftime's, plus {d2} for a day padded with a space and {f7} for the seven
    /// fractional digits of datetime2 - which is how DaffyDB reports a timestamp, so it is
    /// datetime2's output that is copied, not datetime's three digits. <paramref name="Pad"/>
    /// right-aligns the time half when joined (or always, with <paramref name="PadAlone"/>);
    /// <paramref name="OptionalFraction"/> leaves the fraction off when it is zero.
    /// </summary>
    private sealed record DateStyle(string? Date, string? Time, string Join = " ", int Pad = 0,
                                    bool PadAlone = false, bool OptionalFraction = false);

    /// <summary>The styles, as SQL Server writes them for datetime2, date and time.</summary>
    private static readonly Dictionary<int, DateStyle> DateStyles = BuildDateStyles();

    private static Dictionary<int, DateStyle> BuildDateStyles()
    {
        var styles = new Dictionary<int, DateStyle>();

        // Styles 1-12 have a two-digit year; 101-112 are the same with four.
        void Pair(int style, string twoDigitYear, string fourDigitYear)
        {
            styles[style] = new DateStyle(twoDigitYear, null);
            styles[style + 100] = new DateStyle(fourDigitYear, null);
        }

        Pair(1, "%m/%d/%y", "%m/%d/%Y");
        Pair(2, "%y.%m.%d", "%Y.%m.%d");
        Pair(3, "%d/%m/%y", "%d/%m/%Y");
        Pair(4, "%d.%m.%y", "%d.%m.%Y");
        Pair(5, "%d-%m-%y", "%d-%m-%Y");
        Pair(6, "%d %b %y", "%d %b %Y");
        Pair(7, "%b %d, %y", "%b %d, %Y");
        Pair(10, "%m-%d-%y", "%m-%d-%Y");
        Pair(11, "%y/%m/%d", "%Y/%m/%d");
        Pair(12, "%y%m%d", "%Y%m%d");

        styles[0] = styles[100] = new DateStyle("%b {d2} %Y", "%-I:%M%p", Pad: 7);
        styles[9] = styles[109] = new DateStyle("%b {d2} %Y", "%-I:%M:%S.{f7}%p", Pad: 18);
        styles[8] = styles[24] = styles[108] = new DateStyle(null, "%H:%M:%S");
        styles[13] = styles[113] = new DateStyle("%d %b %Y", "%H:%M:%S.{f7}");
        styles[14] = styles[114] = new DateStyle(null, "%H:%M:%S.{f7}");
        styles[20] = styles[120] = new DateStyle("%Y-%m-%d", "%H:%M:%S");
        styles[21] = styles[25] = styles[121] = new DateStyle("%Y-%m-%d", "%H:%M:%S.{f7}");
        styles[22] = new DateStyle("%m/%d/%y", "%-I:%M:%S %p", Pad: 11, PadAlone: true);
        styles[23] = new DateStyle("%Y-%m-%d", null);
        styles[126] = styles[127] = new DateStyle("%Y-%m-%d", "%H:%M:%S", Join: "T", OptionalFraction: true);
        return styles;
    }

    /// <summary>A strftime pattern, with {d2} and {f7}, applied to the timestamp expression <paramref name="t"/>.</summary>
    private static string Strftime(string pattern, string t, Func<string, string> quote)
    {
        var parts = Regex.Split(pattern, @"(\{d2\}|\{f7\})")
            .Where(piece => piece.Length > 0)
            .Select(piece => piece switch
            {
                "{d2}" => $"lpad(strftime({t}, {quote("%-d")}), 2, {quote(" ")})",
                "{f7}" => $"(strftime({t}, {quote("%f")}) || {quote("0")})",
                _ => $"strftime({t}, {quote(piece)})",
            })
            .ToList();

        return parts.Count == 1 ? parts[0] : "(" + string.Join(" || ", parts) + ")";
    }

    private static string TimeHalf(DateStyle style, string t, bool alone, Func<string, string> quote)
    {
        string time = Strftime(style.Time!, t, quote);

        if (style.OptionalFraction)
            time = $"({time} || CASE WHEN date_part('microsecond', {t}) % 1000000 = 0 THEN {quote("")} " +
                   $"ELSE {quote(".")} || strftime({t}, {quote("%f")}) || {quote("0")} END)";

        if (style.Pad > 0 && (!alone || style.PadAlone))
            time = $"lpad({time}, {style.Pad}, {quote(" ")})";

        return time;
    }

    private static string StyledText(DateStyle style, string t, Func<string, string> quote)
    {
        if (style.Date is null) return TimeHalf(style, t, alone: true, quote);
        if (style.Time is null) return Strftime(style.Date, t, quote);
        return $"({Strftime(style.Date, t, quote)} || {quote(style.Join)} || {TimeHalf(style, t, alone: false, quote)})";
    }

    /// <summary>
    /// CONVERT and TRY_CONVERT. A style matters in two directions: writing a date or time as text,
    /// and reading text as a date. Otherwise CONVERT is a CAST, and the CAST rules do the rest -
    /// including cutting text to a varchar(n)'s length.
    /// </summary>
    private static string? Convert(List<string> a, bool isTry, Func<string, string?> literal, Func<string, string> quote)
    {
        if (a.Count < 2) return null;

        string type = (literal(a[0]) ?? a[0]).Trim();
        string value = a[1];
        string cast = isTry ? "TRY_CAST" : "CAST";

        if (a.Count >= 3 && int.TryParse(a[2].Trim().Trim('(', ')').Trim(), out int style))
        {
            var text = Regex.Match(type, @"^n?(?:var)?char\s*(?:\(\s*(?<n>\d+|max)\s*\))?$", RegexOptions.IgnoreCase);
            if (text.Success)
            {
                string written = WriteWithStyle(value, style, isTry, quote);
                return text.Groups["n"].Success && !text.Groups["n"].Value.Equals("max", StringComparison.OrdinalIgnoreCase)
                    ? $"left({written}, {text.Groups["n"].Value})"
                    : written;
            }

            if (DateStyles.TryGetValue(style, out var parse) &&
                Regex.IsMatch(type, @"^(?:date|datetime|datetime2|smalldatetime)\s*(?:\(\s*\d+\s*\))?$", RegexOptions.IgnoreCase))
                return ReadWithStyle(value, type, parse, isTry, quote);
        }

        return $"{cast}({value} AS {type})";
    }

    /// <summary>
    /// A date, time or timestamp written in a style. Anything else - text most of all - comes back
    /// as plain text, since SQL Server ignores a date style for a value that is not a date.
    /// </summary>
    private static string WriteWithStyle(string value, int number, bool isTry, Func<string, string> quote)
    {
        DateStyles.TryGetValue(number, out var style);

        string Fail(string from) => isTry
            ? "NULL"
            : style is null
                ? $"error({quote($"{number} is not a valid style number when converting from {from} to a character string.")})"
                : $"error({quote($"Error converting data type {from} to varchar.")})";

        const string timestamp = "TRY_CAST(__c AS TIMESTAMP)";
        string timeOfDay = $"TRY_CAST({quote("1900-01-01 ")} || CAST(__c AS VARCHAR) AS TIMESTAMP)";

        string date = style?.Date is not null ? StyledText(style with { Time = null }, timestamp, quote) : Fail("date");
        string time = style?.Time is not null ? TimeHalf(style, timeOfDay, alone: true, quote) : Fail("time");
        string full = style is not null ? StyledText(style, timestamp, quote) : Fail("datetime2");

        return $"list_transform([{value}], lambda __c: CASE " +
               $"WHEN typeof(__c) = {quote("DATE")} THEN {date} " +
               $"WHEN typeof(__c) = {quote("TIME")} THEN {time} " +
               $"WHEN typeof(__c) LIKE {quote("TIMESTAMP%")} THEN {full} " +
               $"ELSE CAST(__c AS VARCHAR) END)[1]";
    }

    /// <summary>
    /// Text read as a date in a style's order - 04/10/2026 is 4 October in style 103 - with or
    /// without a time after it. Text the style does not describe falls back to an ordinary CAST.
    /// </summary>
    private static string ReadWithStyle(string value, string type, DateStyle style, bool isTry, Func<string, string> quote)
    {
        string[] times = ["%H:%M", "%H:%M:%S", "%H:%M:%S.%f", "%I:%M%p", "%I:%M %p", "%I:%M:%S%p", "%I:%M:%S %p"];
        var formats = new List<string>();
        string? date = style.Date?.Replace("{d2}", "%d");

        if (date is null) formats.AddRange(times);
        else
        {
            formats.Add(date);
            formats.AddRange(times.Select(t => date + style.Join + t));
        }

        string list = "[" + string.Join(", ", formats.Select(quote)) + "]";
        string target = MapType(type);
        string cast = isTry ? "TRY_CAST" : "CAST";
        string parsed = $"try_strptime(trim(CAST(__p AS VARCHAR)), {list})";

        // A two-digit year. DuckDB's %y reads 00 to 68 as 2000 to 2068; SQL Server, with its
        // default two-digit year cutoff of 2049, reads 50 to 99 as 1950 to 1999. They differ on
        // 50 to 68 alone, so those go back a century. Only what the style's own pattern read is
        // moved: text that fell back to a plain CAST had its year written out in full.
        if (date?.Contains("%y") == true)
            parsed = $"CASE WHEN year({parsed}) BETWEEN 2050 AND 2068 THEN {parsed} - INTERVAL 100 YEAR ELSE {parsed} END";

        return $"list_transform([{value}], lambda __p: CASE WHEN typeof(__p) = {quote("VARCHAR")} " +
               $"THEN {cast}(COALESCE({parsed}, " +
               $"{cast}(CAST(__p AS VARCHAR) AS TIMESTAMP)) AS {target}) " +
               $"ELSE {cast}(__p AS {target}) END)[1]";
    }

    /// <summary>.NET's standard date formats, as en-US spells them out.</summary>
    private static readonly Dictionary<char, string> StandardDateFormats = new()
    {
        ['d'] = "M/d/yyyy",
        ['D'] = "dddd, MMMM d, yyyy",
        ['f'] = "dddd, MMMM d, yyyy h:mm tt",
        ['F'] = "dddd, MMMM d, yyyy h:mm:ss tt",
        ['g'] = "M/d/yyyy h:mm tt",
        ['G'] = "M/d/yyyy h:mm:ss tt",
        ['M'] = "MMMM d",
        ['m'] = "MMMM d",
        ['Y'] = "MMMM yyyy",
        ['y'] = "MMMM yyyy",
        ['t'] = "h:mm tt",
        ['T'] = "h:mm:ss tt",
        ['s'] = "yyyy'-'MM'-'dd'T'HH':'mm':'ss",
        ['u'] = "yyyy'-'MM'-'dd HH':'mm':'ss'Z'",
        ['o'] = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffff",
        ['O'] = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffff",
        ['r'] = "ddd, dd MMM yyyy HH':'mm':'ss 'GMT'",
        ['R'] = "ddd, dd MMM yyyy HH':'mm':'ss 'GMT'",
    };

    /// <summary>
    /// A .NET date format as a strftime pattern, or null for one with no strftime equivalent (an
    /// era, a time zone, one or two fractional digits). Quoted text and \-escaped characters are
    /// copied as they are, as are letters .NET does not treat as specifiers.
    /// </summary>
    private static string? DotNetDateFormat(string pattern)
    {
        if (pattern.Length == 1)
            return StandardDateFormats.TryGetValue(pattern[0], out var standard) ? DotNetDateFormat(standard) : null;

        var result = new StringBuilder();
        void Copy(char c) => result.Append(c == '%' ? "%%" : c.ToString());

        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];

            if (c is '\'' or '"')
            {
                int close = pattern.IndexOf(c, i + 1);
                if (close < 0) return null;
                foreach (char quoted in pattern[(i + 1)..close]) Copy(quoted);
                i = close;
                continue;
            }

            if (c == '\\')
            {
                if (i + 1 >= pattern.Length) return null;
                Copy(pattern[++i]);
                continue;
            }

            // A % in front of a specifier only says "this one letter is a custom format".
            if (c == '%') continue;

            int run = 1;
            while (i + run < pattern.Length && pattern[i + run] == c) run++;

            string? specifier = c switch
            {
                'y' => run switch { 1 => "%-y", 2 => "%y", _ => "%Y" },
                'M' => run switch { 1 => "%-m", 2 => "%m", 3 => "%b", _ => "%B" },
                'd' => run switch { 1 => "%-d", 2 => "%d", 3 => "%a", _ => "%A" },
                'h' => run == 1 ? "%-I" : "%I",
                'H' => run == 1 ? "%-H" : "%H",
                'm' => run == 1 ? "%-M" : "%M",
                's' => run == 1 ? "%-S" : "%S",
                'f' => run switch { 3 => "%g", 6 => "%f", 7 => "%f0", _ => null },
                't' => run == 2 ? "%p" : null,
                'F' or 'z' or 'K' or 'g' => null,
                _ => "",
            };

            if (specifier is null) return null;
            if (specifier.Length > 0) result.Append(specifier);
            else for (int k = 0; k < run; k++) Copy(c);

            i += run - 1;
        }

        return result.ToString();
    }

    /// <summary>
    /// FORMAT(value, pattern [, culture]) for a date or a timestamp, with a .NET date pattern -
    /// 'yyyy-MM', 'ddd', 'MMMM d, yyyy' or a standard one such as 'd'. strftime only speaks
    /// English, so a culture other than en-US is left untranslated, and so is a pattern that is not
    /// a literal. A number, which FORMAT also takes, is reported as not translated rather than
    /// formatted as if it were a date.
    /// </summary>
    private static string? Format(List<string> a, Func<string, string?> literal, Func<string, string> quote)
    {
        if (a.Count is < 2 or > 3) return null;
        if (literal(a[1]) is not { } pattern) return null;
        if (a.Count == 3 && !string.Equals(literal(a[2]), "en-US", StringComparison.OrdinalIgnoreCase)) return null;
        if (DotNetDateFormat(pattern) is not { } strftime) return null;

        return $"list_transform([{a[0]}], lambda __f: CASE " +
               $"WHEN typeof(__f) = {quote("DATE")} OR typeof(__f) LIKE {quote("TIMESTAMP%")} " +
               $"THEN strftime(TRY_CAST(__f AS TIMESTAMP), {quote(strftime)}) " +
               $"ELSE error({quote("DaffyDB: FORMAT is translated for dates and timestamps only")}) END)[1]";
    }

    /// <summary>
    /// The text ISDATE accepts, as us_english reads it: month before day. These are the shapes SQL
    /// Server takes for a datetime - ISO, slashed, dotted and dashed dates, month names, a year or a
    /// month and year alone, a time alone, and a date followed by a time with up to three
    /// fractional digits.
    /// </summary>
    private static readonly string[] IsDateFormats = BuildIsDateFormats();

    private static string[] BuildIsDateFormats()
    {
        // try_strptime stops at the first format that reads, and %Y reads "22" as the year 22, so
        // the two-digit years come before the four-digit ones.
        string[] dates =
        [
            "%Y-%m-%d", "%Y/%m/%d", "%Y.%m.%d", "%Y%m%d",
            "%m/%d/%y", "%m-%d-%y", "%m.%d.%y", "%m/%d/%Y", "%m-%d-%Y", "%m.%d.%Y",
            "%d %b %Y", "%d-%b-%Y", "%d %B %Y", "%d-%B-%Y",
            "%b %d %Y", "%b %d, %Y", "%B %d %Y", "%B %d, %Y",
        ];
        string[] times =
        [
            "%H:%M", "%H:%M:%S", "%H:%M:%S.%g", "%H:%M:%S:%g",
            "%I:%M %p", "%I:%M%p", "%I:%M:%S %p", "%I:%M:%S%p", "%I %p", "%I%p",
        ];

        var formats = new List<string>(dates) { "%b %Y", "%B %Y", "%Y", "%Y-%m-%dZ" };
        formats.AddRange(times);
        foreach (string date in dates)
            formats.AddRange(times.Select(time => $"{date} {time}"));
        formats.AddRange(times.Take(4).Select(time => $"%Y-%m-%dT{time}"));
        return [.. formats];
    }

    /// <summary>
    /// ISDATE: 1 for a value that is, or reads as, a datetime between 1753 and 9999. A date or a
    /// timestamp is checked for its range; text is tried against the shapes above.
    /// </summary>
    private static string IsDate(string value, Func<string, string> quote)
    {
        string range = $"BETWEEN TIMESTAMP {quote("1753-01-01")} AND TIMESTAMP {quote("9999-12-31 23:59:59.999999")}";
        string formats = "[" + string.Join(", ", IsDateFormats.Select(quote)) + "]";

        return $"list_transform([{value}], lambda __d: CASE WHEN __d IS NULL THEN 0 " +
               $"WHEN typeof(__d) = {quote("DATE")} OR typeof(__d) LIKE {quote("TIMESTAMP%")} " +
               $"THEN CASE WHEN TRY_CAST(__d AS TIMESTAMP) {range} THEN 1 ELSE 0 END " +
               $"ELSE CASE WHEN try_strptime(trim(CAST(__d AS VARCHAR)), {formats}) {range} THEN 1 ELSE 0 END END)[1]";
    }
}
