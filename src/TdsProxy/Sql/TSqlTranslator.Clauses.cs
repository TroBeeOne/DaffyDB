using System.Text.RegularExpressions;

namespace TdsProxy.Sql;

/// <summary>
/// Rules that reshape a statement rather than a single call: query and table hints, collations,
/// recursive CTEs, APPLY, STRING_SPLIT as a table, and the T-SQL ways of naming a column.
/// </summary>
internal static partial class TSqlTranslator
{
    /// <summary>A plain name, or a masked [bracketed] / "quoted" one.</summary>
    private static readonly string NamePart = $@"(?:[A-Za-z_][\w$#@]*|{MaskOpen}\d+{MaskClose})";

    private static readonly Regex MaskToken = new($@"^{MaskOpen}\d+{MaskClose}$");

    /// <summary>
    /// DuckDB's keywords, which DuckDB will not take as a column alias unless AS comes first. Set
    /// once the engine is up, since the list is DuckDB's own and changes between versions.
    /// </summary>
    private static HashSet<string> _duckDbKeywords = new(StringComparer.OrdinalIgnoreCase);

    public static void SetKeywords(IEnumerable<string> keywords) =>
        _duckDbKeywords = new HashSet<string>(keywords, StringComparer.OrdinalIgnoreCase);

    /// <summary>How deep in parentheses <paramref name="position"/> sits.</summary>
    private static int Depth(string sql, int position)
    {
        int depth = 0;
        for (int i = 0; i < position; i++)
        {
            if (sql[i] == '(') depth++;
            else if (sql[i] == ')') depth--;
        }
        return depth;
    }

    private static int SkipSpace(string sql, int position)
    {
        while (position < sql.Length && char.IsWhiteSpace(sql[position])) position++;
        return position;
    }

    /// <summary>
    /// OPTION (RECOMPILE), OPTION (MAXDOP 2) and the other query hints that end a statement.
    ///
    /// DuckDB has no such clause, and what it makes of one is worse than a syntax error: after a
    /// table name it reads OPTION (RECOMPILE) as an alias for the table with a column list, so
    /// SELECT * FROM t OPTION (RECOMPILE) runs and renames the first column to RECOMPILE. The hints
    /// only steer SQL Server's plan, so dropping them changes no answer. MAXRECURSION goes too,
    /// which is why a cancel has to reach DuckDB: nothing else stops a runaway recursive CTE.
    /// </summary>
    private static string StripQueryOptions(string sql, Func<string, bool> isComment)
    {
        foreach (Match match in Regex.Matches(sql, @"\bOPTION\s*\(", RegexOptions.IgnoreCase).Reverse())
        {
            if (Depth(sql, match.Index) != 0) continue;

            int close = FindMatchingParen(sql, match.Index + match.Length - 1);
            if (close < 0) continue;

            // Only a clause that really ends the statement; whatever follows may be a semicolon,
            // whitespace or a comment, and nothing else.
            string rest = Regex.Replace(sql[(close + 1)..], $@"{MaskOpen}\d+{MaskClose}",
                                        token => isComment(token.Value) ? "" : token.Value);
            if (rest.Trim().Trim(';').Trim().Length > 0) continue;

            sql = sql[..match.Index] + sql[(close + 1)..];
        }

        return sql;
    }

    /// <summary>
    /// SQL Server's collation names. The connection already compares case-insensitively, as the
    /// SQL_Latin1_General_CP1_CI_AS it reports does, so a case-insensitive collation is simply
    /// dropped and an accent-insensitive one adds noaccent. A case-sensitive or binary collation
    /// becomes DuckDB's binary comparison. Anything else is left for DuckDB to reject by name.
    /// </summary>
    private static string RewriteCollations(string sql, Func<string, string?> identifier) =>
        Regex.Replace(sql, $@"\bCOLLATE\s+(?<name>{NamePart})", match =>
        {
            string name = (identifier(match.Groups["name"].Value) ?? "").ToUpperInvariant();

            if (name is "DATABASE_DEFAULT" or "CATALOG_DEFAULT") return "";
            if (Regex.IsMatch(name, @"_BIN2?$|_CS(_|$)")) return "COLLATE \"binary\"";
            if (Regex.IsMatch(name, @"_CI(_|$)"))
                return Regex.IsMatch(name, @"_AI(_|$)") ? "COLLATE nocase.noaccent" : "";

            return match.Value;
        }, RegexOptions.IgnoreCase);

    /// <summary>
    /// T-SQL makes a CTE recursive by having it name itself; DuckDB wants WITH RECURSIVE said out
    /// loud. Adding it is harmless to the CTEs in the list that do not recurse, so it is added
    /// whenever any of them mentions its own name.
    /// </summary>
    private static string AddRecursive(string sql, Func<string, string?> identifier)
    {
        var header = new Regex($@"\bWITH\s+(?!RECURSIVE\b)(?={NamePart}\s*(?:\([^()]*\)\s*)?AS\s*\()",
                               RegexOptions.IgnoreCase);
        var definition = new Regex($@"\G\s*(?<name>{NamePart})\s*(?:\([^()]*\)\s*)?AS\s*\(", RegexOptions.IgnoreCase);
        var separator = new Regex(@"\G\s*,");

        foreach (Match match in header.Matches(sql).Reverse())
        {
            int position = match.Index + match.Length;
            bool recursive = false;

            while (definition.Match(sql, position) is { Success: true } cte)
            {
                int open = cte.Index + cte.Length - 1;
                int close = FindMatchingParen(sql, open);
                if (close < 0) break;

                string? name = identifier(cte.Groups["name"].Value);
                if (name is not null && NamesItself(sql[(open + 1)..close], name, identifier)) recursive = true;

                if (separator.Match(sql, close + 1) is not { Success: true } comma) break;
                position = comma.Index + comma.Length;
            }

            if (recursive) sql = sql.Insert(match.Index + match.Length, "RECURSIVE ");
        }

        return sql;
    }

    /// <summary>True when <paramref name="body"/> names <paramref name="name"/> other than as part of a dotted name.</summary>
    private static bool NamesItself(string body, string name, Func<string, string?> identifier) =>
        Regex.Matches(body, NamePart).Any(token =>
            (token.Index == 0 || body[token.Index - 1] != '.') &&
            string.Equals(identifier(token.Value), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// STRING_SPLIT(text, separator) is a table in T-SQL and a list in DuckDB. Where it stands as a
    /// table - after FROM, JOIN or APPLY - it becomes a subquery unnesting that list into a column
    /// named value, plus ordinal when the third argument asks for one.
    /// </summary>
    private static string RewriteStringSplit(string sql)
    {
        var pattern = new Regex(@"\b(?:FROM|JOIN|APPLY)(?<gap>\s+)STRING_SPLIT\s*\(", RegexOptions.IgnoreCase);

        foreach (Match match in pattern.Matches(sql).Reverse())
        {
            int open = match.Index + match.Length - 1;
            int close = FindMatchingParen(sql, open);
            if (close < 0) continue;

            var a = SplitArguments(sql[(open + 1)..close]);
            if (a.Count is < 2 or > 3) continue;

            string table = a.Count == 3 && a[2].Trim() != "0"
                ? $"(SELECT unnest(__parts) AS value, generate_subscripts(__parts, 1) AS ordinal " +
                  $"FROM (SELECT string_split({a[0]}, {a[1]}) AS __parts))"
                : $"(SELECT unnest(string_split({a[0]}, {a[1]})) AS value)";

            int start = match.Groups["gap"].Index + match.Groups["gap"].Length;
            sql = sql[..start] + table + sql[(close + 1)..];
        }

        return sql;
    }

    /// <summary>
    /// CROSS APPLY is a lateral join. OUTER APPLY is a left lateral join, which DuckDB will only
    /// take with an ON clause, so ON TRUE goes in after whatever is applied: a parenthesised
    /// subquery or a function call, then its alias and any column list. A shape that cannot be
    /// followed to its end is left alone and DuckDB reports it.
    /// </summary>
    private static string RewriteApply(string sql, List<string> notes)
    {
        sql = Regex.Replace(sql, @"\bCROSS\s+APPLY\b", "CROSS JOIN LATERAL", RegexOptions.IgnoreCase);

        foreach (Match match in Regex.Matches(sql, @"\bOUTER\s+APPLY\b", RegexOptions.IgnoreCase).Reverse())
        {
            int end = EndOfApplied(sql, match.Index + match.Length);
            if (end < 0)
            {
                notes.Add("an OUTER APPLY was left as-is: what it applies could not be followed to its end");
                continue;
            }

            sql = sql[..end] + " ON TRUE" + sql[end..];
            sql = sql[..match.Index] + "LEFT JOIN LATERAL" + sql[(match.Index + match.Length)..];
        }

        return sql;
    }

    /// <summary>Words that end a FROM item rather than alias it.</summary>
    private static readonly HashSet<string> ClauseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "GROUP", "HAVING", "ORDER", "UNION", "EXCEPT", "INTERSECT", "JOIN", "INNER", "LEFT",
        "RIGHT", "FULL", "CROSS", "OUTER", "ON", "WINDOW", "QUALIFY", "LIMIT", "OFFSET", "OPTION", "FOR",
        "INTO", "WITH", "NATURAL", "LATERAL", "USING", "SELECT", "FROM", "APPLY", "PIVOT", "UNPIVOT",
    };

    private static int EndOfApplied(string sql, int position)
    {
        int i = SkipSpace(sql, position);
        if (i >= sql.Length) return -1;

        if (sql[i] == '(')
        {
            int close = FindMatchingParen(sql, i);
            if (close < 0) return -1;
            i = close + 1;
        }
        else
        {
            var name = Regex.Match(sql[i..], $@"^{NamePart}(?:\s*\.\s*{NamePart})*");
            if (!name.Success) return -1;
            i += name.Length;

            int j = SkipSpace(sql, i);
            if (j < sql.Length && sql[j] == '(')
            {
                int close = FindMatchingParen(sql, j);
                if (close < 0) return -1;
                i = close + 1;
            }
        }

        int k = SkipSpace(sql, i);
        var alias = Regex.Match(sql[k..], $@"^(?<as>AS\s+)?(?<name>{NamePart})", RegexOptions.IgnoreCase);
        if (alias.Success && (alias.Groups["as"].Success || !ClauseWords.Contains(alias.Groups["name"].Value)))
        {
            i = k + alias.Length;

            int m = SkipSpace(sql, i);
            if (m < sql.Length && sql[m] == '(')
            {
                int close = FindMatchingParen(sql, m);
                if (close >= 0) i = close + 1;
            }
        }

        return i;
    }

    /// <summary>
    /// Words that end a select list. GROUP and ORDER only do when BY follows, since WITHIN GROUP
    /// (ORDER BY ...) belongs to an aggregate in the list.
    /// </summary>
    private static readonly HashSet<string> SelectListEnders = new(StringComparer.OrdinalIgnoreCase)
    {
        "FROM", "INTO", "WHERE", "HAVING", "UNION", "EXCEPT", "INTERSECT", "WINDOW",
        "QUALIFY", "LIMIT", "OFFSET", "OPTION", "FOR",
    };

    private static readonly HashSet<string> SelectListEndersBeforeBy = new(StringComparer.OrdinalIgnoreCase) { "GROUP", "ORDER" };

    /// <summary>
    /// Keywords after which the next word is still part of the expression, not its alias.
    /// </summary>
    private static readonly HashSet<string> ExpressionContinues = new(StringComparer.OrdinalIgnoreCase)
    {
        "AND", "OR", "NOT", "IS", "AS", "THEN", "ELSE", "WHEN", "CASE", "DISTINCT", "ALL", "BY", "ON",
        "LIKE", "ILIKE", "IN", "BETWEEN", "COLLATE", "ESCAPE", "AT", "ZONE", "INTERVAL", "TOP", "SELECT",
        "NEXT", "SIMILAR", "TO", "EXISTS", "ANY", "SOME", "OVER", "WITHIN", "FILTER",
    };

    /// <summary>Keywords that end an expression, so are never an alias even though DuckDB reserves them.</summary>
    private static readonly HashSet<string> NeverAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "END", "NULL", "TRUE", "FALSE",
    };

    /// <summary>
    /// The two T-SQL ways of naming a column that DuckDB reads differently.
    ///
    /// "SELECT Total = SUM(x)" names a column in T-SQL, where a select list cannot hold a
    /// comparison at all. DuckDB reads it as one, and so either fails to find a column called
    /// Total or, when there is one, quietly returns true or false. It becomes SUM(x) AS Total.
    ///
    /// "SELECT YEAR(d) Year" is an alias without AS, which DuckDB refuses when the alias is one of
    /// its keywords - Year, Name, Value, Type and many more that reports use as column names. The AS
    /// is put in.
    ///
    /// Each SELECT's list runs from after its DISTINCT or TOP to the first FROM, INTO, WHERE and so
    /// on at its own depth, and is split at its own commas; subqueries are handled as SELECTs of
    /// their own. Assignments to @variables never reach here, and $1 is not a name.
    /// </summary>
    private static string RewriteSelectLists(string sql, Func<string, string?> literal, Func<string, string> quoteIdentifier)
    {
        foreach (Match select in Regex.Matches(sql, @"\bSELECT\b", RegexOptions.IgnoreCase).Reverse())
        {
            int start = select.Index + select.Length;
            var prefix = Regex.Match(sql[start..],
                @"^\s*(?:(?:ALL|DISTINCT)\b\s*)?(?:TOP\s*(?:\([^()]*\)|\S+)(?:\s+PERCENT\b)?(?:\s+WITH\s+TIES\b)?)?",
                RegexOptions.IgnoreCase);
            start += prefix.Length;

            int end = EndOfSelectList(sql, start);

            var items = new List<(int Start, int End)>();
            int depth = 0, itemStart = start;
            for (int i = start; i < end; i++)
            {
                if (sql[i] == '(') depth++;
                else if (sql[i] == ')') depth--;
                else if (sql[i] == ',' && depth == 0)
                {
                    items.Add((itemStart, i));
                    itemStart = i + 1;
                }
            }
            items.Add((itemStart, end));

            for (int index = items.Count - 1; index >= 0; index--)
            {
                var (itemFrom, itemTo) = items[index];
                string? rewritten = RewriteSelectItem(sql[itemFrom..itemTo], literal, quoteIdentifier);
                if (rewritten is not null) sql = sql[..itemFrom] + rewritten + sql[itemTo..];
            }
        }

        return sql;
    }

    private static int EndOfSelectList(string sql, int start)
    {
        int depth = 0;

        for (int i = start; i < sql.Length; i++)
        {
            char c = sql[i];
            if (c == '(') { depth++; continue; }
            if (c == ')') { if (depth-- == 0) return i; continue; }
            if (depth > 0) continue;
            if (c == ';') return i;

            if ((char.IsLetter(c) || c == '_') && (i == 0 || !(char.IsLetterOrDigit(sql[i - 1]) || sql[i - 1] == '_')))
            {
                int wordEnd = i;
                while (wordEnd < sql.Length && (char.IsLetterOrDigit(sql[wordEnd]) || sql[wordEnd] == '_')) wordEnd++;
                string word = sql[i..wordEnd];
                if (SelectListEnders.Contains(word)) return i;
                if (SelectListEndersBeforeBy.Contains(word) &&
                    Regex.IsMatch(sql[wordEnd..], @"^\s+BY\b", RegexOptions.IgnoreCase)) return i;
                i = wordEnd - 1;
            }
        }

        return sql.Length;
    }

    private static string? RewriteSelectItem(string item, Func<string, string?> literal, Func<string, string> quoteIdentifier)
    {
        string core = item.Trim();
        if (core.Length == 0) return null;

        string lead = item[..item.IndexOf(core, StringComparison.Ordinal)];
        string trail = item[(lead.Length + core.Length)..];

        // Alias = expression. The alias may be a plain name, a bracketed or quoted one, or a string.
        var assigned = Regex.Match(core, $@"^(?<alias>[A-Za-z_][\w$#@]*|{MaskOpen}\d+{MaskClose})\s*=(?!=)\s*(?<expr>.+)$",
                                   RegexOptions.Singleline);
        if (assigned.Success)
        {
            string alias = assigned.Groups["alias"].Value;
            if (MaskToken.IsMatch(alias) && literal(alias) is { } text) alias = quoteIdentifier(text);
            return $"{lead}{assigned.Groups["expr"].Value.TrimEnd()} AS {alias}{trail}";
        }

        // Expression, whitespace, and an alias DuckDB only accepts after AS.
        var bare = Regex.Match(core, @"^(?<expr>.*\S)\s+(?<alias>[A-Za-z_]\w*)$", RegexOptions.Singleline);
        if (!bare.Success) return null;

        string word = bare.Groups["alias"].Value;
        string expr = bare.Groups["expr"].Value;
        if (!_duckDbKeywords.Contains(word) || NeverAliases.Contains(word)) return null;
        if ("+-*/%=<>,(|&^~!.:".Contains(expr[^1])) return null;
        if (Regex.Match(expr, @"([A-Za-z_]\w*)$") is { Success: true } last && ExpressionContinues.Contains(last.Value)) return null;

        return $"{lead}{expr} AS {word}{trail}";
    }

    /// <summary>
    /// STRING_AGG(x, sep) WITHIN GROUP (ORDER BY y) is T-SQL's ordered form; DuckDB puts the ORDER
    /// BY inside the call: string_agg(x, sep ORDER BY y).
    /// </summary>
    private static string RewriteWithinGroup(string sql)
    {
        foreach (Match match in Regex.Matches(sql, @"\bSTRING_AGG\s*\(", RegexOptions.IgnoreCase).Reverse())
        {
            int close = FindMatchingParen(sql, match.Index + match.Length - 1);
            if (close < 0) continue;

            var within = Regex.Match(sql[(close + 1)..], @"^\s*WITHIN\s+GROUP\s*\(", RegexOptions.IgnoreCase);
            if (!within.Success) continue;

            int groupOpen = close + within.Length;
            int groupClose = FindMatchingParen(sql, groupOpen);
            if (groupClose < 0) continue;

            string order = sql[(groupOpen + 1)..groupClose].Trim();
            if (!Regex.IsMatch(order, @"^ORDER\s+BY\b", RegexOptions.IgnoreCase)) continue;

            sql = sql[..close] + " " + order + ")" + sql[(groupClose + 1)..];
        }

        return sql;
    }

    private static readonly string[] TableHints =
    [
        "NOLOCK", "READUNCOMMITTED", "READCOMMITTED", "REPEATABLEREAD", "SERIALIZABLE",
        "ROWLOCK", "PAGLOCK", "TABLOCK", "TABLOCKX", "UPDLOCK", "XLOCK", "HOLDLOCK",
        "NOEXPAND", "FORCESEEK", "FORCESCAN", "READPAST", "NOWAIT", "SNAPSHOT", "READCOMMITTEDLOCK",
        "KEEPIDENTITY", "KEEPDEFAULTS", "IGNORE_CONSTRAINTS", "IGNORE_TRIGGERS",
    ];

    private static readonly Regex HintWithArguments = new(
        @"^(?:(?:INDEX|FORCESEEK)\s*\(.*\)|INDEX\s*=\s*\S+|SPATIAL_WINDOW_MAX_CELLS\s*=\s*\d+)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>
    /// Drops WITH (NOLOCK), WITH (INDEX(1)) and friends, which only steer SQL Server's locking and
    /// plans. Only removed when every item inside the parentheses is a hint, so a common table
    /// expression is never mistaken for one.
    /// </summary>
    private static string StripTableHints(string sql)
    {
        foreach (Match match in Regex.Matches(sql, @"\bWITH\s*\(", RegexOptions.IgnoreCase).Reverse())
        {
            int close = FindMatchingParen(sql, match.Index + match.Length - 1);
            if (close < 0) continue;

            var items = SplitArguments(sql[(match.Index + match.Length)..close]);
            bool allHints = items.Count > 0 && items.All(item =>
                TableHints.Contains(item.ToUpperInvariant()) || HintWithArguments.IsMatch(item));
            if (!allHints) continue;

            sql = sql[..match.Index] + " " + sql[(close + 1)..];
        }

        return sql;
    }
}
