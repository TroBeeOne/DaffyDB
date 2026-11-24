using System.Globalization;
using System.Text;
using TdsProxy.Query;

namespace TdsProxy.Repl;

/// <summary>
/// Renders result sets as text, a page at a time.
///
/// Two things make a console grid unreadable and both are guarded here: too many rows, which
/// scroll the useful part off the top, and too many columns, which wrap into mush. Rows are paged;
/// columns are fitted to the terminal and the overflow is reported rather than silently wrapped,
/// with <c>:wide</c> as the way to see everything on a table too broad to line up.
///
/// There is deliberately no "show all" and no export. A fifty-thousand row dump helps nobody, and
/// anything that wants the whole set should connect a real client to the listener, which is what
/// the listener is for.
/// </summary>
internal sealed class ResultPrinter(TextWriter output)
{
    /// <summary>No column is allowed to hog the line, however long its widest value.</summary>
    private const int MaxColumnWidth = 40;

    /// <summary>Below this a column shows nothing useful, so drop it rather than shrink it further.</summary>
    private const int MinColumnWidth = 6;

    private const string Indent = "  ";

    public int PageSize { get; set; } = 20;

    public int MaxWidth { get; set; } = 120;

    public bool Wide { get; set; }

    /// <summary>
    /// Waits for the reader and returns true to carry on. Null means nobody is watching — piped
    /// input, a redirected console — in which case the first page is printed and that is that.
    /// </summary>
    public Func<bool>? Pager { get; set; }

    public void Print(IReadOnlyList<ResultSet> sets)
    {
        for (int i = 0; i < sets.Count; i++)
        {
            if (sets.Count > 1)
            {
                output.WriteLine();
                output.WriteLine($"-- result set {i + 1} of {sets.Count} --");
            }

            if (PrintOne(sets[i])) continue;

            // Stopping means stopping: q returns to the prompt rather than skipping ahead to the
            // next grid, so there is one rule to remember instead of two.
            int remaining = sets.Count - i - 1;
            if (remaining > 0) output.WriteLine($"  {remaining} further result set(s) not shown");
            return;
        }
    }

    /// <summary>True if the whole set was shown; false if the reader stopped part way.</summary>
    private bool PrintOne(ResultSet set)
    {
        if (set.Columns.Count == 0 || set.Rows.Count == 0)
        {
            output.WriteLine("  (no rows)");
            return true;
        }

        return Wide ? PrintRecords(set) : PrintGrid(set);
    }

    private bool PrintGrid(ResultSet set)
    {
        var layout = Measure(set, Math.Min(PageSize, set.Rows.Count));
        int shown = 0;

        while (true)
        {
            int take = Math.Min(PageSize, set.Rows.Count - shown);

            output.WriteLine();
            WriteRow(layout, [.. set.Columns.Take(layout.Visible).Select(c => c.Name)], set);
            output.WriteLine(Indent + string.Join("  ", layout.Widths.Select(w => new string('-', w))));

            for (int i = 0; i < take; i++) WriteDataRow(set, shown + i, layout);
            shown += take;

            if (layout.Visible < set.Columns.Count)
            {
                output.WriteLine($"  ({set.Columns.Count - layout.Visible} more column(s) not shown - :wide to see all)");
            }

            if (shown >= set.Rows.Count)
            {
                output.WriteLine($"  {set.Rows.Count} row(s)");
                return true;
            }

            if (!ContinuePaging(shown - take + 1, shown, set.Rows.Count)) return false;
        }
    }

    /// <summary>One row per record, for tables too wide to line up in a grid.</summary>
    private bool PrintRecords(ResultSet set)
    {
        int labelWidth = set.Columns.Max(c => c.Name.Length);
        int perPage = Math.Max(1, PageSize / (set.Columns.Count + 1));
        int shown = 0;

        while (true)
        {
            int take = Math.Min(perPage, set.Rows.Count - shown);

            for (int i = 0; i < take; i++)
            {
                int index = shown + i;
                output.WriteLine();
                output.WriteLine($"-[ row {index + 1} ]" + new string('-', Math.Max(4, labelWidth)));
                for (int c = 0; c < set.Columns.Count; c++)
                {
                    string value = Render(set.Rows[index][c], set.Columns[c].Type);
                    output.WriteLine($"{set.Columns[c].Name.PadRight(labelWidth)} | {value}");
                }
            }

            shown += take;

            if (shown >= set.Rows.Count)
            {
                output.WriteLine();
                output.WriteLine($"  {set.Rows.Count} row(s)");
                return true;
            }

            if (!ContinuePaging(shown - take + 1, shown, set.Rows.Count)) return false;
        }
    }

    /// <summary>
    /// The pause between pages. Returns false when the reader stopped, or when there is no reader
    /// at all: with input piped there is no key to press, so offering one would be a lie and the
    /// footer says plainly what was shown instead.
    /// </summary>
    private bool ContinuePaging(int from, int to, int total)
    {
        output.WriteLine();

        if (Pager is null)
        {
            output.WriteLine($"  showing {to} of {total} rows" +
                             " - add TOP n to your query to trim it at the source");
            return false;
        }

        output.WriteLine($"  rows {from}-{to} of {total}" +
                         "       [space] next page       [q] stop, back to prompt");

        if (Pager()) return true;

        output.WriteLine($"  stopped at row {to} of {total}" +
                         " - add TOP n to your query to trim it at the source");
        return false;
    }

    private void WriteDataRow(ResultSet set, int rowIndex, Layout layout)
    {
        var cells = new string[layout.Visible];
        for (int c = 0; c < layout.Visible; c++)
            cells[c] = Render(set.Rows[rowIndex][c], set.Columns[c].Type);

        WriteRow(layout, cells, set);
    }

    private void WriteRow(Layout layout, string[] cells, ResultSet set)
    {
        var line = new StringBuilder(Indent);
        for (int c = 0; c < layout.Visible; c++)
        {
            if (c > 0) line.Append("  ");
            string cell = Fit(cells[c], layout.Widths[c]);

            // Numbers right-align so magnitudes line up; a header sits over its own data.
            bool right = IsNumeric(set.Columns[c].Type);
            line.Append(right ? cell.PadLeft(layout.Widths[c]) : cell.PadRight(layout.Widths[c]));
        }

        output.WriteLine(line.ToString().TrimEnd());
    }

    /// <summary>
    /// Column widths from the first page only, then reused for the rest. Measuring every row would
    /// mean formatting fifty thousand of them to print twenty, and re-measuring per page would make
    /// the columns jitter as you page through.
    /// </summary>
    private Layout Measure(ResultSet set, int sampleRows)
    {
        int n = set.Columns.Count;
        var widths = new int[n];

        for (int c = 0; c < n; c++)
        {
            int width = set.Columns[c].Name.Length;
            for (int r = 0; r < sampleRows; r++)
                width = Math.Max(width, Render(set.Rows[r][c], set.Columns[c].Type).Length);

            widths[c] = Math.Clamp(width, 1, MaxColumnWidth);
        }

        int visible = n;
        while (visible > 1 && Total(widths, visible) > MaxWidth)
        {
            // Squeeze the widest column first; when nothing is left to squeeze, drop a column.
            int widest = 0;
            for (int c = 1; c < visible; c++) if (widths[c] > widths[widest]) widest = c;

            if (widths[widest] > MinColumnWidth) widths[widest]--;
            else visible--;
        }

        return new Layout(widths.Take(visible).ToArray(), visible);
    }

    private static int Total(int[] widths, int visible) =>
        Indent.Length + widths.Take(visible).Sum() + (2 * (visible - 1));

    private static string Fit(string value, int width)
    {
        if (value.Length <= width) return value;
        return width > 4 ? string.Concat(value.AsSpan(0, width - 3), "...") : value[..width];
    }

    private static bool IsNumeric(SqlColumnType type) =>
        type is SqlColumnType.Int or SqlColumnType.BigInt or SqlColumnType.Double or SqlColumnType.Decimal
             or SqlColumnType.TinyInt or SqlColumnType.SmallInt or SqlColumnType.Real;

    /// <summary>
    /// Newlines and tabs are flattened: one value spilling onto a second line destroys the
    /// alignment of everything under it, and a grid that cannot be read is worse than a truncated
    /// one. <c>:wide</c> is the way to see a value in full.
    /// </summary>
    public static string Render(object? value, SqlColumnType type) => value switch
    {
        null => "NULL",
        bool b => b ? "1" : "0",
        DateTime d => type == SqlColumnType.Date
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        double x => x.ToString(CultureInfo.InvariantCulture),
        float f => f.ToString(CultureInfo.InvariantCulture),
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        TimeSpan t => t.ToString("c", CultureInfo.InvariantCulture),
        Guid g => g.ToString("D").ToUpperInvariant(),
        _ => Flatten(value.ToString() ?? string.Empty),
    };

    private static string Flatten(string text) =>
        text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');

    private sealed record Layout(int[] Widths, int Visible);
}
