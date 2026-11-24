using System.Text;

namespace DaffySQL.Client;

/// <summary>
/// T-SQL object names as daffy_scan and daffy_columns are given them: parsed, then quoted - never
/// pasted into T-SQL. Lifted from DaffyDuo's TransferNames, which does the same for daffy.transfer.
/// </summary>
internal static class SqlNames
{
    public static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

    /// <summary>
    /// A table or view, quoted: one, two or three parts (Customers, dbo.Customers, Sales.dbo.Customers,
    /// [My Table], Sales..Customers). Not a #temp table: the session it was made on is reset before
    /// anyone else uses it.
    /// </summary>
    public static string Table(string name)
    {
        var parts = Split(name) ?? throw Bad($"'{name}' is not a table name");
        if (parts.Count > 3) throw Bad($"'{name}' has {parts.Count} parts; name a table as table, schema.table or database.schema.table");
        if (parts[^1].Length == 0 || parts.Count == 2 && parts[0].Length == 0 || parts.Count == 3 && parts[0].Length == 0)
            throw Bad($"'{name}' is not a table name");
        if (parts[^1].StartsWith('#'))
            throw Bad($"'{name}' is a temp table, which does not outlast the query that made it here: sessions are reset as they go " +
                      "back to the pool. Use daffy_query, with the T-SQL that fills it, in one batch");
        return string.Join(".", parts.Select(p => p.Length == 0 ? "" : Quote(p)));
    }

    /// <summary>
    /// The parts of a dotted name: [bracketed] and "quoted" parts may hold anything (]] and "" for
    /// the closing character itself); a plain part may not hold spaces, quotes or punctuation.
    /// Null when it is not a name at all.
    /// </summary>
    internal static List<string>? Split(string name)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        bool delimited = false;
        int i = 0;
        string text = name.Trim();

        while (i < text.Length)
        {
            char c = text[i];
            if (c is '[' or '"')
            {
                if (current.Length > 0 || delimited) return null;
                char close = c == '[' ? ']' : '"';
                i++;
                while (true)
                {
                    if (i >= text.Length) return null;
                    if (text[i] == close)
                    {
                        if (i + 1 < text.Length && text[i + 1] == close) { current.Append(close); i += 2; continue; }
                        i++;
                        break;
                    }
                    current.Append(text[i++]);
                }
                delimited = true;
            }
            else if (c == '.')
            {
                parts.Add(current.ToString());
                current.Clear();
                delimited = false;
                i++;
            }
            else if (char.IsWhiteSpace(c) || c is ']' or ';' or '\'' or '(' or ')' or ',')
            {
                return null;
            }
            else
            {
                if (delimited) return null;
                current.Append(c);
                i++;
            }
        }

        parts.Add(current.ToString());
        return parts;
    }

    /// <summary>
    /// daffy_scan's filter, if it is one T-SQL condition and no more. It is sent inside the
    /// SELECT's WHERE (...), so the only way out of the condition is to close that parenthesis,
    /// or to end the statement, or to comment the rest away - and each is refused, wherever it is
    /// outside a 'string', a [name] or a "name". Inside the parentheses T-SQL has expressions and
    /// nothing else: no second statement can start there. What an expression can reach - a
    /// subquery of another table - is the login's to allow or not.
    /// </summary>
    public static string Condition(string filter)
    {
        int depth = 0;
        for (int i = 0; i < filter.Length; i++)
        {
            char c = filter[i];
            switch (c)
            {
                case '\'' or '[' or '"':
                    char close = c == '[' ? ']' : c;
                    i = Closing(filter, i, close) ?? throw NotOne(filter, c == '\'' ? "a string is not closed" : "a name is not closed");
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    if (--depth < 0) throw NotOne(filter, "it closes a parenthesis it did not open");
                    break;
                case ';':
                    throw NotOne(filter, "it has a ; in it");
                case '-' when i + 1 < filter.Length && filter[i + 1] == '-':
                case '/' when i + 1 < filter.Length && filter[i + 1] == '*':
                    throw NotOne(filter, "it has a comment in it");
            }
        }

        if (depth > 0) throw NotOne(filter, "it leaves a parenthesis open");
        return filter;
    }

    /// <summary>Where a 'string', [name] or "name" opened at <paramref name="open"/> ends, its closing character doubled inside it; null when it does not.</summary>
    private static int? Closing(string text, int open, char close)
    {
        for (int i = open + 1; i < text.Length; i++)
        {
            if (text[i] != close) continue;
            if (i + 1 < text.Length && text[i + 1] == close) { i++; continue; }
            return i;
        }
        return null;
    }

    private static ArgumentException NotOne(string filter, string why) =>
        new($"filter is one T-SQL condition, sent as the WHERE of the SELECT, and '{filter}' is not: {why}");

    private static ArgumentException Bad(string message) => new(message);
}
