using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DriverProbe;

/// <summary>
/// One column as a client library reports it. <paramref name="Type"/> is in that library's own
/// vocabulary - a SQL Server type name from SqlClient, an ODBC SQL type from odbc32, a CLR type from
/// OLE DB - because the question is always "does this client see the same thing from both servers",
/// never "are these two vocabularies equivalent".
/// </summary>
internal sealed record ColumnShape(string Name, string Type, long Size, bool Nullable);

/// <summary>
/// A result set reduced to what is compared. <paramref name="RowCount"/> is the true count;
/// <paramref name="Rows"/> holds only the rows the check's <see cref="RowRule"/> keeps.
/// </summary>
internal sealed record ResultSetShape(List<ColumnShape> Columns, int RowCount, List<List<string?>> Rows);

internal sealed record CheckOutcome(string Id, string? Error, List<ResultSetShape> Sets);

internal sealed record ReferenceFile(string Source, string RecordedAt, List<CheckOutcome> Checks)
{
    private const string NewLine = "\n";

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Laid out by hand, one column or one row per line. Indented JSON gives every cell a line of its
    /// own, and compact JSON puts a 42-column result on one line; either way the reference stops
    /// being something a person reads, and a reference nobody reads is one nobody checks.
    /// </summary>
    public void Save(string path)
    {
        var text = new StringBuilder();
        text.Append('{').Append(NewLine);
        text.Append($"  \"Source\": {Json(Source)},").Append(NewLine);
        text.Append($"  \"RecordedAt\": {Json(RecordedAt)},").Append(NewLine);
        text.Append("  \"Checks\": [").Append(NewLine);

        for (int i = 0; i < Checks.Count; i++)
        {
            var check = Checks[i];
            text.Append("    {").Append(NewLine);
            text.Append($"      \"Id\": {Json(check.Id)},").Append(NewLine);
            text.Append($"      \"Error\": {Json(check.Error)},").Append(NewLine);
            text.Append("      \"Sets\": [");

            for (int j = 0; j < check.Sets.Count; j++)
            {
                var set = check.Sets[j];
                text.Append(j == 0 ? "" : ",").Append(NewLine).Append("        {").Append(NewLine);
                AppendList(text, "Columns", set.Columns);
                text.Append($"          \"RowCount\": {set.RowCount},").Append(NewLine);
                AppendList(text, "Rows", set.Rows, last: true);
                text.Append("        }");
            }

            text.Append(check.Sets.Count == 0 ? "]" : NewLine + "      ]").Append(NewLine);
            text.Append(i == Checks.Count - 1 ? "    }" : "    },").Append(NewLine);
        }

        text.Append("  ]").Append(NewLine).Append('}').Append(NewLine);

        // The layout is the only source of raw newlines: any inside a value are escaped by Json().
        File.WriteAllText(path, text.ToString().Replace(NewLine, Environment.NewLine));
    }

    public static ReferenceFile Load(string path) =>
        JsonSerializer.Deserialize<ReferenceFile>(File.ReadAllText(path))
        ?? throw new InvalidDataException($"{path} is empty");

    private static void AppendList<T>(StringBuilder text, string name, List<T> items, bool last = false)
    {
        text.Append($"          \"{name}\": [");
        for (int k = 0; k < items.Count; k++)
            text.Append(k == 0 ? "" : ",").Append(NewLine).Append("            ").Append(Json(items[k]));

        text.Append(items.Count == 0 ? "]" : NewLine + "          ]").Append(last ? "" : ",").Append(NewLine);
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, Compact);
}

internal static class Values
{
    /// <summary>
    /// Every value becomes text before it is compared, in one fixed format per type, so that the
    /// comparison is about what the client received rather than about how .NET prints it.
    /// </summary>
    public static string? Format(object? value) => value switch
    {
        null or DBNull => null,
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        DateTimeOffset offset => offset.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture),
        TimeSpan time => time.ToString("c", CultureInfo.InvariantCulture),
        Guid guid => guid.ToString("D").ToUpperInvariant(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}

internal static class Comparison
{
    /// <summary>
    /// Differences are failures: a column missing, renamed, reordered or of another type, or a row
    /// that reads differently. Notes are not: declared sizes and nullability are reported, because
    /// they may yet turn out to matter to some driver, but nothing is known to depend on them.
    /// </summary>
    public static (List<string> Differences, List<string> Notes) Run(CheckOutcome expected, CheckOutcome actual,
                                                                     int maxLines = 8)
    {
        var differences = new List<string>();
        var notes = new List<string>();

        if (expected.Error is not null)
        {
            differences.Add($"the reference itself failed here: {expected.Error}");
            return (differences, notes);
        }

        if (actual.Error is not null)
        {
            differences.Add(actual.Error);
            return (differences, notes);
        }

        if (expected.Sets.Count != actual.Sets.Count)
            differences.Add($"{actual.Sets.Count} result set(s), expected {expected.Sets.Count}");

        for (int i = 0; i < Math.Min(expected.Sets.Count, actual.Sets.Count); i++)
            CompareSet(i, expected.Sets[i], actual.Sets[i], differences, notes);

        return (Trim(differences, maxLines), Trim(notes, maxLines));
    }

    private static void CompareSet(int index, ResultSetShape expected, ResultSetShape actual,
                                   List<string> differences, List<string> notes)
    {
        string where = index == 0 ? "" : $"set {index + 1}: ";
        bool columnsAlign = expected.Columns.Count == actual.Columns.Count;

        if (!columnsAlign)
            differences.Add($"{where}{actual.Columns.Count} columns, expected {expected.Columns.Count}");

        for (int c = 0; c < Math.Max(expected.Columns.Count, actual.Columns.Count); c++)
        {
            var want = c < expected.Columns.Count ? expected.Columns[c] : null;
            var got = c < actual.Columns.Count ? actual.Columns[c] : null;

            if (want is null) { differences.Add($"{where}column {c + 1} '{got!.Name}' is extra"); continue; }
            if (got is null) { differences.Add($"{where}column {c + 1} '{want.Name}' {want.Type} is missing"); continue; }

            if (!string.Equals(want.Name, got.Name, StringComparison.Ordinal))
            {
                differences.Add($"{where}column {c + 1} is '{got.Name}', expected '{want.Name}'");
                columnsAlign = false;
                continue;
            }

            if (want.Type != got.Type)
                differences.Add($"{where}{want.Name} is {got.Type}, expected {want.Type}");
            else if (want.Size != got.Size || want.Nullable != got.Nullable)
                notes.Add($"{where}{want.Name} declared size {got.Size}{(got.Nullable ? "" : " not null")}, " +
                          $"reference {want.Size}{(want.Nullable ? "" : " not null")}");
        }

        if (expected.Rows.Count != actual.Rows.Count)
            differences.Add($"{where}{actual.Rows.Count} row(s) compared, expected {expected.Rows.Count}");

        // Reading cells against the wrong column names would bury the one real difference under
        // dozens of consequential ones, so rows are only compared once the columns agree.
        if (!columnsAlign) return;

        for (int r = 0; r < Math.Min(expected.Rows.Count, actual.Rows.Count); r++)
        {
            for (int c = 0; c < expected.Columns.Count; c++)
            {
                string? want = expected.Rows[r][c], got = actual.Rows[r][c];
                if (want != got)
                    differences.Add($"{where}row {r + 1} {expected.Columns[c].Name} = {Show(got)}, expected {Show(want)}");
            }
        }
    }

    private static string Show(string? value) => value is null ? "NULL" : $"'{value}'";

    private static List<string> Trim(List<string> lines, int maxLines) =>
        lines.Count <= maxLines
            ? lines
            : [.. lines.Take(maxLines), $"... and {lines.Count - maxLines} more (--verbose, or -Details from the script, shows all)"];
}
