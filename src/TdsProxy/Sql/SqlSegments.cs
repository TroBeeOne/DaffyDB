using System.Text;

namespace TdsProxy.Sql;

internal enum SegmentKind
{
    Code,
    StringLiteral,
    QuotedIdentifier,
    BracketIdentifier,
    Comment,
}

internal readonly record struct SqlSegment(SegmentKind Kind, string Text);

/// <summary>
/// Splits SQL into code and non-code runs so that rewriting never corrupts a string literal,
/// comment or quoted identifier. Every rule in <see cref="TSqlTranslator"/> depends on this: a
/// regex for ISNULL( would otherwise happily rewrite the inside of '...ISNULL(...' .
/// </summary>
internal static class SqlSegments
{
    public static List<SqlSegment> Split(string sql)
    {
        var segments = new List<SqlSegment>();
        var code = new StringBuilder();
        int i = 0;

        void FlushCode()
        {
            if (code.Length == 0) return;
            segments.Add(new SqlSegment(SegmentKind.Code, code.ToString()));
            code.Clear();
        }

        while (i < sql.Length)
        {
            char c = sql[i];

            // N'...' is a T-SQL unicode literal; DuckDB has no such prefix, so drop the N here
            // rather than trying to spot it later when it is no longer adjacent to its literal.
            if (c == '\'')
            {
                TrimUnicodePrefix(code);
                FlushCode();
                segments.Add(new SqlSegment(SegmentKind.StringLiteral, ReadDelimited(sql, ref i, '\'', '\'')));
                continue;
            }

            if (c == '"')
            {
                FlushCode();
                segments.Add(new SqlSegment(SegmentKind.QuotedIdentifier, ReadDelimited(sql, ref i, '"', '"')));
                continue;
            }

            if (c == '[')
            {
                FlushCode();
                segments.Add(new SqlSegment(SegmentKind.BracketIdentifier, ReadDelimited(sql, ref i, '[', ']')));
                continue;
            }

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                FlushCode();
                int start = i;
                while (i < sql.Length && sql[i] != '\n') i++;
                segments.Add(new SqlSegment(SegmentKind.Comment, sql[start..i]));
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                FlushCode();
                int start = i;
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/')) i++;
                i = Math.Min(i + 2, sql.Length);
                segments.Add(new SqlSegment(SegmentKind.Comment, sql[start..i]));
                continue;
            }

            code.Append(c);
            i++;
        }

        FlushCode();
        return segments;
    }

    private static readonly string[] StatementKeywords =
    [
        "DECLARE", "SET", "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "PRINT",
        "EXEC", "EXECUTE", "IF", "WHILE", "BEGIN", "WITH", "CREATE", "DROP", "ALTER", "TRUNCATE",
    ];

    /// <summary>
    /// Splits a batch into statements.
    ///
    /// Semicolons are the reliable separator, but T-SQL does not require them, and the batches that
    /// matter here look like "DECLARE @x int" / "SET @x = 1" / "SELECT ..." on separate lines. So a
    /// newline also ends a statement when what has been read so far is a DECLARE or a SET of a
    /// variable and the next line opens a new statement. Splitting on a bare newline generally is
    /// not safe: it would break a multi-line SELECT, or a UNION whose second arm starts a line.
    /// </summary>
    public static List<string> SplitStatements(string sql)
    {
        var statements = new List<string>();
        var current = new StringBuilder();
        int depth = 0;
        int i = 0;

        void Flush()
        {
            string text = current.ToString().Trim();
            if (text.Length > 0) statements.Add(text);
            current.Clear();
        }

        while (i < sql.Length)
        {
            char c = sql[i];

            if (c == '\'') { current.Append(ReadDelimited(sql, ref i, '\'', '\'')); continue; }
            if (c == '"') { current.Append(ReadDelimited(sql, ref i, '"', '"')); continue; }
            if (c == '[') { current.Append(ReadDelimited(sql, ref i, '[', ']')); continue; }

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') current.Append(sql[i++]);
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                int start = i;
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/')) i++;
                i = Math.Min(i + 2, sql.Length);
                current.Append(sql[start..i]);
                continue;
            }

            if (c == '(') depth++;
            else if (c == ')') depth--;

            if (depth == 0 && c == ';')
            {
                Flush();
                i++;
                continue;
            }

            if (depth == 0 && c == '\n' && IsAssignment(current) && OpensStatement(sql, i + 1))
            {
                Flush();
                i++;
                continue;
            }

            current.Append(c);
            i++;
        }

        Flush();
        return statements;
    }

    /// <summary>True for DECLARE and for SET of a variable (not SET NOCOUNT ON and friends).</summary>
    private static bool IsAssignment(StringBuilder current)
    {
        string text = current.ToString().TrimStart();
        if (text.StartsWith("DECLARE", StringComparison.OrdinalIgnoreCase)) return true;

        if (!text.StartsWith("SET", StringComparison.OrdinalIgnoreCase)) return false;
        string rest = text[3..].TrimStart();
        return rest.StartsWith('@');
    }

    private static bool OpensStatement(string sql, int position)
    {
        while (position < sql.Length && char.IsWhiteSpace(sql[position])) position++;

        int end = position;
        while (end < sql.Length && char.IsLetter(sql[end])) end++;
        if (end == position) return false;

        string word = sql[position..end];
        return StatementKeywords.Contains(word, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Reads a delimited run including both delimiters, honouring the doubled-delimiter escape.</summary>
    private static string ReadDelimited(string sql, ref int i, char open, char close)
    {
        var text = new StringBuilder();
        text.Append(sql[i++]);   // opening delimiter

        while (i < sql.Length)
        {
            char c = sql[i];
            if (c == close)
            {
                if (i + 1 < sql.Length && sql[i + 1] == close)
                {
                    text.Append(c).Append(close);
                    i += 2;
                    continue;
                }
                text.Append(c);
                i++;
                return text.ToString();
            }
            text.Append(c);
            i++;
        }

        return text.ToString();   // unterminated; let the engine complain about it
    }

    private static void TrimUnicodePrefix(StringBuilder code)
    {
        if (code.Length == 0 || (code[^1] != 'N' && code[^1] != 'n')) return;
        if (code.Length >= 2 && (char.IsLetterOrDigit(code[^2]) || code[^2] == '_')) return;  // part of a longer word
        code.Length--;
    }

    /// <summary>Unescapes a [bracketed] or "quoted" identifier into its bare name.</summary>
    public static string IdentifierName(string delimited, char open, char close) =>
        delimited[1..^1].Replace(new string(close, 2), close.ToString());
}
