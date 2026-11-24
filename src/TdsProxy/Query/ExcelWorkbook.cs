using System.Globalization;
using DuckDB.NET.Data;
using Sylvan.Data.Excel;

namespace TdsProxy.Query;

/// <summary>
/// Loads the sheets of an Excel workbook into DuckDB tables, one table per sheet, in a schema named
/// after the workbook: Budget.xlsx becomes Budget.Q1, Budget.[Summary Q1] and so on.
///
/// This is the one kind of file that is copied rather than read in place. DuckDB can only read a
/// workbook through its excel extension, which is a download, and which types every column from
/// the first data row alone - a column that turns to text at row 301 fails, and one that turns
/// fractional at row 560 is silently rounded into the integer type it guessed. So the workbook is
/// read here instead, twice per sheet: once to see every cell's type, once to append the rows into
/// a table typed from all of them. Nothing is guessed from a sample, and nothing is written back to
/// the file.
///
/// A sheet is taken to be a table when its first non-empty row is a header - all text - with
/// nothing to the right of it further down. A first row that is not all text is data, and the
/// columns are named column_A, column_B and so on. Anything else - a title above the table, a
/// report laid out around it - is left out and said so, as is anything that fails to load.
/// </summary>
internal static class ExcelWorkbook
{
    /// <summary>What became of one sheet, for the mount log.</summary>
    public abstract record Outcome(string Sheet);

    public sealed record Loaded(string Sheet, int Rows) : Outcome(Sheet);

    /// <summary>The name is already mounted from another file, which keeps it.</summary>
    public sealed record Clash(string Sheet, string Owner) : Outcome(Sheet);

    /// <summary>The sheet is laid out as something other than a table.</summary>
    public sealed record NotATable(string Sheet) : Outcome(Sheet);

    public sealed record Failed(string Sheet, string Reason) : Outcome(Sheet);

    /// <summary>
    /// The names of a workbook's sheets, without reading any of them: what daffydb.mount checks
    /// for clashes before it loads anything. A workbook that cannot be opened has none, and says
    /// why when it is loaded.
    /// </summary>
    public static IReadOnlyList<string> SheetNames(string path)
    {
        try
        {
            using var stream = Open(path);
            using var reader = ExcelDataReader.Create(stream, ExcelWorkbookType.ExcelXml, ReaderOptions());
            return reader.WorksheetNames.ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Shared for writing and deleting, so a workbook that is open in Excel still loads.</summary>
    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static ExcelDataReaderOptions ReaderOptions() => new()
    {
        // The first row is judged below rather than assumed to be a header.
        Schema = ExcelSchema.NoHeaders,

        // A sheet filtered in Excel hides rows without removing them, and they are still data.
        ReadHiddenRows = true,

        // A hidden sheet is left out, as a hidden folder is. Chart sheets are never listed.
        ReadHiddenWorksheets = false,
    };

    /// <summary>
    /// Loads every table-shaped sheet of a workbook into <paramref name="schema"/>. Only opening the
    /// workbook itself can throw; a sheet that fails is dropped and reported, and the rest still load.
    /// </summary>
    /// <param name="owner">Which file already holds a table of this name in the schema, if any.</param>
    public static List<Outcome> Load(DuckDBConnection connection, string path, string catalog, string schema,
                                     Func<string, string?> owner)
    {
        using var stream = Open(path);
        using var reader = ExcelDataReader.Create(stream, ExcelWorkbookType.ExcelXml, ReaderOptions());

        var outcomes = new List<Outcome>();
        bool schemaCreated = false;

        foreach (string sheet in reader.WorksheetNames.ToList())
        {
            string table = $"{Quote(catalog)}.{Quote(schema)}.{Quote(sheet)}";
            bool created = false;

            try
            {
                if (!reader.TryOpenWorksheet(sheet)) throw new InvalidDataException("the sheet could not be opened");

                var layout = Survey(reader);
                if (layout is null) continue;                 // empty: nothing to say
                if (layout.Columns.Count == 0) { outcomes.Add(new NotATable(sheet)); continue; }

                if (owner(sheet) is string other)
                {
                    outcomes.Add(new Clash(sheet, other));
                    continue;
                }

                if (!schemaCreated)
                {
                    Execute(connection, $"CREATE SCHEMA IF NOT EXISTS {Quote(catalog)}.{Quote(schema)}");
                    schemaCreated = true;
                }

                Execute(connection, $"CREATE TABLE {table} (" +
                    string.Join(", ", layout.Columns.Select(c => $"{Quote(c.Name)} {SqlType(c.Kind)}")) + ")");
                created = true;

                if (!reader.TryOpenWorksheet(sheet)) throw new InvalidDataException("the sheet could not be opened again");
                outcomes.Add(new Loaded(sheet, Append(connection, reader, layout, catalog, schema, sheet)));
            }
            catch (Exception ex)
            {
                // Never half a table: whatever was appended before the failure goes with it.
                if (created)
                {
                    try { Execute(connection, $"DROP TABLE IF EXISTS {table}"); }
                    catch (DuckDBException) { }
                }

                outcomes.Add(new Failed(sheet, FirstLine(ex.Message)));
            }
        }

        return outcomes;
    }

    /// <summary>How a column is stored, decided from every cell in it.</summary>
    private enum Kind { Empty, Integer, Double, Date, Timestamp, Time, Boolean, Text }

    private sealed record Column(int Ordinal, string Name, Kind Kind);

    /// <summary>The header row, if there is one, and the columns worth loading.</summary>
    private sealed record Layout(int HeaderRow, IReadOnlyList<Column> Columns);

    /// <summary>
    /// The first pass over a sheet: where the header is, how wide the table is, and what every
    /// column holds. Returns null for a sheet with nothing in it, and a layout with no columns for
    /// one that does not look like a table.
    /// </summary>
    private static Layout? Survey(ExcelDataReader reader)
    {
        int headerRow = 0;
        string?[] headers = [];
        var kinds = new List<Kind>();
        int widest = -1;
        bool first = true;

        while (reader.Read())
        {
            if (IsBlank(reader)) continue;

            // The first row with anything in it: a header when every value in it is text.
            if (first)
            {
                first = false;

                bool allText = true;
                for (int i = 0; i < reader.RowFieldCount && allText; i++)
                    allText = CellKind(reader, i) is Kind.Empty or Kind.Text;

                if (allText)
                {
                    headerRow = reader.RowNumber;
                    headers = new string?[reader.RowFieldCount];
                    for (int i = 0; i < headers.Length; i++)
                        headers[i] = CellKind(reader, i) == Kind.Empty ? null : HeaderText(reader.GetString(i));
                    continue;
                }
            }

            for (int i = 0; i < reader.RowFieldCount; i++)
            {
                var kind = CellKind(reader, i);
                if (kind == Kind.Empty) continue;

                while (kinds.Count <= i) kinds.Add(Kind.Empty);
                kinds[i] = Merge(kinds[i], kind);
                widest = Math.Max(widest, i);
            }
        }

        if (headerRow == 0 && widest < 0) return null;

        // A title above the table, or notes beside it, put values past the header's last column.
        int headerWidth = Array.FindLastIndex(headers, h => h is not null) + 1;
        if (headerRow != 0 && widest >= headerWidth) return new Layout(headerRow, []);

        int width = headerRow != 0 ? headerWidth : widest + 1;
        var columns = new List<Column>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < width; i++)
        {
            string? header = i < headers.Length ? headers[i] : null;
            var kind = i < kinds.Count ? kinds[i] : Kind.Empty;

            // No header and no values: the space beside a table that does not start in column A.
            if (header is null && kind == Kind.Empty) continue;

            string name = header ?? $"column_{Letter(i)}";
            while (!names.Add(name)) name = $"{name}_column_{Letter(i)}";

            columns.Add(new Column(i, name, kind));
        }

        return new Layout(headerRow, columns);
    }

    /// <summary>The second pass: every row after the header, appended as the survey typed it.</summary>
    private static int Append(DuckDBConnection connection, ExcelDataReader reader, Layout layout,
                              string catalog, string schema, string sheet)
    {
        int rows = 0;
        using var appender = connection.CreateAppender(catalog, schema, sheet);

        while (reader.Read())
        {
            if (reader.RowNumber == layout.HeaderRow || IsBlank(reader)) continue;

            var row = appender.CreateRow();
            foreach (var column in layout.Columns)
            {
                int i = column.Ordinal;
                var cell = i < reader.RowFieldCount ? CellKind(reader, i) : Kind.Empty;
                if (cell == Kind.Empty)
                {
                    row.AppendNullValue();
                    continue;
                }

                switch (column.Kind)
                {
                    case Kind.Integer: row.AppendValue((long)reader.GetDouble(i)); break;
                    case Kind.Double: row.AppendValue(reader.GetDouble(i)); break;
                    case Kind.Date: row.AppendValue(DateOnly.FromDateTime(DateTimeOf(reader, i))); break;
                    case Kind.Timestamp: row.AppendValue(DateTimeOf(reader, i)); break;
                    case Kind.Time: row.AppendValue(TimeOf(reader, i)); break;
                    case Kind.Boolean: row.AppendValue(reader.GetBoolean(i)); break;
                    default: row.AppendValue(Text(reader, i, cell)); break;
                }
            }

            row.EndRow();
            rows++;
        }

        return rows;
    }

    /// <summary>
    /// What one cell holds. Excel keeps dates and times as numbers and only the cell's format says
    /// which is which, so the format decides. An error such as #N/A counts as empty, and so does
    /// text that is only whitespace.
    /// </summary>
    private static Kind CellKind(ExcelDataReader reader, int i)
    {
        switch (reader.GetExcelDataType(i))
        {
            case ExcelDataType.String:
                return string.IsNullOrWhiteSpace(reader.GetString(i)) ? Kind.Empty : Kind.Text;

            case ExcelDataType.Boolean:
                return Kind.Boolean;

            case ExcelDataType.DateTime:
                return DateTimeOf(reader, i).TimeOfDay == TimeSpan.Zero ? Kind.Date : Kind.Timestamp;

            case ExcelDataType.Numeric:
                double value = reader.GetDouble(i);
                switch (reader.GetFormat(i)?.Kind)
                {
                    case FormatKind.Date:
                        return DateTimeOf(reader, i).TimeOfDay == TimeSpan.Zero ? Kind.Date : Kind.Timestamp;

                    // A time of day is a fraction of one. A value past that is a duration such as
                    // [h]:mm, which has no time-of-day reading, so it stays a number.
                    case FormatKind.Time when value is >= 0 and < 1:
                        return Kind.Time;
                }

                return value == Math.Floor(value) && Math.Abs(value) <= MaxExactInteger ? Kind.Integer : Kind.Double;

            default:
                return Kind.Empty;
        }
    }

    /// <summary>Beyond 2^53 a double no longer holds every integer, so it is not treated as one.</summary>
    private const double MaxExactInteger = 9_007_199_254_740_992;

    /// <summary>
    /// Two kinds meeting in one column. Whole and fractional numbers make a fractional column, and
    /// dates with and without a time make a timestamp column. Anything else is text, which holds
    /// every value there is without changing any of them.
    /// </summary>
    private static Kind Merge(Kind column, Kind cell) => (column, cell) switch
    {
        _ when column == cell => column,
        (Kind.Empty, _) => cell,
        (Kind.Integer, Kind.Double) or (Kind.Double, Kind.Integer) => Kind.Double,
        (Kind.Date, Kind.Timestamp) or (Kind.Timestamp, Kind.Date) => Kind.Timestamp,
        _ => Kind.Text,
    };

    private static string SqlType(Kind kind) => kind switch
    {
        Kind.Integer => "BIGINT",
        Kind.Double => "DOUBLE",
        Kind.Date => "DATE",
        Kind.Timestamp => "TIMESTAMP",
        Kind.Time => "TIME",
        Kind.Boolean => "BOOLEAN",
        _ => "VARCHAR",
    };

    /// <summary>A value in a text column, written the way it reads rather than how it is stored.</summary>
    private static string Text(ExcelDataReader reader, int i, Kind cell)
    {
        switch (cell)
        {
            case Kind.Integer or Kind.Double:
                return reader.GetDouble(i).ToString(CultureInfo.InvariantCulture);

            case Kind.Date or Kind.Timestamp:
                var value = DateTimeOf(reader, i);
                string format = cell == Kind.Date ? "yyyy-MM-dd"
                              : value.Millisecond == 0 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm:ss.fff";
                return value.ToString(format, CultureInfo.InvariantCulture);

            case Kind.Time:
                return TimeOf(reader, i).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

            case Kind.Boolean:
                return reader.GetBoolean(i) ? "TRUE" : "FALSE";

            default:
                return reader.GetString(i);
        }
    }

    /// <summary>
    /// A header as a column name. A header cell can hold line breaks - Alt+Enter, to wrap a long
    /// title - which make a name nobody can type, so each run of whitespace becomes one space.
    /// </summary>
    private static string HeaderText(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// A date cell, to the millisecond. Excel's serial numbers are doubles, and 13:30 can come back
    /// as 13:29:59.9999999 - which would make a column of plain dates look like timestamps.
    /// </summary>
    private static DateTime DateTimeOf(ExcelDataReader reader, int i)
    {
        long ticks = reader.GetDateTime(i).Ticks;
        return new DateTime((ticks + TimeSpan.TicksPerMillisecond / 2) / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond);
    }

    private static TimeOnly TimeOf(ExcelDataReader reader, int i) =>
        new(Math.Min((long)Math.Round(reader.GetDouble(i) * 86_400_000) * TimeSpan.TicksPerMillisecond,
                     TimeOnly.MaxValue.Ticks));

    private static bool IsBlank(ExcelDataReader reader)
    {
        for (int i = 0; i < reader.RowFieldCount; i++)
            if (CellKind(reader, i) != Kind.Empty) return false;
        return true;
    }

    /// <summary>A column's letter as Excel shows it: 0 is A, 25 is Z, 26 is AA.</summary>
    private static string Letter(int ordinal)
    {
        string letters = "";
        for (int n = ordinal + 1; n > 0; n = (n - 1) / 26)
            letters = (char)('A' + (n - 1) % 26) + letters;
        return letters;
    }

    private static void Execute(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Quote(string identifier) => '"' + identifier.Replace("\"", "\"\"") + '"';

    private static string FirstLine(string text)
    {
        int newline = text.IndexOfAny(['\r', '\n']);
        return newline < 0 ? text : text[..newline].TrimEnd();
    }
}
