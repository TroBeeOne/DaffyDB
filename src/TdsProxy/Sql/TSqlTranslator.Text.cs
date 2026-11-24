using System.Text;
using System.Text.RegularExpressions;

namespace TdsProxy.Sql;

/// <summary>
/// Text and numbers: LIKE, CAST to a sized varchar, ISNUMERIC, the JSON functions and ROUND's
/// truncating form.
/// </summary>
internal static partial class TSqlTranslator
{
    /// <summary>
    /// Like <see cref="RewriteCalls"/>, but visits every NAME(...) exactly once, last first, and
    /// leaves a call alone when <paramref name="build"/> returns null. Going backwards keeps the
    /// position of every call still to come, and means a replacement that contains a call of the
    /// same name - CAST rewritten as left(CAST(...)) - is never rewritten again.
    /// </summary>
    private static string RewriteEachCall(string sql, string name, Func<List<string>, string?> build)
    {
        var matches = Regex.Matches(sql, $@"\b{name}\s*\(", RegexOptions.IgnoreCase);

        for (int m = matches.Count - 1; m >= 0; m--)
        {
            var match = matches[m];
            int open = match.Index + match.Length - 1;
            int close = FindMatchingParen(sql, open);
            if (close < 0) continue;

            string? replacement = build(SplitArguments(sql[(open + 1)..close]));
            if (replacement is not null) sql = sql[..match.Index] + replacement + sql[(close + 1)..];
        }

        return sql;
    }

    /// <summary>Splits the inside of CAST(...) at the AS that stands at its own top level.</summary>
    private static (string Value, string Type)? CastParts(string argument)
    {
        int depth = 0, at = -1;

        for (int i = 0; i + 2 < argument.Length; i++)
        {
            char c = argument[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (depth == 0 && c is 'A' or 'a' && argument[i + 1] is 'S' or 's' && char.IsWhiteSpace(argument[i + 2]) &&
                     (i == 0 || !(char.IsLetterOrDigit(argument[i - 1]) || argument[i - 1] == '_')))
                at = i;
        }

        return at < 0 ? null : (argument[..at].TrimEnd(), argument[(at + 2)..].Trim());
    }

    /// <summary>
    /// CAST(x AS varchar(3)) cuts x to three characters in T-SQL; DuckDB accepts the length and
    /// ignores it, which is how CONVERT(varchar(7), d, 120) - the usual way to get "2026-10" -
    /// came back as the whole timestamp. The length is applied with left(). Padding a char(n) out
    /// to its length is deliberately not copied: T-SQL ignores trailing spaces when it compares
    /// strings and DuckDB does not, so padded values would stop matching.
    /// </summary>
    private static string RewriteCharCasts(string sql)
    {
        foreach (string name in new[] { "TRY_CAST", "CAST" })
        {
            sql = RewriteEachCall(sql, name, a =>
            {
                if (a.Count != 1 || CastParts(a[0]) is not { } parts) return null;

                var sized = Regex.Match(parts.Type, @"^n?(?:var)?char\s*\(\s*(?<n>\d+)\s*\)$", RegexOptions.IgnoreCase);
                return sized.Success ? $"left({name}({parts.Value} AS VARCHAR), {sized.Groups["n"].Value})" : null;
            });
        }

        return sql;
    }

    /// <summary>A CAST can still name a T-SQL type, e.g. CAST(x AS nvarchar(50)) or CAST(x AS bit).</summary>
    private static string MapCastTypes(string sql)
    {
        foreach (string name in new[] { "TRY_CAST", "CAST" })
        {
            sql = RewriteEachCall(sql, name, a =>
            {
                if (a.Count != 1 || CastParts(a[0]) is not { } parts) return null;

                string mapped = MapType(parts.Type);
                return mapped == parts.Type ? null : $"{name}({parts.Value} AS {mapped})";
            });
        }

        return sql;
    }

    /// <summary>
    /// LIKE as SQL Server runs it: case-insensitively, as the CI collation DaffyDB reports says, and
    /// with [a-z] and [^0-9] character classes. DuckDB's LIKE does neither - it ignores the default
    /// collation and reads a bracket as a bracket - so LIKE becomes ILIKE, and a literal pattern with
    /// a class becomes the equivalent regular expression under SIMILAR TO. A pattern held in a
    /// parameter cannot be read here, so it gets ILIKE and its brackets stay literal.
    /// </summary>
    private static string RewriteLike(string sql, Func<string, string?> literal, Func<string, string> quote)
    {
        var pattern = new Regex(
            $@"\bLIKE\b(?<rest>\s*(?<pattern>{MaskOpen}\d+{MaskClose})?(?:\s+ESCAPE\s+(?<escape>{MaskOpen}\d+{MaskClose}))?)",
            RegexOptions.IgnoreCase);

        return pattern.Replace(sql, match =>
        {
            string ilike = "ILIKE" + match.Groups["rest"].Value;

            string? text = match.Groups["pattern"].Success ? literal(match.Groups["pattern"].Value) : null;
            if (text is null || !text.Contains('[')) return ilike;

            char? escape = null;
            if (match.Groups["escape"].Success)
            {
                if (literal(match.Groups["escape"].Value) is not { Length: 1 } e) return ilike;
                escape = e[0];
            }

            return "SIMILAR TO " + quote(LikeToRegex(text, escape));
        });
    }

    /// <summary>
    /// A T-SQL LIKE pattern as an RE2 expression matched against the whole value: % is any run,
    /// _ any one character, [...] a class (a lone [%] or [_] being the character itself), and
    /// everything else literal.
    /// </summary>
    private static string LikeToRegex(string pattern, char? escape)
    {
        var regex = new StringBuilder("(?is)");

        static string Literal(char c) => char.IsLetterOrDigit(c) || c == ' ' || c == '_' ? c.ToString() : "\\" + c;

        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];

            if (c == escape && i + 1 < pattern.Length)
            {
                regex.Append(Literal(pattern[++i]));
                continue;
            }

            switch (c)
            {
                case '%': regex.Append(".*"); break;
                case '_': regex.Append('.'); break;

                case '[':
                    int close = pattern.IndexOf(']', i + 1);
                    if (close <= i + 1)
                    {
                        regex.Append(Literal(c));
                        break;
                    }

                    string body = pattern[(i + 1)..close];
                    bool negated = body.Length > 1 && body[0] == '^';
                    if (negated) body = body[1..];

                    regex.Append('[').Append(negated ? "^" : "");
                    foreach (char member in body)
                        regex.Append(member is '\\' or '[' or ']' or '^' ? "\\" + member : member.ToString());
                    regex.Append(']');

                    i = close;
                    break;

                default: regex.Append(Literal(c)); break;
            }
        }

        return regex.ToString();
    }

    /// <summary>
    /// What SQL Server's money type reads, which is what makes ISNUMERIC answer 1 for '$', ',', '.'
    /// and '1,2,3': an optional sign, an optional currency symbol with its own optional sign, then
    /// digits and commas with at most one decimal point, any of which may be missing. Only SQL
    /// Server's currency symbols count, the backslash among them.
    /// </summary>
    private const string MoneyText =
        @"(?:[+-] *)?(?:[$\x{A2}-\x{A5}\x{09F2}\x{09F3}\x{0E3F}\x{17DB}\x{20A0}-\x{20B1}\x{FDFC}\x{FE69}\x{FF04}\x{FFE0}\x{FFE1}\x{FFE5}\x{FFE6}\\] *(?:[+-] *)?)?[0-9,]*(?:\.[0-9,]*)?";

    /// <summary>What float reads beyond that: an exponent, written with e or, as in Fortran, d.</summary>
    private const string FloatText = @"[+-]?(?:[0-9]+\.?[0-9]*|\.[0-9]+)[eEdD][+-]?[0-9]+";

    private static readonly string[] NumericTypes =
    [
        "BOOLEAN", "TINYINT", "SMALLINT", "INTEGER", "BIGINT", "HUGEINT", "UTINYINT", "USMALLINT",
        "UINTEGER", "UBIGINT", "UHUGEINT", "FLOAT", "DOUBLE",
    ];

    /// <summary>
    /// ISNUMERIC: 1 when SQL Server could convert the value to any numeric type, including money,
    /// which takes currency symbols and commas anywhere. Spaces around the text are ignored, but
    /// text that is nothing but spaces is not a number; tabs and line breaks are ignored inside it.
    /// A number is numeric, bit included; a date is not.
    /// </summary>
    private static string IsNumeric(string value, Func<string, string> quote)
    {
        string types = string.Join(", ", NumericTypes.Select(quote));

        return $"list_transform([{value}], lambda __n: CASE WHEN __n IS NULL THEN 0 " +
               $"WHEN typeof(__n) = {quote("VARCHAR")} THEN list_transform([trim(CAST(__n AS VARCHAR), {quote(" ")})], lambda __s: CASE " +
               $"WHEN __s = {quote("")} THEN 0 " +
               $"WHEN regexp_full_match(regexp_replace(__s, {quote(@"[\t\n\r\x{0B}\x{0C}]")}, {quote("")}, {quote("g")}), {quote(MoneyText)}) THEN 1 " +
               $"WHEN regexp_full_match(__s, {quote(FloatText)}) AND isfinite(TRY_CAST(regexp_replace(__s, {quote("[dD]")}, {quote("e")}) AS DOUBLE)) THEN 1 " +
               $"ELSE 0 END)[1] " +
               $"WHEN typeof(__n) IN ({types}) OR typeof(__n) LIKE {quote("DECIMAL%")} THEN 1 " +
               $"ELSE 0 END)[1]";
    }

    /// <summary>A JSON path, without the lax or strict mode SQL Server allows in front of it.</summary>
    private static string JsonPath(string argument, Func<string, string?> literal, Func<string, string> quote) =>
        literal(argument) is { } path
            ? quote(Regex.Replace(path, @"^\s*(?:lax|strict)\s+", "", RegexOptions.IgnoreCase))
            : argument;

    /// <summary>
    /// JSON_VALUE returns a scalar as plain text - x, not "x" - and NULL for an object or array.
    /// DuckDB's json_value keeps the quotes, so json_extract_string is used instead.
    /// </summary>
    private static string JsonValue(List<string> a, Func<string, string?> literal, Func<string, string> quote)
    {
        string path = JsonPath(a[1], literal, quote);
        return $"list_transform([{a[0]}], lambda __j: CASE WHEN json_type(__j, {path}) IN ({quote("OBJECT")}, {quote("ARRAY")}) " +
               $"THEN NULL ELSE json_extract_string(__j, {path}) END)[1]";
    }

    /// <summary>JSON_QUERY returns an object or an array as JSON text, and NULL for a scalar.</summary>
    private static string JsonQuery(List<string> a, Func<string, string?> literal, Func<string, string> quote)
    {
        string path = a.Count == 2 ? JsonPath(a[1], literal, quote) : quote("$");
        return $"list_transform([{a[0]}], lambda __j: CASE WHEN json_type(__j, {path}) IN ({quote("OBJECT")}, {quote("ARRAY")}) " +
               $"THEN CAST(json_extract(__j, {path}) AS VARCHAR) END)[1]";
    }

    /// <summary>
    /// ISJSON is 1 for a valid JSON object or array, or with SQL Server 2022's second argument, for
    /// valid JSON of the type it names; NULL for NULL.
    /// </summary>
    private static string? IsJson(List<string> a, Func<string, string> quote)
    {
        string objectOrArray = $"({quote("OBJECT")}, {quote("ARRAY")})";
        string? accepted = (a.Count == 2 ? a[1].Trim().ToUpperInvariant() : null) switch
        {
            null => $"json_type(__j) IN {objectOrArray}",
            "VALUE" => "true",
            "ARRAY" => $"json_type(__j) = {quote("ARRAY")}",
            "OBJECT" => $"json_type(__j) = {quote("OBJECT")}",
            "SCALAR" => $"json_type(__j) NOT IN {objectOrArray}",
            _ => null,
        };
        if (accepted is null) return null;

        return $"list_transform([{a[0]}], lambda __j: CASE WHEN __j IS NULL THEN NULL " +
               $"WHEN json_valid(__j) THEN CASE WHEN {accepted} THEN 1 ELSE 0 END ELSE 0 END)[1]";
    }

    /// <summary>ROUND(x, d, f) truncates rather than rounds when f is not 0; DuckDB calls that trunc.</summary>
    private static string? RoundWithFunction(List<string> a)
    {
        if (a.Count != 3) return null;

        string function = a[2].Trim();
        if (function == "0") return $"round({a[0]}, {a[1]})";
        if (Regex.IsMatch(function, @"^[+-]?\d+$")) return $"trunc({a[0]}, {a[1]})";
        return $"CASE WHEN ({function}) = 0 THEN round({a[0]}, {a[1]}) ELSE trunc({a[0]}, {a[1]}) END";
    }
}
