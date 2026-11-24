using System.Text.RegularExpressions;
using TdsProxy.Query;

namespace TdsProxy.Sql;

/// <summary>
/// A system stored procedure DaffyDB answers. <paramref name="Sql"/> is DuckDB SQL that refers to
/// each parameter as $name; <paramref name="Parameters"/> lists them in declaration order, which is
/// the order positional arguments bind in. <paramref name="Columns"/>, when given, is the shape the
/// result is sent in - names and types exactly as SQL Server declares them - because ODBC and OLE DB
/// read these results by position and type, and some columns have no name at all.
/// </summary>
internal sealed record SystemProcedure(
    string Name,
    string Sql,
    string[]? Parameters = null,
    IReadOnlyList<ColumnDef>? Columns = null,
    IReadOnlyDictionary<string, object?>? Defaults = null,
    string[]? Patterns = null)
{
    public string[] ParameterNames => Parameters ?? [];

    /// <summary>A procedure with no SQL does nothing, returns nothing, and takes any arguments.</summary>
    public bool IsNoOp => Sql.Length == 0;
}

/// <summary>One argument of a call written as text: EXEC name N'orders', @ODBCVer = 3.</summary>
internal sealed record ProcedureArgument(string? Name, string Expression);

internal sealed record ProcedureCall(string Name, IReadOnlyList<ProcedureArgument> Arguments);

/// <summary>
/// System stored procedures a client calls for information the catalog does not expose.
///
/// There is one table of them, reached two ways. ODBC and OLE DB send an RPC naming the procedure,
/// with no SQL text at all; a person, or a tool like SSMS, writes EXEC name args in a batch. Both
/// arrive at <see cref="Find"/> with the same name and bind their arguments the same way, so a
/// procedure is written once and works from either side.
/// </summary>
internal static class SystemProcedures
{
    /// <summary>SQL Server's number for "Could not find stored procedure", which clients recognise.</summary>
    public const int NotFoundNumber = 2812;

    /// <summary>Stands in for the DEFAULT keyword: take the parameter's default, as if omitted.</summary>
    public static readonly object DefaultValue = new();

    /// <summary>
    /// The server's capabilities as older drivers read them, one attribute per row, or just the one
    /// @attribute_id names. The product's name and version line are DaffyDB's own, worded as
    /// @@VERSION's first line is. Every other value is LocalDB's, because a client reading them is
    /// deciding how to speak T-SQL, and T-SQL is what the translator takes.
    /// </summary>
    private static readonly SystemProcedure ServerInfo = new("sp_server_info", $"""
        SELECT attribute_id, attribute_name, attribute_value
        FROM (VALUES
            (1,   'DBMS_NAME',              '{TSqlTranslator.ProductName}'),
            (2,   'DBMS_VER',               '{TSqlTranslator.VersionLine}'),
            (10,  'OWNER_TERM',             'owner'),
            (11,  'TABLE_TERM',             'table'),
            (12,  'MAX_OWNER_NAME_LENGTH',  '128'),
            (13,  'TABLE_LENGTH',           '128'),
            (14,  'MAX_QUAL_LENGTH',        '128'),
            (15,  'COLUMN_LENGTH',          '128'),
            (16,  'IDENTIFIER_CASE',        'MIXED'),
            (17,  'TX_ISOLATION',           '2'),
            (18,  'COLLATION_SEQ',          'charset=iso_1 sort_order=nocase_iso charset_num=1 sort_order_num=52'),
            (19,  'SAVEPOINT_SUPPORT',      'Y'),
            (20,  'MULTI_RESULT_SETS',      'Y'),
            (22,  'ACCESSIBLE_TABLES',      'Y'),
            (100, 'USERID_LENGTH',          '128'),
            (101, 'QUALIFIER_TERM',         'database'),
            (102, 'NAMED_TRANSACTIONS',     'Y'),
            (103, 'SPROC_AS_LANGUAGE',      'Y'),
            (104, 'ACCESSIBLE_SPROC',       'Y'),
            (105, 'MAX_INDEX_COLS',         '16'),
            (106, 'RENAME_TABLE',           'Y'),
            (107, 'RENAME_COLUMN',          'Y'),
            (108, 'DROP_COLUMN',            'Y'),
            (109, 'INCREASE_COLUMN_LENGTH', 'Y'),
            (110, 'DDL_IN_TRANSACTION',     'Y'),
            (111, 'DESCENDING_INDEXES',     'Y'),
            (112, 'SP_RENAME',              'Y'),
            (113, 'REMOTE_SPROC',           'Y'),
            (500, 'SYS_SPROC_VERSION',      '17.00.4025')
        ) AS t(attribute_id, attribute_name, attribute_value)
        WHERE $attribute_id IS NULL OR attribute_id = CAST($attribute_id AS INTEGER)
        ORDER BY attribute_id
        """,
        Parameters: ["attribute_id"],
        Columns:
        [
            new("attribute_id", SqlColumnType.Int, false),
            Shape.VarChar("attribute_name", 60),
            Shape.VarChar("attribute_value", 255),
        ]);

    private static readonly Dictionary<string, SystemProcedure> Procedures =
        new SystemProcedure[]
        {
            new("xp_msver", "SELECT * FROM sys.xp_msver"),
            ServerInfo,
            new("sp_databases", "SELECT name AS DATABASE_NAME, 0 AS DATABASE_SIZE, NULL AS REMARKS FROM sys.databases"),

            // MSOLEDBSQL runs this on every new session, before anything else: is the database
            // read-only, and who am I in it. Both columns are unnamed in SQL Server.
            new("sp_oledb_ro_usrname", "SELECT 'N', 'dbo'", Columns:
            [
                new("", SqlColumnType.VarChar, true, MaxLength: 1),
                new("", SqlColumnType.NVarChar, true, MaxLength: 256),
            ]),

            // SMO reads the registry through these to fill in the server's properties - audit level,
            // login mode, install paths - before it will script a Select Top 1000 Rows. There is no
            // registry here. They do nothing, and every OUTPUT variable they would have set stays
            // NULL, which SMO's own batch already treats as "not configured".
            new("xp_instance_regread", ""),
            new("xp_regread", ""),
            new("xp_qv", ""),
        }
        .Concat(OdbcProcedures.All)
        .Concat(OleDbProcedures.All)
        .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves a procedure however the client qualified it: [master].[sys].sp_tables_rowset2,
    /// master..xp_msver and sp_databases all name one procedure each. The schema is not checked,
    /// and a ;n group number, which only the legacy SQLOLEDB provider sends, is dropped. The
    /// database decides where the procedure runs, not which one it is; see <see cref="DatabaseOf"/>.
    /// </summary>
    public static SystemProcedure? Find(string qualifiedName) =>
        Procedures.GetValueOrDefault(BareName(qualifiedName));

    /// <summary>
    /// The database a three-part name puts the procedure in - daffydb in [daffydb].[sys].sp_tables,
    /// master in master..xp_msver - or null when the name does not say.
    /// </summary>
    public static string? DatabaseOf(string qualifiedName)
    {
        string[] parts = qualifiedName.Trim().Split('.');
        if (parts.Length < 3) return null;

        string database = parts[0].Trim().Trim('[', ']', '"');
        return database.Length == 0 ? null : database;
    }

    public static string BareName(string qualifiedName)
    {
        string name = qualifiedName.Trim();

        int group = name.LastIndexOf(';');
        if (group > 0 && name[(group + 1)..].Trim().All(char.IsAsciiDigit)) name = name[..group];

        string last = name.Split('.').Last(part => part.Trim().Length > 0);
        return last.Trim().Trim('[', ']', '"');
    }

    /// <summary>
    /// Matches arguments to parameters the way SQL Server does: positional ones in declaration
    /// order, then named ones by name. Anything left out takes its default, which is NULL unless
    /// the procedure says otherwise. Errors carry SQL Server's numbers and wording.
    /// </summary>
    public static Dictionary<string, object?> Bind(SystemProcedure procedure,
                                                   IEnumerable<(string? Name, object? Value)> arguments)
    {
        var bound = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var names = procedure.ParameterNames;
        int position = 0;

        foreach (var (rawName, value) in arguments)
        {
            string? name = string.IsNullOrEmpty(rawName) ? null : rawName.TrimStart('@');

            if (name is null)
            {
                if (position >= names.Length)
                    throw new TdsQueryException(new SqlErrorInfo(8144,
                        $"Procedure or function {procedure.Name} has too many arguments specified."));
                name = names[position++];
            }
            else if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw new TdsQueryException(new SqlErrorInfo(8145,
                    $"@{name} is not a parameter for procedure {procedure.Name}."));
            }

            if (!ReferenceEquals(value, DefaultValue)) bound[name] = value;
        }

        foreach (string name in names)
        {
            if (!bound.ContainsKey(name))
                bound[name] = procedure.Defaults?.GetValueOrDefault(name);
        }

        // A pattern argument is also bound as <name>_like: a case-insensitive regular expression for
        // DuckDB's regexp_full_match, which can express what T-SQL's LIKE does - [_] classes
        // included - where DuckDB's own LIKE cannot. The raw value stays bound too, because a
        // procedure's special cases test it literally: sp_tables lists databases when the qualifier
        // is exactly '%'. With @fUsePattern = 0 the argument is matched as plain text instead.
        bool usePatterns = !bound.TryGetValue("fUsePattern", out object? flag) || flag is null ||
                           (flag is bool on ? on : System.Convert.ToInt32(flag) != 0);

        foreach (string name in procedure.Patterns ?? [])
        {
            bound[name + "_like"] = bound.GetValueOrDefault(name) is string text
                ? "(?i)" + (usePatterns ? LikeToRegex(text) : EscapeRegex(text))
                : null;
        }

        return bound;
    }

    /// <summary>T-SQL LIKE to RE2: % and _ are wildcards, [...] is a character class, and the rest is literal.</summary>
    public static string LikeToRegex(string pattern)
    {
        var regex = new System.Text.StringBuilder();

        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            switch (c)
            {
                case '%': regex.Append(".*"); break;
                case '_': regex.Append('.'); break;
                case '[':
                    int close = pattern.IndexOf(']', i + 1);
                    if (close < 0) { regex.Append(@"\["); break; }

                    string body = pattern[(i + 1)..close];
                    bool negated = body.StartsWith('^');
                    if (negated) body = body[1..];
                    regex.Append('[').Append(negated ? "^" : "").Append(body.Replace(@"\", @"\\")).Append(']');
                    i = close;
                    break;
                default: regex.Append(EscapeRegex(c.ToString())); break;
            }
        }

        return regex.ToString();
    }

    /// <summary>
    /// Escapes only what RE2 treats specially. .NET's Regex.Escape also escapes spaces and '#',
    /// which RE2 rejects as unknown escapes.
    /// </summary>
    private static string EscapeRegex(string text)
    {
        var escaped = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (@"\.+*?()|[]{}^$".Contains(c)) escaped.Append('\\');
            escaped.Append(c);
        }
        return escaped.ToString();
    }

    private static readonly Regex InsertExecPattern = new(
        @"^INSERT\s+(INTO\s+)?(?<target>[^\s(]+)\s+EXEC(UTE)?\s+(?<proc>[\w\.\[\]""#]+)\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>
    /// INSERT #t EXEC some_proc - T-SQL allows the INTO to be omitted. Only a procedure that takes
    /// no arguments and keeps DuckDB's own column types can be used this way, because the insert is
    /// done by DuckDB from the procedure's query; SMO's INSERT #tmp EXEC xp_msver is the one that
    /// matters.
    /// </summary>
    public static bool TryParseInsertExec(string statement, out string target, out string query)
    {
        target = "";
        query = "";

        var match = InsertExecPattern.Match(statement.Trim());
        if (!match.Success) return false;

        var procedure = Find(match.Groups["proc"].Value);
        if (procedure is null || procedure.ParameterNames.Length > 0 || procedure.Columns is not null) return false;

        target = match.Groups["target"].Value;
        query = procedure.Sql;
        return true;
    }

    /// <summary>
    /// The escape hatch: EXEC sp_duckdb N'...' hands its argument to DuckDB untranslated.
    ///
    /// It is a procedure call rather than a new keyword or a comment pragma for three reasons: it
    /// is valid T-SQL, so a client's own parser and SSMS's editor accept it; it reuses the EXEC
    /// path that already exists; and it is impossible to reach by accident, so a translation bug
    /// can never be mistaken for pass-through or the other way round.
    ///
    /// The argument is returned unevaluated — it may be a literal or a variable — so the caller
    /// decides how to resolve it.
    /// </summary>
    public static bool TryParseDuckDb(string statement, out string argument)
    {
        var match = DuckDbPattern.Match(statement.Trim());
        argument = match.Success ? match.Groups["arg"].Value.Trim() : "";
        return match.Success;
    }

    private static readonly Regex DuckDbPattern = new(
        @"^EXEC(UTE)?\s+(\[?\w+\]?\s*\.\s*)?(\[?dbo\]?\s*\.\s*)?\[?sp_duckdb\]?\s+(?<arg>.+?)\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>
    /// EXEC [@status =] name [args]. The name may be qualified and bracketed; the arguments are
    /// split at the commas that separate them, not the ones inside a string or a parenthesis.
    /// EXEC('...') and EXEC @variable are not calls of a named procedure and do not match.
    /// </summary>
    private static readonly Regex CallPattern = new(
        @"^EXEC(?:UTE)?\s+(?:@\w+\s*=\s*)?" +
        @"(?<name>(?:\[[^\]]+\]|""[^""]+""|[\w#$]+)(?:\s*\.\s*(?:\[[^\]]+\]|""[^""]+""|[\w#$]+)?)*)" +
        @"(?<args>\s.*?)?\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex NamedArgumentPattern = new(
        @"^@(?<name>\w+)\s*=\s*(?<value>.+)$", RegexOptions.Singleline);

    private static readonly Regex OutputSuffix = new(@"\s+OUT(PUT)?$", RegexOptions.IgnoreCase);

    public static bool TryParseCall(string statement, out ProcedureCall call)
    {
        call = new ProcedureCall("", []);

        var match = CallPattern.Match(statement.Trim());
        if (!match.Success) return false;

        string name = match.Groups["name"].Value;
        if (name.Equals("AS", StringComparison.OrdinalIgnoreCase)) return false;   // EXECUTE AS USER = ...

        string args = match.Groups["args"].Value.Trim();

        var arguments = new List<ProcedureArgument>();
        foreach (string piece in SplitArguments(args))
        {
            // An OUTPUT argument is accepted and its value simply not returned: nothing here sets one.
            string text = OutputSuffix.Replace(piece.Trim(), "");

            var named = NamedArgumentPattern.Match(text);
            arguments.Add(named.Success
                ? new ProcedureArgument(named.Groups["name"].Value, named.Groups["value"].Value.Trim())
                : new ProcedureArgument(null, text));
        }

        call = new ProcedureCall(name, arguments);
        return true;
    }

    private static IEnumerable<string> SplitArguments(string text)
    {
        if (text.Length == 0) yield break;

        int depth = 0, start = 0;
        char quote = '\0';

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (quote != '\0')
            {
                if (c == quote)
                {
                    // A doubled quote inside a string is the quote character, not its end.
                    if (quote == '\'' && i + 1 < text.Length && text[i + 1] == '\'') { i++; continue; }
                    quote = '\0';
                }
                continue;
            }

            switch (c)
            {
                case '\'': quote = '\''; break;
                case '"': quote = '"'; break;
                case '[': quote = ']'; break;
                case '(': depth++; break;
                case ')': depth--; break;
                case ',' when depth == 0:
                    yield return text[start..i];
                    start = i + 1;
                    break;
            }
        }

        yield return text[start..];
    }
}
