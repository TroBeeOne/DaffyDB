using System.Text;

namespace TdsProxy.Sql;

internal abstract record TSqlStatement;

/// <summary>
/// Anything that is executed as-is: a SELECT, a DECLARE, a SET, an EXEC. <paramref name="Line"/> is
/// the 1-based line the statement started on, which is all ERROR_LINE() needs.
/// </summary>
internal sealed record SimpleStatement(string Text, int Line = 0) : TSqlStatement;

internal sealed record BlockStatement(IReadOnlyList<TSqlStatement> Statements) : TSqlStatement;

internal sealed record IfStatement(string Condition, TSqlStatement Then, TSqlStatement? Else) : TSqlStatement;

/// <summary>
/// BEGIN TRY ... END TRY BEGIN CATCH ... END CATCH. The CATCH half is optional here only so that a
/// half-written batch parses; T-SQL requires it.
/// </summary>
internal sealed record TryCatchStatement(TSqlStatement Try, TSqlStatement? Catch) : TSqlStatement;

/// <summary>
/// Turns a batch into a statement tree. This is deliberately shallow — it understands statement
/// boundaries, BEGIN/END blocks and IF/ELSE, and treats everything else as opaque text to be
/// translated and handed to DuckDB.
///
/// The hard part is boundaries, because T-SQL does not require semicolons. A newline ends a
/// statement when the next line opens one, with two carve-outs that would otherwise break ordinary
/// queries: a line starting with SELECT only counts when the previous token cannot be continued
/// (so UNION / EXCEPT / INTERSECT and unclosed expressions keep the statement going), and nothing
/// splits inside parentheses.
/// </summary>
internal static class TSqlParser
{
    /// <summary>Keywords that unambiguously open a new statement, even mid-line.</summary>
    private static readonly HashSet<string> HardStatementKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "DECLARE", "SET", "PRINT", "EXEC", "EXECUTE", "IF", "WHILE", "BEGIN",
        "INSERT", "UPDATE", "DELETE", "MERGE", "CREATE", "DROP", "ALTER", "TRUNCATE",
        "USE", "RETURN", "THROW", "RAISERROR",
    };

    /// <summary>
    /// Tokens after which a following SELECT is a continuation of the same statement rather than a
    /// new one.
    /// </summary>
    private static readonly HashSet<string> ContinuationTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "UNION", "EXCEPT", "INTERSECT", "ALL", "AS", "IN", "EXISTS", "AND", "OR", "NOT",
        "THEN", "ELSE", "FROM", "WHERE", "JOIN", "ON", "WITH", "BY", "HAVING", "INTO", "FOR",
    };

    /// <summary>Words that close a construct instead of opening a statement.</summary>
    private static readonly HashSet<string> Terminators = new(StringComparer.OrdinalIgnoreCase) { "ELSE", "END" };

    public static List<TSqlStatement> Parse(string sql)
    {
        var cursor = new Cursor(sql);
        var statements = new List<TSqlStatement>();

        while (true)
        {
            cursor.SkipTrivia();
            if (cursor.AtEnd) break;

            // A stray ELSE/END with nothing to attach to; stop rather than loop forever.
            if (Terminators.Contains(cursor.PeekWord())) break;

            var statement = ParseStatement(cursor);
            if (statement is null) break;
            statements.Add(statement);
        }

        return statements;
    }

    private static TSqlStatement? ParseStatement(Cursor cursor)
    {
        // Stray semicolons first: ";WITH cte AS (...)" is a common habit, and an empty statement
        // used to end the batch, so everything after it was silently not run.
        cursor.SkipSeparators();
        if (cursor.AtEnd) return null;

        string word = cursor.PeekWord();

        if (word.Equals("BEGIN", StringComparison.OrdinalIgnoreCase))
        {
            // BEGIN opens three different things. BEGIN TRANSACTION is a statement of its own,
            // BEGIN TRY opens a guarded block, and a bare BEGIN opens an ordinary one.
            string second = cursor.PeekSecondWord();

            if (second.Equals("TRY", StringComparison.OrdinalIgnoreCase)) return ParseTryCatch(cursor);
            if (!cursor.IsTransactionBegin()) return ParseBlock(cursor);
        }

        if (word.Equals("IF", StringComparison.OrdinalIgnoreCase))
            return ParseIf(cursor);

        int line = cursor.Line;
        string text = cursor.ReadStatementText();
        return text.Length == 0 ? null : new SimpleStatement(text, line);
    }

    /// <summary>
    /// BEGIN TRY ... END TRY [BEGIN CATCH ... END CATCH]. Each half is an ordinary block; the only
    /// thing that makes them special is the qualifier on their END.
    /// </summary>
    private static TSqlStatement ParseTryCatch(Cursor cursor)
    {
        cursor.ConsumeWord();   // BEGIN
        cursor.ConsumeWord();   // TRY
        var guarded = ParseUntilQualifiedEnd(cursor, "TRY");

        cursor.SkipSemicolons();

        TSqlStatement? handler = null;
        if (cursor.PeekWord().Equals("BEGIN", StringComparison.OrdinalIgnoreCase) &&
            cursor.PeekSecondWord().Equals("CATCH", StringComparison.OrdinalIgnoreCase))
        {
            cursor.ConsumeWord();   // BEGIN
            cursor.ConsumeWord();   // CATCH
            handler = ParseUntilQualifiedEnd(cursor, "CATCH");
        }

        return new TryCatchStatement(guarded, handler);
    }

    /// <summary>Reads statements up to and including the "END TRY" / "END CATCH" that closes them.</summary>
    private static TSqlStatement ParseUntilQualifiedEnd(Cursor cursor, string qualifier)
    {
        var statements = new List<TSqlStatement>();

        while (true)
        {
            cursor.SkipTrivia();
            if (cursor.AtEnd) break;

            if (cursor.PeekWord().Equals("END", StringComparison.OrdinalIgnoreCase))
            {
                cursor.ConsumeWord();
                if (cursor.PeekWord().Equals(qualifier, StringComparison.OrdinalIgnoreCase)) cursor.ConsumeWord();
                break;
            }

            var statement = ParseStatement(cursor);
            if (statement is null) break;
            statements.Add(statement);
        }

        return new BlockStatement(statements);
    }

    private static TSqlStatement ParseBlock(Cursor cursor)
    {
        cursor.ConsumeWord();   // BEGIN
        var statements = new List<TSqlStatement>();

        while (true)
        {
            cursor.SkipTrivia();
            if (cursor.AtEnd) break;

            if (cursor.PeekWord().Equals("END", StringComparison.OrdinalIgnoreCase))
            {
                cursor.ConsumeWord();
                break;
            }

            var statement = ParseStatement(cursor);
            if (statement is null) break;
            statements.Add(statement);
        }

        return new BlockStatement(statements);
    }

    private static TSqlStatement ParseIf(Cursor cursor)
    {
        cursor.ConsumeWord();   // IF

        string condition = cursor.ReadConditionText();
        var thenBranch = ParseStatement(cursor) ?? new SimpleStatement("");

        cursor.SkipTrivia();
        cursor.SkipSemicolons();
        cursor.SkipTrivia();

        TSqlStatement? elseBranch = null;
        if (cursor.PeekWord().Equals("ELSE", StringComparison.OrdinalIgnoreCase))
        {
            cursor.ConsumeWord();
            elseBranch = ParseStatement(cursor);
        }

        return new IfStatement(condition, thenBranch, elseBranch);
    }

    /// <summary>Character-level scanner that knows about literals, comments and nesting.</summary>
    private sealed class Cursor(string text)
    {
        private int _position;

        public bool AtEnd => _position >= text.Length;

        public void SkipTrivia()
        {
            while (_position < text.Length)
            {
                if (char.IsWhiteSpace(text[_position])) { _position++; continue; }

                if (text[_position] == '-' && _position + 1 < text.Length && text[_position + 1] == '-')
                {
                    while (_position < text.Length && text[_position] != '\n') _position++;
                    continue;
                }

                if (text[_position] == '/' && _position + 1 < text.Length && text[_position + 1] == '*')
                {
                    _position += 2;
                    while (_position + 1 < text.Length && !(text[_position] == '*' && text[_position + 1] == '/')) _position++;
                    _position = Math.Min(_position + 2, text.Length);
                    continue;
                }

                break;
            }
        }

        public void SkipSemicolons()
        {
            while (_position < text.Length && (text[_position] == ';' || char.IsWhiteSpace(text[_position]))) _position++;
        }

        /// <summary>Whitespace, comments and semicolons, in any order.</summary>
        public void SkipSeparators()
        {
            while (true)
            {
                SkipTrivia();
                if (_position >= text.Length || text[_position] != ';') return;
                _position++;
            }
        }

        public string PeekWord()
        {
            SkipTrivia();
            int end = _position;
            while (end < text.Length && (char.IsLetter(text[end]) || text[end] == '_')) end++;
            return text[_position..end];
        }

        public void ConsumeWord()
        {
            SkipTrivia();
            while (_position < text.Length && (char.IsLetter(text[_position]) || text[_position] == '_')) _position++;
        }

        /// <summary>The word after the one at the cursor, without moving it.</summary>
        public string PeekSecondWord()
        {
            int saved = _position;
            ConsumeWord();
            string next = PeekWord();
            _position = saved;
            return next;
        }

        /// <summary>The 1-based line the cursor is on, which is what ERROR_LINE() reports.</summary>
        public int Line
        {
            get
            {
                int line = 1;
                for (int i = 0; i < _position && i < text.Length; i++)
                {
                    if (text[i] == '\n') line++;
                }
                return line;
            }
        }

        /// <summary>BEGIN TRANSACTION is not a block, so it must not be parsed as one.</summary>
        public bool IsTransactionBegin()
        {
            string next = PeekSecondWord();
            return next.Equals("TRANSACTION", StringComparison.OrdinalIgnoreCase)
                || next.Equals("TRAN", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Reads an IF condition: everything up to the keyword that starts the branch statement.
        /// A SELECT nested in parentheses (EXISTS (SELECT ...)) stays part of the condition.
        /// </summary>
        public string ReadConditionText()
        {
            var builder = new StringBuilder();
            int depth = 0;

            while (_position < text.Length)
            {
                if (CopyLiteralOrComment(builder)) continue;

                char c = text[_position];

                if (char.IsLetter(c) || c == '_')
                {
                    int start = _position;
                    string word = ReadWordRaw();
                    if (depth == 0 && (HardStatementKeywords.Contains(word) || word.Equals("SELECT", StringComparison.OrdinalIgnoreCase)))
                    {
                        _position = start;
                        break;
                    }
                    builder.Append(word);
                    continue;
                }

                if (c == '(') depth++;
                else if (c == ')') depth--;

                builder.Append(c);
                _position++;
            }

            return builder.ToString().Trim();
        }

        /// <summary>Reads one plain statement, stopping at its boundary without consuming the next.</summary>
        public string ReadStatementText()
        {
            var builder = new StringBuilder();
            int depth = 0;
            string lastWord = "";

            // An INSERT or a CTE is not finished until its own SELECT (or VALUES) has been seen at
            // the top level. Until then a line starting with SELECT belongs to this statement, not
            // to a new one.
            bool sawBody = false;

            while (_position < text.Length)
            {
                if (CopyLiteralOrComment(builder)) continue;

                char c = text[_position];

                if (depth == 0 && c == ';')
                {
                    _position++;
                    break;
                }

                if (char.IsLetter(c) || c == '_')
                {
                    int start = _position;
                    string word = ReadWordRaw();

                    // A SELECT can open the next statement mid-line, as it can at the start of one:
                    // "set implicit_transactions off select USER_NAME() select ..." is three, sent
                    // on one line by the ODBC driver for a linked server.
                    if (depth == 0 && word.Equals("SELECT", StringComparison.OrdinalIgnoreCase) &&
                        start > 0 && char.IsWhiteSpace(text[start - 1]) &&
                        SelectStartsStatement(lastWord, builder, sawBody))
                    {
                        _position = start;
                        break;
                    }

                    // EXEC counts too: "INSERT #t EXEC some_proc" has no SELECT of its own.
                    if (depth == 0 && builder.ToString().Trim().Length > 0 &&
                        (word.Equals("SELECT", StringComparison.OrdinalIgnoreCase) ||
                         word.Equals("VALUES", StringComparison.OrdinalIgnoreCase) ||
                         word.Equals("EXEC", StringComparison.OrdinalIgnoreCase) ||
                         word.Equals("EXECUTE", StringComparison.OrdinalIgnoreCase)))
                    {
                        sawBody = true;
                    }

                    if (depth == 0 && Terminators.Contains(word) && builder.ToString().Trim().Length > 0)
                    {
                        // ELSE/END belong to the enclosing construct, and a CASE keeps its own END.
                        if (!(word.Equals("END", StringComparison.OrdinalIgnoreCase) && CaseIsOpen(builder.ToString())) &&
                            !(word.Equals("ELSE", StringComparison.OrdinalIgnoreCase) && CaseIsOpen(builder.ToString())))
                        {
                            _position = start;
                            break;
                        }
                    }

                    builder.Append(word);
                    lastWord = word;
                    continue;
                }

                if (c == '(') depth++;
                else if (c == ')') depth--;

                if (depth == 0 && c == '\n' && StartsNewStatement(lastWord, builder, sawBody)) break;

                builder.Append(c);
                _position++;
            }

            return builder.ToString().Trim();
        }

        /// <summary>True when the text after the current newline begins a statement of its own.</summary>
        private bool StartsNewStatement(string lastWord, StringBuilder builder, bool sawBody)
        {
            int probe = _position + 1;
            while (probe < text.Length && char.IsWhiteSpace(text[probe])) probe++;

            int end = probe;
            while (end < text.Length && (char.IsLetter(text[end]) || text[end] == '_')) end++;
            if (end == probe) return false;

            string next = text[probe..end];
            if (HardStatementKeywords.Contains(next)) return true;
            if (!next.Equals("SELECT", StringComparison.OrdinalIgnoreCase)) return false;

            return SelectStartsStatement(lastWord, builder, sawBody);
        }

        /// <summary>True when a SELECT here, at the top level, opens a statement of its own.</summary>
        private static bool SelectStartsStatement(string lastWord, StringBuilder builder, bool sawBody)
        {
            string sofar = builder.ToString().TrimStart();
            if (sofar.Length == 0) return false;

            // A SET is never continued by a SELECT outside parentheses. SET NOCOUNT ON ends at its
            // ON, which is not a join's, and the SELECT after it - on the same line or the next -
            // is a statement of its own; it used to vanish into the SET.
            if (sofar.StartsWith("SET", StringComparison.OrdinalIgnoreCase) &&
                (sofar.Length == 3 || !char.IsLetterOrDigit(sofar[3]) && sofar[3] != '_'))
                return true;

            // "INSERT INTO t" and "WITH cte AS (...)" are both incomplete until their own SELECT
            // arrives, so a SELECT after them finishes them rather than starting something.
            bool needsBody = sofar.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
                          || sofar.StartsWith("WITH", StringComparison.OrdinalIgnoreCase);
            if (needsBody && !sawBody) return false;

            // Otherwise a SELECT continues the statement only when the previous token demands
            // something after it, which is what makes UNION and subqueries work.
            return !ContinuationTokens.Contains(lastWord);
        }

        private static bool CaseIsOpen(string sofar)
        {
            int open = CountWord(sofar, "CASE");
            int close = CountWord(sofar, "END");
            return open > close;
        }

        private static int CountWord(string text, string word)
        {
            int count = 0, index = 0;
            while ((index = text.IndexOf(word, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                bool leftOk = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
                int after = index + word.Length;
                bool rightOk = after >= text.Length || !char.IsLetterOrDigit(text[after]);
                if (leftOk && rightOk) count++;
                index = after;
            }
            return count;
        }

        private string ReadWordRaw()
        {
            int start = _position;
            while (_position < text.Length && (char.IsLetterOrDigit(text[_position]) || text[_position] == '_')) _position++;
            return text[start.._position];
        }

        /// <summary>Copies a string literal, quoted identifier or comment verbatim.</summary>
        private bool CopyLiteralOrComment(StringBuilder builder)
        {
            if (_position >= text.Length) return false;
            char c = text[_position];

            if (c is '\'' or '"' or '[')
            {
                char close = c == '[' ? ']' : c;
                builder.Append(text[_position++]);

                while (_position < text.Length)
                {
                    if (text[_position] == close)
                    {
                        if (_position + 1 < text.Length && text[_position + 1] == close)
                        {
                            builder.Append(text[_position]).Append(text[_position + 1]);
                            _position += 2;
                            continue;
                        }
                        builder.Append(text[_position++]);
                        break;
                    }
                    builder.Append(text[_position++]);
                }
                return true;
            }

            if (c == '-' && _position + 1 < text.Length && text[_position + 1] == '-')
            {
                while (_position < text.Length && text[_position] != '\n') builder.Append(text[_position++]);
                return true;
            }

            if (c == '/' && _position + 1 < text.Length && text[_position + 1] == '*')
            {
                builder.Append(text[_position++]).Append(text[_position++]);
                while (_position + 1 < text.Length && !(text[_position] == '*' && text[_position + 1] == '/'))
                    builder.Append(text[_position++]);
                if (_position + 1 < text.Length) { builder.Append(text[_position++]).Append(text[_position++]); }
                return true;
            }

            return false;
        }
    }
}
