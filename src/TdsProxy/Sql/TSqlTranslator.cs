using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace TdsProxy.Sql;

/// <summary>
/// The result of translating one T-SQL statement. <paramref name="ParameterOrder"/> lists the
/// original parameter names in the order DuckDB's positional placeholders ($1, $2, ...) expect
/// them, which is how RPC parameter values get bound.
/// </summary>
internal sealed record TranslatedSql(string Sql, IReadOnlyList<string> ParameterOrder, IReadOnlyList<string> Notes);

/// <summary>
/// Rewrites the T-SQL dialect into what DuckDB accepts.
///
/// Deliberately a rewriter, not a parser: it handles a documented list of constructs and leaves
/// everything else alone, so anything unsupported reaches DuckDB and comes back as a real error
/// message rather than being silently mistranslated.
///
/// All rewriting happens on a masked form of the statement in which every string literal, comment
/// and quoted identifier is a single opaque token. That keeps literals safe from the regexes and,
/// just as importantly, lets the function-argument splitter ignore commas and parentheses that
/// appear inside literals.
/// </summary>
internal static partial class TSqlTranslator
{
    // Private-use code points, so a mask token can never collide with anything in real SQL.
    private const char MaskOpen = '';
    private const char MaskClose = '';

    /// <summary>What the proxy reports for SERVERPROPERTY and the @@ globals.</summary>
    public const string ProductVersion = "17.0.4025.3";
    public const string ServerName = "DAFFY_PROXY_DUCKDB";

    /// <summary>
    /// The name DaffyDB gives itself where SQL Server would name its product: @@VERSION, xp_msver
    /// and sp_server_info. The version numbers beside it stay SQL Server's, because those are what
    /// clients decide features from. The login acknowledgement is the exception; see
    /// TdsSession.LoginAckProductName for why.
    /// </summary>
    public const string ProductName = "DaffyDB";

    /// <summary>
    /// What @@VERSION answers. The first line keeps SQL Server's layout - name, then " - ", the
    /// four-part version and the platform - because clients take the version number apart from it;
    /// the second says what is really answering, for whoever reads it.
    /// </summary>
    public static string VersionBanner => $"{VersionLine} (X64) \n\t{Identity}";

    /// <summary>The first line of @@VERSION without the platform, which is how sp_server_info's DBMS_VER reads.</summary>
    public static string VersionLine => $"{ProductName} - {ProductVersion}";

    /// <summary>DaffyDB and the DuckDB build underneath it - and, lest anyone wonder, not SQL Server.</summary>
    public static string Identity { get; private set; } = Describe(null);

    /// <summary>Called once the engine is up, since only it knows which DuckDB build it is.</summary>
    public static void SetEngineVersion(string duckDbVersion) => Identity = Describe(duckDbVersion);

    private static string Describe(string? duckDbVersion) =>
        $"DaffyDB {StartupBanner.ToolVersion()} over DuckDB" +
        (duckDbVersion is null ? "" : " " + duckDbVersion.TrimStart('v')) +
        ". This is not SQL Server.";

    /// <summary>
    /// Rewrites one statement. <paramref name="error"/> is the error a CATCH block is currently
    /// handling, which is what the ERROR_* functions report; outside a CATCH it is null and they
    /// report NULL, exactly as T-SQL does.
    /// </summary>
    public static TranslatedSql Translate(string sql, SqlErrorInfo? error = null, SessionContext? session = null)
    {
        session ??= SessionContext.Default;

        var notes = new List<string>();
        var segments = SqlSegments.Split(sql);
        string masked = Mask(segments, out var replacements);

        // Resolves a masked argument back to its string-literal text, for the handful of rules that
        // need to read a literal (SERVERPROPERTY('Edition'), DATENAME(month, ...) and friends).
        string? Literal(string argument)
        {
            var match = Regex.Match(argument.Trim(), $"^{MaskOpen}(\\d+){MaskClose}$");
            if (!match.Success) return null;

            var segment = replacements[int.Parse(match.Groups[1].Value)];
            return segment.Kind == SegmentKind.StringLiteral
                ? segment.Text[1..^1].Replace("''", "'")
                : null;
        }

        string Quote(string text) => Mask(new SqlSegment(SegmentKind.StringLiteral, "'" + text.Replace("'", "''") + "'"), replacements);
        string QuoteIdentifier(string name) =>
            Mask(new SqlSegment(SegmentKind.QuotedIdentifier, "\"" + name.Replace("\"", "\"\"") + "\""), replacements);

        bool IsComment(string token)
        {
            var match = Regex.Match(token, $"^{MaskOpen}(\\d+){MaskClose}$");
            return match.Success && replacements[int.Parse(match.Groups[1].Value)].Kind == SegmentKind.Comment;
        }

        // The name a part of a dotted name spells, whether written plainly or delimited.
        string? Identifier(string part)
        {
            var match = Regex.Match(part, $"^{MaskOpen}(\\d+){MaskClose}$");
            if (!match.Success) return part;

            var segment = replacements[int.Parse(match.Groups[1].Value)];
            return segment.Kind switch
            {
                SegmentKind.BracketIdentifier => SqlSegments.IdentifierName(segment.Text, '[', ']'),
                SegmentKind.QuotedIdentifier => SqlSegments.IdentifierName(segment.Text, '"', '"'),
                _ => null,
            };
        }

        masked = RewriteInformationSchema(masked, Identifier);
        masked = RewriteLegacyViews(masked);
        masked = RewriteTempTables(masked);
        masked = AddMissingInsertInto(masked);
        masked = RewriteCreateTableTypes(masked);
        masked = RewriteParameters(masked, out var parameterOrder);
        masked = RewriteGlobals(masked, Quote);
        masked = RewriteHexLiterals(masked);
        masked = StripTableHints(masked);
        masked = StripQueryOptions(masked, IsComment);
        masked = RewriteCollations(masked, Identifier);
        masked = AddRecursive(masked, Identifier);
        masked = RewriteOffsetFetch(masked);
        masked = RewriteStringSplit(masked);
        masked = RewriteApply(masked, notes);
        masked = RewriteTop(masked, out string? limit, notes);
        masked = RewriteWithinGroup(masked);
        masked = RewriteSelectLists(masked, Literal, QuoteIdentifier);
        masked = RewriteStringConcat(masked, token => Literal(token) is not null);
        masked = UnifyValuesColumns(masked, token => Literal(token) is not null);
        masked = RewriteLike(masked, Literal, Quote);

        // CONVERT and CAST before the simple renames, which would otherwise turn the type in
        // CAST(x AS char(10)) into a call to chr().
        masked = RewriteEachCall(masked, "TRY_CONVERT", a => Convert(a, isTry: true, Literal, Quote));
        masked = RewriteEachCall(masked, "CONVERT", a => Convert(a, isTry: false, Literal, Quote));
        masked = RewriteCharCasts(masked);
        masked = RewriteSimpleNames(masked);

        // Calls whose arguments have to be rearranged rather than just renamed.
        masked = RewriteCalls(masked, "LEN", a => a.Count == 1 ? $"length(rtrim({a[0]}))" : null);
        // strlen is DuckDB's byte length; length() counts characters and octet_length only takes a
        // BLOB. The cast pins down an untyped string literal.
        masked = RewriteCalls(masked, "DATALENGTH", a => a.Count == 1
            ? $"strlen(CAST({a[0]} AS VARCHAR))"
            : null);
        masked = RewriteCalls(masked, "SPACE", a => a.Count == 1 ? $"repeat(' ', {a[0]})" : null);
        masked = RewriteCalls(masked, "SQUARE", a => a.Count == 1 ? $"power({a[0]}, 2)" : null);

        // T-SQL's FLOOR and CEILING return the type they are given: FLOOR of an int is an int.
        // DuckDB's always return a double for an integer, so a client reading Power BI's column
        // query - floor(max_length / 2) - was handed a float where SQL Server sends an int. trunc
        // does keep the type, and floor and ceiling are trunc, moved by one where it went the wrong
        // way. The lambda is there so the argument is evaluated once: FLOOR(RAND() * 10) must see
        // one random number, not three.
        masked = RewriteCalls(masked, "FLOOR", a => a.Count == 1
            ? $"list_transform([{a[0]}], lambda __v: trunc(__v) - CASE WHEN __v < trunc(__v) THEN 1 ELSE 0 END)[1]"
            : null);
        masked = RewriteCalls(masked, "CEILING", a => a.Count == 1
            ? $"list_transform([{a[0]}], lambda __v: trunc(__v) + CASE WHEN __v > trunc(__v) THEN 1 ELSE 0 END)[1]"
            : null);
        masked = RewriteCalls(masked, "STR", a => a.Count >= 1 ? $"CAST({a[0]} AS VARCHAR)" : null);
        masked = RewriteCalls(masked, "CHARINDEX", a => a.Count switch
        {
            2 => $"strpos({a[1]}, {a[0]})",
            // The 3-argument form starts the search at a position, which strpos cannot express, so
            // search the tail and shift the result back.
            3 => $"CASE WHEN strpos(substring({a[1]}, {a[2]}), {a[0]}) = 0 THEN 0 " +
                 $"ELSE strpos(substring({a[1]}, {a[2]}), {a[0]}) + {a[2]} - 1 END",
            _ => null,
        });
        masked = RewriteCalls(masked, "STUFF", a => a.Count == 4
            ? $"(substring({a[0]}, 1, ({a[1]}) - 1) || {a[3]} || substring({a[0]}, ({a[1]}) + ({a[2]})))"
            : null);
        masked = RewriteCalls(masked, "IIF", a => a.Count == 3
            ? $"CASE WHEN {a[0]} THEN {a[1]} ELSE {a[2]} END"
            : null);
        masked = RewriteCalls(masked, "CHOOSE", a => a.Count >= 2
            ? "CASE " + string.Join(" ", a.Skip(1).Select((v, i) => $"WHEN ({a[0]}) = {i + 1} THEN {v}")) + " END"
            : null);
        masked = RewriteEachCall(masked, "ROUND", RoundWithFunction);
        masked = RewriteEachCall(masked, "JSON_VALUE", a => a.Count == 2 ? JsonValue(a, Literal, Quote) : null);
        masked = RewriteEachCall(masked, "JSON_QUERY", a => a.Count is 1 or 2 ? JsonQuery(a, Literal, Quote) : null);
        masked = RewriteEachCall(masked, "ISJSON", a => a.Count is 1 or 2 ? IsJson(a, Quote) : null);
        masked = RewriteEachCall(masked, "ISNUMERIC", a => a.Count == 1 ? IsNumeric(a[0], Quote) : null);
        masked = RewriteEachCall(masked, "ISDATE", a => a.Count == 1 ? IsDate(a[0], Quote) : null);
        masked = RewriteEachCall(masked, "FORMAT", a => Format(a, Literal, Quote));

        // SERVERPROPERTY is typed per property: EngineEdition and friends come back as integers and
        // callers cast them straight to an enum, so quoting them breaks the caller.
        masked = RewriteCalls(masked, "SERVERPROPERTY", a =>
        {
            if (a.Count != 1) return null;
            string property = Literal(a[0]) ?? "";
            string value = ServerProperty(property);

            // A property with nothing to report is NULL, as in SQL Server. An empty string is not
            // the same answer: SMO casts several of these to bit, and CAST('' AS BOOLEAN) fails.
            if (value.Length == 0) return "NULL";

            return NumericServerProperties.Contains(property) ? value : Quote(value);
        });
        masked = RewriteCalls(masked, "FULLTEXTSERVICEPROPERTY", a => a.Count == 1 ? "0" : null);

        // QUOTENAME wraps an identifier in a delimiter, doubling any occurrence of it inside.
        masked = RewriteCalls(masked, "QUOTENAME", a => a.Count switch
        {
            1 => Wrap(a[0], "[", "]", "]", "]]", Quote),
            2 => (Literal(a[1]) ?? "[") switch
            {
                "'" => Wrap(a[0], "'", "'", "'", "''", Quote),
                "\"" => Wrap(a[0], "\"", "\"", "\"", "\"\"", Quote),
                _ => Wrap(a[0], "[", "]", "]", "]]", Quote),
            },
            _ => null,
        });
        masked = RewriteCalls(masked, "CONNECTIONPROPERTY", a => a.Count == 1
            ? Quote(ConnectionProperty(Literal(a[0]) ?? ""))
            : null);

        // With no argument these are the caller's own database; given one, they look it up, which is
        // how a client turns the database_id in a catalog row back into a name.
        masked = RewriteCalls(masked, "DB_NAME", a => a.All(string.IsNullOrWhiteSpace)
            ? Quote(session.Database)
            : $"(SELECT name FROM master.sys.databases WHERE database_id = {a[0]})");
        masked = RewriteCalls(masked, "DB_ID", a => a.All(string.IsNullOrWhiteSpace)
            ? Databases.IdOf(session.Database).ToString(CultureInfo.InvariantCulture)
            : $"(SELECT database_id FROM master.sys.databases WHERE name = {a[0]})");

        // Identity and context functions. Clients call these while connecting, long before they run
        // anything the user typed.
        foreach (var (function, value) in ContextFunctions(session))
            masked = RewriteCalls(masked, function, _ => Quote(value));

        foreach (var (function, value) in ConstantFunctions)
            masked = RewriteCalls(masked, function, _ => value);

        // The ERROR_* family reads the error the enclosing CATCH block is handling. Outside one
        // there is no error, and T-SQL answers NULL rather than failing.
        masked = RewriteCalls(masked, "ERROR_NUMBER", _ => Number(error?.Number));
        masked = RewriteCalls(masked, "ERROR_SEVERITY", _ => Number(error?.Severity));
        masked = RewriteCalls(masked, "ERROR_STATE", _ => Number(error?.State));
        masked = RewriteCalls(masked, "ERROR_LINE", _ => Number(error?.Line));
        masked = RewriteCalls(masked, "ERROR_MESSAGE", _ => error is null ? "NULL" : Quote(error.Message));
        masked = RewriteCalls(masked, "ERROR_PROCEDURE", _ =>
            error?.Procedure is null ? "NULL" : Quote(error.Procedure));

        // Catalog lookups. OBJECT_ID answers NULL for something that does not exist, which is how a
        // client asks "does this table exist". The name may be qualified and bracketed, and its
        // schema counts: dbo.orders and sales.orders are two tables. A name without one is dbo's,
        // the caller's default schema, as SQL Server resolves it.
        masked = RewriteCalls(masked, "OBJECT_ID", a => a.Count >= 1
            ? $"(SELECT o.object_id FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id " +
              $"WHERE o.name = {DottedPart(a[0], 1)} AND s.name = coalesce(nullif({DottedPart(a[0], 2)}, ''), 'dbo'))"
            : null);
        masked = RewriteCalls(masked, "OBJECT_NAME", a => a.Count >= 1
            ? $"(SELECT name FROM sys.objects WHERE object_id = {a[0]})"
            : null);
        masked = RewriteCalls(masked, "OBJECTPROPERTY", a => a.Count == 2
            ? ObjectProperty(a[0], Literal(a[1]) ?? "", Quote)
            : null);
        // SCHEMA_NAME and SCHEMA_ID are a pair, and both have to be real lookups: a client reads a
        // schema back with SCHEMA_NAME(o.schema_id), so answering with a constant puts every object
        // in the tree under dbo no matter which schema it was mounted into. With no argument they
        // mean the caller's own default schema, which here is dbo.
        masked = RewriteCalls(masked, "SCHEMA_NAME", a => a.Count == 1
            ? $"(SELECT name FROM sys.schemas WHERE schema_id = {a[0]})"
            : Quote("dbo"));
        masked = RewriteCalls(masked, "SCHEMA_ID", a => a.Count == 1
            ? $"(SELECT schema_id FROM sys.schemas WHERE name = {a[0]})"
            : "(SELECT schema_id FROM sys.schemas WHERE name = 'dbo')");
        // When a database was last checked with DBCC CHECKDB: never, which SQL Server reports as
        // 1900-01-01. SMO asks it as ISNULL(DATABASEPROPERTYEX(name, 'LastGoodCheckDbTime'), 0) and
        // casts that to datetime, where T-SQL turns the 0 into 1900-01-01 too. DuckDB will neither
        // mix a timestamp with 0 in COALESCE nor cast 0 to a timestamp, so the ISNULL - a COALESCE
        // by now - is answered whole, before the property alone is. The property is never NULL, so
        // the fallback is never what the COALESCE answers anyway.
        string NeverChecked() => $"CAST({Quote("1900-01-01 00:00:00")} AS TIMESTAMP)";
        bool IsLastGoodCheck(string argument)
        {
            var call = Regex.Match(argument.Trim(), @"^DATABASEPROPERTYEX\s*\((?<args>.*)\)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!call.Success) return false;
            var args = SplitArguments(call.Groups["args"].Value);
            return args.Count == 2 && string.Equals(Literal(args[1]), "LastGoodCheckDbTime", StringComparison.OrdinalIgnoreCase);
        }
        masked = RewriteEachCall(masked, "COALESCE", a => a.Count >= 2 && IsLastGoodCheck(a[0]) ? NeverChecked() : null);

        // A property with nothing to report is NULL, as in SQL Server and as SERVERPROPERTY answers.
        // It used to be '', which SMO's ISNULL(DATABASEPROPERTYEX(name, 'IsXTPSupported'), 0) cannot
        // turn into the 0 it falls back to.
        masked = RewriteCalls(masked, "DATABASEPROPERTYEX", a =>
        {
            if (a.Count != 2) return null;
            string property = Literal(a[1]) ?? "";
            if (property.Equals("LastGoodCheckDbTime", StringComparison.OrdinalIgnoreCase)) return NeverChecked();

            string value = DatabaseProperty(property);
            return value.Length == 0 ? "NULL" : Quote(value);
        });

        // The session's SET options, 1 for on. DaffyDB accepts SET and ignores it, so these are the
        // options DuckDB always behaves as: NULL = NULL is unknown, as with ANSI_NULLS on, and a
        // "double-quoted" name is an identifier. VS Code asks for these two while connecting. Any
        // other is NULL, rather than an answer DaffyDB could not stand behind.
        masked = RewriteCalls(masked, "SESSIONPROPERTY", a => a.Count == 1
            ? (Literal(a[0]) ?? "").ToUpperInvariant() switch
            {
                "ANSI_NULLS" or "QUOTED_IDENTIFIER" => "1",
                _ => "NULL",
            }
            : null);

        // Nothing here sets CONTEXT_INFO, so it is NULL, as on a SQL Server session that never set
        // it. VS Code reads it on connecting.
        masked = RewriteCalls(masked, "CONTEXT_INFO", a => a.All(string.IsNullOrWhiteSpace) ? "NULL" : null);

        // The first argument is the collation to describe, and SSMS passes a subquery over
        // sys.databases rather than a name. It is ignored: there is one collation here, so the
        // property alone decides the answer, and the subquery never has to be evaluated at
        // translation time to find out which collation was meant.
        masked = RewriteCalls(masked, "COLLATIONPROPERTY", a => a.Count == 2
            ? CollationProperty(Literal(a[1]) ?? "")
            : null);

        // OLE DB asks this on every new session, passing back the five collation bytes the login
        // response sent it - DaffyDB's own - to learn that collation's name.
        masked = RewriteCalls(masked, "COLLATIONNAME", a => a.Count == 1
            ? Quote("SQL_Latin1_General_CP1_CI_AS")
            : null);

        masked = Regex.Replace(masked, @"\b(CURRENT_USER|SESSION_USER|SYSTEM_USER)\b",
                               _ => Quote("dbo"), RegexOptions.IgnoreCase);

        masked = RewriteCalls(masked, "DATEPART", a => a.Count == 2
            ? DatePartOf(DatePart(Literal(a[0]) ?? a[0]), DateValue(a[1], Literal))
            : null);
        masked = RewriteCalls(masked, "DATENAME", a => a.Count == 2
            ? DateName(DatePart(Literal(a[0]) ?? a[0]), DateValue(a[1], Literal))
            : null);
        masked = RewriteEachCall(masked, "DATEDIFF_BIG", a => a.Count == 3
            ? DateDiff(DatePart(Literal(a[0]) ?? a[0]), DateValue(a[1], Literal), DateValue(a[2], Literal))
            : null);
        masked = RewriteEachCall(masked, "DATEDIFF", a => a.Count == 3
            ? DateDiff(DatePart(Literal(a[0]) ?? a[0]), DateValue(a[1], Literal), DateValue(a[2], Literal))
            : null);
        masked = RewriteCalls(masked, "DATEADD", a => a.Count == 3
            ? DateAdd(DatePart(Literal(a[0]) ?? a[0]), a[1], DateValue(a[2], Literal))
            : null);
        masked = RewriteCalls(masked, "EOMONTH", a => a.Count switch
        {
            1 => $"last_day({DateValue(a[0], Literal)})",
            2 => $"last_day({DateValue(a[0], Literal)} + to_months(CAST({a[1]} AS INTEGER)))",
            _ => null,
        });
        masked = RewriteCalls(masked, "DATEFROMPARTS", a => a.Count == 3 ? $"make_date({a[0]}, {a[1]}, {a[2]})" : null);
        masked = RewriteCalls(masked, "DATETIMEFROMPARTS", a => a.Count == 7
            ? $"make_timestamp({a[0]}, {a[1]}, {a[2]}, {a[3]}, {a[4]}, {a[5]} + ({a[6]}) / 1000.0)"
            : null);

        masked = MapCastTypes(masked);

        if (limit is not null) masked += " LIMIT " + limit;

        masked = CompactParameters(masked, ref parameterOrder);
        return new TranslatedSql(Unmask(masked, replacements), parameterOrder, notes);
    }

    /// <summary>
    /// Renumbers the $n placeholders that survived translation as $1, $2, ... with no gaps.
    ///
    /// Variables are numbered early, and a later rewrite can drop one with the call around it:
    /// SUSER_SNAME(sid_binary(ISNULL(@SqlGroup, N''))) becomes the login's name, and $17 goes with
    /// it. The values are bound by position, so every one after the gap then lands one place off,
    /// and DuckDB reports the last as never supplied. Only the placeholders still in the text are
    /// kept - string literals are masked at this point, so a '$1' inside one is not mistaken for one.
    /// </summary>
    private static string CompactParameters(string sql, ref IReadOnlyList<string> parameterOrder)
    {
        var original = parameterOrder;
        var compacted = new List<string>();
        var renumbered = new Dictionary<int, int>();

        string result = Regex.Replace(sql, @"\$(\d+)\b", match =>
        {
            int old = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (old < 1 || old > original.Count) return match.Value;

            if (!renumbered.TryGetValue(old, out int current))
            {
                compacted.Add(original[old - 1]);
                current = compacted.Count;
                renumbered[old] = current;
            }
            return "$" + current.ToString(CultureInfo.InvariantCulture);
        });

        parameterOrder = compacted;
        return result;
    }

    /// <summary>Renders a nullable number as SQL: the value, or NULL when there is none.</summary>
    private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "NULL";

    private static string Mask(List<SqlSegment> segments, out List<SqlSegment> replacements)
    {
        replacements = [];
        var masked = new StringBuilder();

        foreach (var segment in segments)
        {
            if (segment.Kind == SegmentKind.Code) masked.Append(segment.Text);
            else masked.Append(Mask(segment, replacements));
        }

        return masked.ToString();
    }

    private static string Mask(SqlSegment segment, List<SqlSegment> replacements)
    {
        replacements.Add(segment);
        return $"{MaskOpen}{replacements.Count - 1}{MaskClose}";
    }

    private static string Unmask(string masked, List<SqlSegment> replacements) =>
        Regex.Replace(masked, $"{MaskOpen}(\\d+){MaskClose}", match =>
        {
            var segment = replacements[int.Parse(match.Groups[1].Value)];
            return segment.Kind switch
            {
                // T-SQL delimits identifiers with brackets; DuckDB uses double quotes, and treats
                // brackets as list indexing.
                SegmentKind.BracketIdentifier =>
                    '"' + SqlSegments.IdentifierName(segment.Text, '[', ']').Replace("\"", "\"\"") + '"',
                _ => segment.Text,
            };
        });

    /// <summary>
    /// The INFORMATION_SCHEMA views DaffyDB provides in SQL Server's shape. The rest are left to
    /// DuckDB's own, which exist under the same names and describe the same objects in DuckDB's
    /// terms.
    /// </summary>
    private static readonly HashSet<string> InformationSchemaViews = new(StringComparer.OrdinalIgnoreCase)
    {
        "TABLES", "COLUMNS", "VIEWS", "SCHEMATA", "ROUTINES", "PARAMETERS",
    };

    /// <summary>
    /// Points INFORMATION_SCHEMA.TABLES and its siblings at the SQL Server-shaped views in each
    /// database's sys schema, keeping any database qualifier.
    ///
    /// DuckDB has an information_schema of its own, and it cannot be replaced: a schema by that
    /// name is reserved in every catalog. Its answers are right for DuckDB and wrong for a SQL
    /// Server client - they span every database at once, call every mounted file a VIEW, name
    /// types VARCHAR and INTEGER, and have no ROUTINES at all, which is the view Power BI's
    /// navigator reads straight after TABLES.
    /// </summary>
    private static string RewriteInformationSchema(string sql, Func<string, string?> identifier)
    {
        string part = $@"(?:\w+|{MaskOpen}\d+{MaskClose})";
        var pattern = new Regex(
            $@"(?<![\w.{MaskClose}])(?:(?<database>{part})\s*\.\s*)?(?<schema>{part})\s*\.\s*(?<view>{part})");

        return pattern.Replace(sql, match =>
        {
            if (!string.Equals(identifier(match.Groups["schema"].Value), "INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase))
                return match.Value;

            string? view = identifier(match.Groups["view"].Value);
            if (view is null || !InformationSchemaViews.Contains(view)) return match.Value;

            string database = match.Groups["database"].Success ? match.Groups["database"].Value + "." : "";
            return $"{database}sys.information_schema_{view.ToLowerInvariant()}";
        });
    }

    /// <summary>
    /// SQL Server 2000's catalog views answer to their bare names in any database: "FROM systypes".
    /// Here they live in sys, where DuckDB looks only when told to.
    /// </summary>
    private static string RewriteLegacyViews(string sql) =>
        Regex.Replace(sql, $@"(?<![\w.{MaskClose}])systypes\b", "sys.systypes", RegexOptions.IgnoreCase);

    /// <summary>
    /// T-SQL names a temp table #foo (or ##foo when global). DuckDB has temp tables but # is not a
    /// legal identifier character, so the name is quoted and the table made genuinely temporary.
    /// CREATE and DROP are made idempotent because a client that reconnects will run them again.
    /// </summary>
    private static string RewriteTempTables(string sql)
    {
        if (!sql.Contains('#')) return sql;

        sql = Regex.Replace(sql, @"(?<![\w""])(#{1,2}\w+)", match => '"' + match.Groups[1].Value + '"');
        sql = Regex.Replace(sql, @"\bCREATE\s+TABLE\s+(?="")", "CREATE OR REPLACE TEMP TABLE ", RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"\bDROP\s+TABLE\s+(?="")", "DROP TABLE IF EXISTS ", RegexOptions.IgnoreCase);
        return sql;
    }

    /// <summary>
    /// Makes a VALUES column that mixes string literals with anything else uniformly text.
    ///
    /// SQL Server types a VALUES list against its destination and converts as it goes, so a column
    /// holding both '' and 0 is unremarkable — SMO writes exactly that. DuckDB types the list on its
    /// own and refuses to combine INTEGER with VARCHAR, so the mixed column is cast to text here and
    /// converted back on the way into the table.
    /// </summary>
    private static string UnifyValuesColumns(string sql, Func<string, bool> isStringLiteral)
    {
        int search = 0;

        for (int guard = 0; guard < 100; guard++)
        {
            var keyword = Regex.Match(sql[search..], @"\bVALUES\b", RegexOptions.IgnoreCase);
            if (!keyword.Success) return sql;

            int afterValues = search + keyword.Index + keyword.Length;
            var rows = ReadValueRows(sql, afterValues);
            search = afterValues;

            if (rows.Count < 2) continue;

            int width = rows[0].Items.Count;
            if (rows.Any(row => row.Items.Count != width)) continue;

            var mixed = new bool[width];
            for (int column = 0; column < width; column++)
            {
                bool anyText = rows.Any(row => isStringLiteral(row.Items[column]));
                bool anyOther = rows.Any(row => !isStringLiteral(row.Items[column]));
                mixed[column] = anyText && anyOther;
            }

            if (!mixed.Any(m => m)) continue;

            // Rebuild back to front so earlier offsets stay valid.
            for (int index = rows.Count - 1; index >= 0; index--)
            {
                var row = rows[index];
                string rebuilt = "(" + string.Join(", ",
                    row.Items.Select((item, column) => mixed[column] ? $"CAST({item} AS VARCHAR)" : item)) + ")";

                sql = sql[..row.Start] + rebuilt + sql[(row.End + 1)..];
            }
        }

        return sql;
    }

    /// <summary>Reads the "(...), (...), ..." that follows a VALUES keyword.</summary>
    private static List<(int Start, int End, List<string> Items)> ReadValueRows(string sql, int position)
    {
        var rows = new List<(int, int, List<string>)>();

        while (true)
        {
            while (position < sql.Length && char.IsWhiteSpace(sql[position])) position++;
            if (position >= sql.Length || sql[position] != '(') break;

            int close = FindMatchingParen(sql, position);
            if (close < 0) break;

            rows.Add((position, close, SplitArguments(sql[(position + 1)..close])));
            position = close + 1;

            int resume = position;
            while (position < sql.Length && char.IsWhiteSpace(sql[position])) position++;
            if (position < sql.Length && sql[position] == ',') { position++; continue; }

            position = resume;
            break;
        }

        return rows;
    }

    /// <summary>T-SQL lets INSERT omit INTO; DuckDB requires it.</summary>
    private static string AddMissingInsertInto(string sql) =>
        Regex.Replace(sql, @"^(\s*)INSERT\s+(?!INTO\b)", "$1INSERT INTO ", RegexOptions.IgnoreCase);

    /// <summary>
    /// Maps the column types in a CREATE TABLE, which the CAST rule does not reach. Only the type
    /// half of each column definition is touched, so a column named "date" stays as it is.
    /// </summary>
    private static string RewriteCreateTableTypes(string sql)
    {
        var match = Regex.Match(sql,
            @"^\s*CREATE\s+(OR\s+REPLACE\s+)?(TEMP(ORARY)?\s+)?TABLE\s+\S+\s*\(", RegexOptions.IgnoreCase);
        if (!match.Success) return sql;

        int open = match.Index + match.Length - 1;
        int close = FindMatchingParen(sql, open);
        if (close < 0) return sql;

        var columns = SplitArguments(sql[(open + 1)..close]).Select(column =>
        {
            var parts = Regex.Match(column.Trim(), @"^(?<name>\S+)\s+(?<type>[A-Za-z_]\w*(\s*\([^)]*\))?)(?<rest>.*)$",
                                    RegexOptions.Singleline);
            return parts.Success
                ? $"{parts.Groups["name"].Value} {MapType(parts.Groups["type"].Value)}{parts.Groups["rest"].Value}"
                : column;
        });

        return sql[..(open + 1)] + string.Join(", ", columns) + sql[close..];
    }

    /// <summary>@name placeholders become DuckDB's positional $1, $2, ... in order of first use.</summary>
    private static string RewriteParameters(string sql, out IReadOnlyList<string> parameterOrder)
    {
        var order = new List<string>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        string result = Regex.Replace(sql, @"(?<!@)@(?!@)(\w+)", match =>
        {
            string name = "@" + match.Groups[1].Value;
            if (!seen.TryGetValue(name, out int index))
            {
                order.Add(name);
                index = order.Count;
                seen[name] = index;
            }
            return "$" + index;
        });

        parameterOrder = order;
        return result;
    }

    /// <summary>The @@ globals a client is likely to ask for on connect.</summary>
    private static string RewriteGlobals(string sql, Func<string, string> quote) =>
        Regex.Replace(sql, @"@@(\w+)", match => match.Groups[1].Value.ToUpperInvariant() switch
        {
            "VERSION" => quote(VersionBanner),
            "SERVERNAME" or "SERVICENAME" => quote(ServerName),
            "SPID" => "51",
            "ROWCOUNT" or "ERROR" or "TRANCOUNT" or "FETCH_STATUS" => "0",
            // (major << 24) | (minor << 16) | build, which is how clients take it apart again.
            "MICROSOFTVERSION" => ((17 << 24) | (0 << 16) | 4025).ToString(),
            "LANGUAGE" => quote("us_english"),
            "MAX_PRECISION" => "38",
            _ => match.Value,
        }, RegexOptions.IgnoreCase);

    /// <summary>
    /// Permission and identity checks. This proxy has one user with access to everything, so these
    /// answer yes — a client that asks whether it may see something is not told no.
    /// </summary>
    private static readonly (string Function, string Value)[] ConstantFunctions =
    [
        ("HAS_DBACCESS", "1"),
        ("IS_SRVROLEMEMBER", "1"),
        ("IS_MEMBER", "1"),
        ("IS_ROLEMEMBER", "1"),
        ("HAS_PERMS_BY_NAME", "1"),
        ("PERMISSIONS", "4194303"),
        ("USER_ID", "1"),
        ("SUSER_ID", "1"),
        ("SUSER_SID", "NULL"),
        ("SCOPE_IDENTITY", "NULL"),
        ("NEWID", "uuid()"),
    ];

    /// <summary>
    /// OBJECTPROPERTY, for what a client asks of a table or view here: no triggers and no indexes -
    /// a file is a heap - owned through its schema by dbo, with quoted identifiers and ANSI NULLs on,
    /// as DuckDB always has them. LocalDB answers the same for a heap with no triggers. An object
    /// that does not exist, or a property not listed, is NULL, as in SQL Server. VS Code reads the
    /// triggers, indexes and owner with a table's properties, before it will script "Select Top
    /// 1000 Rows".
    ///
    /// The lookup renames sys.objects' columns, so that an unqualified object_id handed in -
    /// OBJECTPROPERTY(object_id, 'IsUserTable') over sys.objects, the usual way to write it - still
    /// means the row outside, not every row of the lookup.
    /// </summary>
    private static string ObjectProperty(string id, string property, Func<string, string> quote)
    {
        string? value = property.ToUpperInvariant() switch
        {
            "ISUSERTABLE" or "ISTABLE" => $"CASE WHEN __op_type = {quote("U")} THEN 1 ELSE 0 END",
            "ISVIEW" => $"CASE WHEN __op_type = {quote("V")} THEN 1 ELSE 0 END",
            // A table can take an index; a view only with SCHEMABINDING, which no view here has.
            "ISINDEXABLE" => $"CASE WHEN __op_type = {quote("U")} THEN 1 ELSE 0 END",
            "ISINDEXED" or "ISMSSHIPPED" or "ISSYSTEMTABLE" or "ISPROCEDURE" or "ISTRIGGER"
                or "TABLEHASPRIMARYKEY" or "TABLEHASIDENTITY" or "HASAFTERTRIGGER" or "HASINSERTTRIGGER"
                or "HASDELETETRIGGER" or "HASINSTEADOFTRIGGER" or "HASUPDATETRIGGER" => "0",
            "ISQUOTEDIDENTON" or "ISANSINULLSON" => "1",
            "OWNERID" => "1",
            _ => null,
        };

        return value is null
            ? "NULL"
            : $"(SELECT CAST({value} AS INTEGER) FROM (SELECT object_id AS __op_id, type AS __op_type FROM sys.objects) " +
              $"WHERE __op_id = {id})";
    }

    private static string DatabaseProperty(string property) => property.ToUpperInvariant() switch
    {
        "STATUS" => "ONLINE",
        "UPDATEABILITY" => "READ_WRITE",
        "USERACCESS" => "MULTI_USER",
        "RECOVERY" => "SIMPLE",
        "COLLATION" => "SQL_Latin1_General_CP1_CI_AS",
        "VERSION" => "0",

        // The character set and sort order behind SQL_Latin1_General_CP1_CI_AS. The ODBC Driver 18
        // setup wizard looks the character set up in sys.syscharsets by this id.
        "SQLCHARSET" => "1",
        "SQLCHARSETNAME" => "iso_1",
        "SQLSORTORDER" => "52",
        "SQLSORTORDERNAME" => "nocase_iso",
        "LCID" => "1033",
        _ => "",
    };

    /// <summary>
    /// Reduces a possibly qualified, possibly bracketed name such as [dbo].[orders] to just
    /// "orders", which is what the catalog views key on.
    /// </summary>
    /// <summary>
    /// One part of a dotted name, counted from the right: 1 is the object, 2 its schema, NULL when
    /// the name has no such part. [Brackets] and "quotes" are taken off first.
    /// </summary>
    private static string DottedPart(string expression, int fromRight) =>
        $"list_reverse(string_split(replace(replace(replace(CAST({expression} AS VARCHAR), '[', ''), ']', ''), '\"', ''), '.'))[{fromRight}]";

    /// <summary>
    /// Identity and context functions, answered from the connection that asked. Clients call these
    /// while connecting, long before they run anything a person typed.
    ///
    /// SUSER_SNAME and friends are the *login*, so they report whoever authenticated. USER_NAME and
    /// CURRENT_USER are the *database user*, which on a server with one owner is always dbo — that
    /// distinction is real in SQL Server and tools rely on it. DB_NAME is handled beside DB_ID,
    /// because given an argument it is a lookup rather than a fact about the caller.
    /// </summary>
    private static (string Function, string Value)[] ContextFunctions(SessionContext session) =>
    [
        ("USER_NAME", "dbo"),
        ("SUSER_SNAME", session.UserName),
        ("SUSER_NAME", session.UserName),
        ("ORIGINAL_LOGIN", session.UserName),
        ("HOST_NAME", session.HostName),
        ("APP_NAME", session.ApplicationName),
    ];

    private static readonly HashSet<string> NumericServerProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "EngineEdition", "EditionID", "ProductMajorVersion", "ProductMinorVersion", "ProductBuild",
        "IsClustered", "IsHadrEnabled", "IsIntegratedSecurityOnly", "IsFullTextInstalled",
        "IsSingleUser", "IsXTPSupported", "IsPolyBaseInstalled", "IsLocalDB",
        "CollationID", "ComparisonStyle", "SqlCharSet", "SqlSortOrder", "LCID",
    };

    private static string ServerProperty(string property) => property.ToUpperInvariant() switch
    {
        "PRODUCTVERSION" => ProductVersion,
        "PRODUCTLEVEL" => "RTM",
        "PRODUCTMAJORVERSION" => "17",
        "PRODUCTMINORVERSION" => "0",
        "PRODUCTBUILD" => "4025",
        "EDITION" => "Developer Edition (64-bit)",
        "ENGINEEDITION" => "3",

        // Developer's edition number. SSMS 22 reads it with CONVERT(bigint, ...) while connecting,
        // so a NULL here stops the connection with "Object cannot be cast from DBNull".
        "EDITIONID" => "-2117995310",
        "MACHINENAME" or "SERVERNAME" or "INSTANCENAME" or "COMPUTERNAMEPHYSICALNETBIOS" => ServerName,
        "COLLATION" => "SQL_Latin1_General_CP1_CI_AS",
        "ISCLUSTERED" or "ISHADRENABLED" or "ISINTEGRATEDSECURITYONLY" => "0",
        "ISFULLTEXTINSTALLED" or "ISSINGLEUSER" or "ISPOLYBASEINSTALLED" or "ISXTPSUPPORTED" or "ISLOCALDB" => "0",
        "PATHSEPARATOR" => "\\",
        "RESOURCEVERSION" => ProductVersion,
        "RESOURCELASTUPDATEDATETIME" => "2024-01-01T00:00:00",
        "BUILDCLRVERSION" => "v4.0.30319",

        // Values that describe SQL_Latin1_General_CP1_CI_AS.
        "COLLATIONID" => "872468488",
        "COMPARISONSTYLE" => "196609",
        "SQLCHARSET" => "1",
        "SQLCHARSETNAME" => "iso_1",
        "SQLSORTORDER" => "52",
        "SQLSORTORDERNAME" => "nocase_iso",
        "LCID" => "1033",
        _ => "",
    };

    /// <summary>
    /// What COLLATIONPROPERTY answers about SQL_Latin1_General_CP1_CI_AS, the one collation this
    /// server reports. These are the real values for it, and they agree with the ones
    /// SERVERPROPERTY already publishes. An unrecognised property is NULL, as it is on a real
    /// server; the result is an integer rather than a quoted string because every one of them is.
    /// </summary>
    private static string CollationProperty(string property) => property.ToUpperInvariant() switch
    {
        "CODEPAGE" => "1252",
        "LCID" => "1033",
        "COMPARISONSTYLE" => "196609",
        "VERSION" => "0",
        _ => "NULL",
    };

    /// <summary>Builds the QUOTENAME expression: open || escaped value || close.</summary>
    private static string Wrap(string value, string open, string close, string escaped, string doubled,
                               Func<string, string> quote) =>
        $"({quote(open)} || replace({value}, {quote(escaped)}, {quote(doubled)}) || {quote(close)})";

    /// <summary>T-SQL writes hex as 0xFF; DuckDB has no such literal, so it becomes decimal.</summary>
    private static string RewriteHexLiterals(string sql) =>
        Regex.Replace(sql, @"(?<![\w.])0[xX]([0-9a-fA-F]+)\b", match =>
        {
            var value = BigInteger.Parse("0" + match.Groups[1].Value, NumberStyles.HexNumber);
            return value.ToString();
        });

    private static string ConnectionProperty(string property) => property.ToUpperInvariant() switch
    {
        "NET_TRANSPORT" => "TCP",
        "PROTOCOL_TYPE" => "TSQL",
        "AUTH_SCHEME" => "SQL",
        "LOCAL_NET_ADDRESS" or "CLIENT_NET_ADDRESS" => "127.0.0.1",
        "LOCAL_TCP_PORT" => "1433",
        _ => "",
    };

    /// <summary>OFFSET n ROWS FETCH NEXT m ROWS ONLY becomes LIMIT m OFFSET n.</summary>
    private static string RewriteOffsetFetch(string sql) =>
        Regex.Replace(sql,
            @"\bOFFSET\s+(\S+)\s+ROWS?(?:\s+FETCH\s+(?:NEXT|FIRST)\s+(\S+)\s+ROWS?\s+ONLY)?",
            match => match.Groups[2].Success
                ? $"LIMIT {match.Groups[2].Value} OFFSET {match.Groups[1].Value}"
                : $"OFFSET {match.Groups[1].Value}",
            RegexOptions.IgnoreCase);

    /// <summary>A row count on a TOP: either a literal, or a parameter already rewritten to $n.</summary>
    private static readonly Regex TopPattern = new(
        @"\bTOP\s*(?:\(\s*(\d+|\$\d+)\s*\)|(\d+|\$\d+)\b)", RegexOptions.IgnoreCase);

    /// <summary>
    /// SELECT TOP n / TOP (n) becomes LIMIT n - on the query the TOP actually belongs to.
    ///
    /// Which query that is, is the whole difficulty. T-SQL writes the row count at the front of a
    /// SELECT and DuckDB writes it at the back, so the two are the same statement only while there
    /// is one SELECT. A table listing from Object Explorer has six, five of them scalar subqueries
    /// in the select list, and hoisting whichever TOP comes first out to the end of the statement
    /// silently limits the wrong query - the outer one - to a single row.
    ///
    /// So each TOP is matched to its own scope by counting parentheses forward from it: the first
    /// unbalanced ")" closes the subquery it sits in, and LIMIT goes just inside that. Landing
    /// there also puts it after any ORDER BY, which is where DuckDB needs it. A TOP that never
    /// meets an unbalanced ")" belongs to the statement itself, and is handed back through
    /// <paramref name="limit"/> to be appended once every other rewrite has run.
    ///
    /// Parentheses can be counted like this because the statement is masked: every string literal,
    /// comment and quoted identifier is already one opaque token, so none of them can contribute a
    /// bracket of their own.
    /// </summary>
    private static string RewriteTop(string sql, out string? limit, List<string> notes)
    {
        limit = null;

        var matches = TopPattern.Matches(sql);
        if (matches.Count == 0) return sql;

        // WITH TIES, or a row count that is an expression: matched by neither branch above, so it
        // reaches DuckDB verbatim and fails there. Saying so beats letting the error speak.
        if (Regex.IsMatch(sql, @"\bTOP\b(?!\s*\(?\s*(?:\d|\$))", RegexOptions.IgnoreCase))
            notes.Add("a TOP whose row count is not a literal or a parameter was left as-is");

        // Every edit is measured against the original string and applied back to front, so no edit
        // can shift the offsets of one still to come.
        var edits = new List<(int Position, int Length, string Text)>();
        var scopesTaken = new HashSet<int>();

        foreach (Match match in matches)
        {
            // TOP n PERCENT asks for a proportion, not a row count, and DuckDB has no equivalent.
            // Turning it into LIMIT n would quietly answer a different question, so it is left
            // alone: an error the client can see beats a plausible wrong answer it cannot.
            if (Regex.IsMatch(sql[(match.Index + match.Length)..], @"^\s*PERCENT\b", RegexOptions.IgnoreCase))
            {
                notes.Add("TOP ... PERCENT is not supported; it was left for DuckDB to reject");
                continue;
            }

            string count = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            int close = EnclosingClose(sql, match.Index + match.Length);

            if (close < 0)
            {
                // The outermost query. Appending after every other rewrite is how this has always
                // worked, and it keeps the LIMIT clear of a trailing comment the mask is hiding.
                if (limit is not null)
                {
                    notes.Add("two TOP clauses share the outermost query; the second was left as-is");
                    continue;
                }

                limit = count;
            }
            else if (scopesTaken.Add(close))
            {
                edits.Add((close, 0, $" LIMIT {count} "));
            }
            else
            {
                // Two scopes cannot share one LIMIT - a UNION inside a subquery is the way to get
                // here. Removing the TOP without replacing it would answer the wrong question
                // quietly, so it stays put and DuckDB rejects the statement instead.
                notes.Add("two TOP clauses share one subquery; the second was left as-is");
                continue;
            }

            // Only now that the row count has somewhere to go is the TOP taken out.
            edits.Add((match.Index, match.Length, ""));
        }

        foreach (var (position, length, text) in edits.OrderByDescending(e => e.Position))
            sql = sql.Remove(position, length).Insert(position, text);

        return sql;
    }

    /// <summary>
    /// The index of the ")" closing the parenthesised group that <paramref name="from"/> sits
    /// inside, or -1 when it sits at the top level of the statement.
    /// </summary>
    private static int EnclosingClose(string sql, int from)
    {
        int depth = 0;

        for (int i = from; i < sql.Length; i++)
        {
            if (sql[i] == '(') depth++;
            else if (sql[i] == ')' && depth-- == 0) return i;
        }

        return -1;
    }

    /// <summary>
    /// Functions that differ only by name. LOG is the dangerous one: in T-SQL it is the natural
    /// logarithm, but in DuckDB log() is base 10 and ln() is natural.
    /// </summary>
    private static readonly (string TSql, string DuckDb)[] SimpleNames =
    [
        ("ISNULL", "COALESCE"),
        ("REPLICATE", "repeat"),
        ("RAND", "random"),
        ("LOG10", "log"),
        ("LOG", "ln"),
        ("NCHAR", "chr"),
        ("CHAR", "chr"),
    ];

    /// <summary>
    /// Renames every function in one pass. Doing them one regex at a time would let a later rule
    /// rewrite an earlier rule's output — LOG10 becomes log(), which the LOG rule would then turn
    /// into ln().
    /// </summary>
    private static string RewriteSimpleNames(string sql)
    {
        var byName = SimpleNames.ToDictionary(n => n.TSql, n => n.DuckDb, StringComparer.OrdinalIgnoreCase);
        string alternation = string.Join('|', SimpleNames.Select(n => n.TSql).OrderByDescending(n => n.Length));

        // Not after AS, where CHAR(10) is a type rather than a call.
        sql = Regex.Replace(sql, $@"(?<!\bAS\s+)\b({alternation})\s*\(",
                            match => byName[match.Groups[1].Value] + "(", RegexOptions.IgnoreCase);

        // Zero-argument T-SQL clock functions, with and without parentheses.
        sql = Regex.Replace(sql, @"\b(GETDATE|SYSDATETIME|GETUTCDATE|SYSUTCDATETIME)\s*\(\s*\)", "now()",
                            RegexOptions.IgnoreCase);
        return sql;
    }

    /// <summary>
    /// T-SQL overloads + for both addition and string concatenation, and telling them apart needs
    /// real type resolution. The one case that is unambiguous is a string literal on either side,
    /// which is also the common one, so that alone is rewritten to DuckDB's || operator.
    /// </summary>
    private static string RewriteStringConcat(string sql, Func<string, bool> isStringLiteral)
    {
        var pattern = new Regex($@"({MaskOpen}\d+{MaskClose})\s*\+|\+\s*({MaskOpen}\d+{MaskClose})");

        for (int guard = 0; guard < 100; guard++)
        {
            var match = pattern.Matches(sql)
                .FirstOrDefault(m => isStringLiteral(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value));
            if (match is null) return sql;

            int plus = sql.IndexOf('+', match.Index);
            sql = sql[..plus] + "||" + sql[(plus + 1)..];
        }

        return sql;
    }

    /// <summary>
    /// Finds NAME(...) with balanced parentheses, splits the arguments at depth zero, and replaces
    /// the whole call with whatever <paramref name="build"/> returns. Repeats until no occurrence
    /// remains, which also handles the same function nested inside its own arguments.
    /// </summary>
    private static string RewriteCalls(string sql, string name, Func<List<string>, string?> build)
    {
        var pattern = new Regex($@"\b{name}\s*\(", RegexOptions.IgnoreCase);

        for (int guard = 0; guard < 100; guard++)
        {
            var match = pattern.Match(sql);
            if (!match.Success) return sql;

            int openParen = match.Index + match.Length - 1;
            int close = FindMatchingParen(sql, openParen);
            if (close < 0) return sql;

            var args = SplitArguments(sql[(openParen + 1)..close]);
            string? replacement = build(args);
            if (replacement is null) return sql;

            sql = sql[..match.Index] + replacement + sql[(close + 1)..];
        }

        return sql;
    }

    private static int FindMatchingParen(string sql, int openIndex)
    {
        int depth = 0;
        for (int i = openIndex; i < sql.Length; i++)
        {
            if (sql[i] == '(') depth++;
            else if (sql[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    private static List<string> SplitArguments(string inner)
    {
        var args = new List<string>();
        int depth = 0, start = 0;

        for (int i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '(') depth++;
            else if (inner[i] == ')') depth--;
            else if (inner[i] == ',' && depth == 0)
            {
                args.Add(inner[start..i].Trim());
                start = i + 1;
            }
        }

        if (inner.Trim().Length > 0) args.Add(inner[start..].Trim());
        return args;
    }

    /// <summary>Maps T-SQL type names onto DuckDB ones, preserving any precision/scale.</summary>
    private static string MapType(string type)
    {
        string trimmed = type.Trim();
        var match = Regex.Match(trimmed, @"^(\w+)\s*(\(([^)]*)\))?$");
        if (!match.Success) return trimmed;

        string name = match.Groups[1].Value.ToLowerInvariant();
        string arguments = match.Groups[3].Value.Trim();

        return name switch
        {
            "int" or "integer" => "INTEGER",
            "bigint" => "BIGINT",
            "smallint" => "SMALLINT",
            "tinyint" => "SMALLINT",
            "bit" => "BOOLEAN",
            "char" or "nchar" or "varchar" or "nvarchar" or "text" or "ntext" or "sysname" => "VARCHAR",
            "date" => "DATE",
            "datetime" or "datetime2" or "smalldatetime" => "TIMESTAMP",
            "time" => "TIME",
            "float" or "real" => "DOUBLE",
            "money" or "smallmoney" => "DECIMAL(19,4)",
            "decimal" or "numeric" => arguments.Length > 0 ? $"DECIMAL({arguments})" : "DECIMAL(18,0)",
            "uniqueidentifier" => "UUID",
            "varbinary" or "binary" or "image" => "BLOB",
            _ => trimmed,
        };
    }
}
