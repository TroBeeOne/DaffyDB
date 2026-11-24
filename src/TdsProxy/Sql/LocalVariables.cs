using System.Text.RegularExpressions;

namespace TdsProxy.Sql;

/// <summary>One variable introduced by a DECLARE, with its declared type and optional initialiser.</summary>
internal sealed record VariableDeclaration(string Name, string? Type, string? InitialExpression);

/// <summary>
/// A recognised "SELECT [TOP n] @a = expr, ... [FROM ...]".
///
/// <paramref name="Prefix"/> holds whatever stood between SELECT and the first assignment —
/// TOP and DISTINCT are both legal there and both change which row is assigned, so the prefix is
/// carried through to the query that actually runs rather than dropped.
/// </summary>
internal sealed record SelectAssignment(
    string Prefix,
    IReadOnlyList<(string Name, string Expression)> Assignments,
    string Tail);

/// <summary>
/// Recognises the two T-SQL statements that introduce and assign local variables. Both are handled
/// by the proxy rather than passed to DuckDB, which has no notion of @-prefixed batch variables.
/// </summary>
internal static class LocalVariables
{
    private static readonly Regex DeclarePattern =
        new(@"^DECLARE\s+(?<body>.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex SetPattern =
        new(@"^SET\s+(?<name>@\w+)\s*=\s*(?<value>.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>DECLARE @a int, @b nvarchar(50) = 'x'</summary>
    public static bool TryParseDeclare(string statement, out List<VariableDeclaration> declarations)
    {
        declarations = [];

        var match = DeclarePattern.Match(statement.Trim());
        if (!match.Success) return false;

        // A cursor or table variable declaration is not a scalar; leave those to fail loudly.
        string body = match.Groups["body"].Value;
        if (Regex.IsMatch(body, @"\b(CURSOR|TABLE)\b", RegexOptions.IgnoreCase)) return false;

        foreach (string item in SplitTopLevel(body))
        {
            var parts = Regex.Match(item.Trim(),
                @"^(?<name>@\w+)\s*(?<type>[A-Za-z_]\w*(\s*\([^)]*\))?)?\s*(=\s*(?<init>.+))?$",
                RegexOptions.Singleline);

            if (!parts.Success) return false;

            declarations.Add(new VariableDeclaration(
                parts.Groups["name"].Value,
                parts.Groups["type"].Success ? parts.Groups["type"].Value.Trim() : null,
                parts.Groups["init"].Success ? parts.Groups["init"].Value.Trim() : null));
        }

        return declarations.Count > 0;
    }

    private static readonly Regex ExecPattern =
        new(@"^EXEC(UTE)?\s*\((?<body>.+)\)\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>EXEC('some sql') — the argument is an expression yielding the batch to run.</summary>
    public static bool TryParseExec(string statement, out string expression)
    {
        var match = ExecPattern.Match(statement.Trim());
        expression = match.Success ? match.Groups["body"].Value.Trim() : "";
        return match.Success;
    }

    /// <summary>SET @a = expression. Returns false for SET NOCOUNT ON and other session options.</summary>
    public static bool TryParseSet(string statement, out string name, out string expression)
    {
        var match = SetPattern.Match(statement.Trim());
        name = match.Success ? match.Groups["name"].Value : "";
        expression = match.Success ? match.Groups["value"].Value.Trim() : "";
        return match.Success;
    }

    /// <summary>
    /// Anything T-SQL allows between SELECT and the select list: TOP, and the DISTINCT/ALL
    /// quantifier. Both are optional, so this always matches — possibly with an empty prefix.
    /// </summary>
    private static readonly Regex SelectPrefixPattern = new(
        @"^\s*(?<prefix>(?:(?:ALL|DISTINCT)\s+)?(?:TOP\s*(?:\([^)]*\)|\d+)(?:\s+PERCENT)?(?:\s+WITH\s+TIES)?\s*)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// SELECT @a = expr, @b = expr [FROM ...] — T-SQL's other assignment form, which tools use to
    /// pull a value out of the catalog. Recognised only when every item in the select list is an
    /// assignment, since "SELECT @a = 1, name FROM t" is a result set, not an assignment.
    /// </summary>
    public static bool TryParseSelectAssignment(string statement, out SelectAssignment? assignment)
    {
        assignment = null;

        string trimmed = statement.TrimStart();
        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) return false;
        if (trimmed.Length > 6 && (char.IsLetterOrDigit(trimmed[6]) || trimmed[6] == '_')) return false;

        string body = trimmed[6..];

        // "SELECT TOP 1 @name = ..." is an assignment just as much as "SELECT @name = ...", so the
        // prefix has to come off before the select list can be recognised.
        string prefix = SelectPrefixPattern.Match(body).Groups["prefix"].Value;
        body = body[prefix.Length..];

        int from = FindTopLevelFrom(body);
        string list = from < 0 ? body : body[..from];
        string tail = from < 0 ? "" : body[from..];

        var assignments = new List<(string Name, string Expression)>();
        foreach (string item in SplitTopLevel(list))
        {
            var match = Regex.Match(item.Trim(), @"^(?<name>@\w+)\s*=\s*(?<value>.+)$", RegexOptions.Singleline);
            if (!match.Success) return false;

            assignments.Add((match.Groups["name"].Value, match.Groups["value"].Value.Trim()));
        }

        if (assignments.Count == 0) return false;

        assignment = new SelectAssignment(prefix.Trim(), assignments, tail);
        return true;
    }

    /// <summary>Finds the FROM that belongs to this select, skipping literals and subqueries.</summary>
    private static int FindTopLevelFrom(string text)
    {
        int depth = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c is '\'' or '"' or '[')
            {
                char close = c == '[' ? ']' : c;
                i++;
                while (i < text.Length && text[i] != close) i++;
                continue;
            }

            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (depth == 0 && (c == 'F' || c == 'f') &&
                     i + 4 <= text.Length && text.Substring(i, 4).Equals("FROM", StringComparison.OrdinalIgnoreCase) &&
                     (i == 0 || !char.IsLetterOrDigit(text[i - 1])) &&
                     (i + 4 >= text.Length || !char.IsLetterOrDigit(text[i + 4])))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Splits a comma-separated list, ignoring commas inside parentheses such as decimal(18,2).</summary>
    private static List<string> SplitTopLevel(string text)
    {
        var items = new List<string>();
        int depth = 0, start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')') depth--;
            else if (text[i] == ',' && depth == 0)
            {
                items.Add(text[start..i]);
                start = i + 1;
            }
        }

        items.Add(text[start..]);
        return items;
    }
}
