using DriverProbe;

// Replays Power BI's metadata conversation through the real client stacks - SqlClient, ODBC
// Driver 18 and MSOLEDBSQL19 - and records or compares what comes back.
//
//   DriverProbe --localdb --record src/DriverProbe/reference/localdb.json
//       The ground truth. LocalDB is a real SQL Server, the same build DaffyDB reports itself as,
//       so its answer to each call is the answer DaffyDB has to give.
//
//   DriverProbe --port N --compare src/DriverProbe/reference/localdb.json
//       DaffyDB against that truth. Needs --allow-passthrough on the server, to build type_zoo.

const string ReferenceDatabase = "DaffyDbReference";

int port = 1433;
bool localDb = false, verbose = false;
string? record = null, compare = null, only = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--localdb": localDb = true; break;
        case "--port" when i + 1 < args.Length: port = int.Parse(args[++i]); break;
        case "--record" when i + 1 < args.Length: record = args[++i]; break;
        case "--compare" when i + 1 < args.Length: compare = args[++i]; break;
        case "--only" when i + 1 < args.Length: only = args[++i]; break;
        case "--verbose" or "-v": verbose = true; break;
        default:
            Console.Error.WriteLine($"unknown argument: {args[i]}");
            return 2;
    }
}

var target = localDb
    ? new Target("LocalDB (MSSQLLocalDB)", ReferenceDatabase,
        $@"Server=(localdb)\MSSQLLocalDB;Integrated Security=True;Initial Catalog={ReferenceDatabase};TrustServerCertificate=True;Pooling=False;Connect Timeout=60",
        $@"Driver={{ODBC Driver 18 for SQL Server}};Server=(localdb)\MSSQLLocalDB;Trusted_Connection=yes;Database={ReferenceDatabase};TrustServerCertificate=yes",
        $@"Provider=MSOLEDBSQL19;Data Source=(localdb)\MSSQLLocalDB;Integrated Security=SSPI;Initial Catalog={ReferenceDatabase};Use Encryption for Data=Mandatory;Trust Server Certificate=True")
    : new Target($"DaffyDB on 127.0.0.1:{port}", "daffydb",
        $"Server=tcp:127.0.0.1,{port};Encrypt=Mandatory;TrustServerCertificate=True;User Id=demo;Password=x;Pooling=False;Connect Timeout=10",
        $"Driver={{ODBC Driver 18 for SQL Server}};Server=tcp:127.0.0.1,{port};UID=demo;PWD=x;Encrypt=yes;TrustServerCertificate=yes",
        $"Provider=MSOLEDBSQL19;Data Source=tcp:127.0.0.1,{port};User ID=demo;Password=x;Use Encryption for Data=Mandatory;Trust Server Certificate=True;Connect Timeout=10");

Console.WriteLine($"target: {target.Label}");

try
{
    if (localDb) Setup.LocalDb(target);
    else Setup.DaffyDb(target);
}
catch (Exception ex)
{
    // Every check would fail for the same reason, and forty copies of one message hide it.
    Console.WriteLine($"FAIL  setup: {FirstLine(ex.Message)}");
    return 2;
}

var outcomes = new List<CheckOutcome>();
foreach (var check in Checks.All.Where(c => only is null || c.Id.Contains(only, StringComparison.OrdinalIgnoreCase)))
{
    CheckOutcome outcome;
    try
    {
        outcome = new CheckOutcome(check.Id, null, check.Run(target));
    }
    catch (Exception ex)
    {
        outcome = new CheckOutcome(check.Id, FirstLine(ex.Message), []);
    }

    outcomes.Add(Reduce(check, outcome, target.Database));
}

if (compare is not null) return Compare(ReferenceFile.Load(compare), outcomes, verbose);

foreach (var outcome in outcomes)
{
    Console.WriteLine($"{(outcome.Error is null ? "ok   " : "ERROR")} {outcome.Id,-40} {Describe(outcome)}");
    if (verbose) Print(outcome);
}

int failures = outcomes.Count(o => o.Error is not null || o.Sets.Any(s => s.Rows.Any(r => r.Any(IsMissing))));

if (record is not null)
{
    new ReferenceFile(target.Label, DateTime.UtcNow.ToString("u"), outcomes).Save(record);
    Console.WriteLine($"recorded {outcomes.Count} checks to {record}");
}

Console.WriteLine($"{outcomes.Count - failures} of {outcomes.Count} checks answered cleanly");
return failures == 0 ? 0 : 1;

static int Compare(ReferenceFile reference, List<CheckOutcome> outcomes, bool verbose)
{
    Console.WriteLine($"reference: {reference.Source}, recorded {reference.RecordedAt}");
    var expected = reference.Checks.ToDictionary(c => c.Id);
    int matched = 0;

    foreach (var actual in outcomes)
    {
        if (!expected.TryGetValue(actual.Id, out var want))
        {
            Console.WriteLine($"FAIL  {actual.Id,-40} not in the reference; record it again");
            continue;
        }

        var (differences, notes) = Comparison.Run(want, actual, verbose ? int.MaxValue : 8);
        if (differences.Count == 0)
        {
            matched++;
            Console.WriteLine($"ok    {actual.Id,-40} {Describe(actual)}{(notes.Count > 0 ? $"  ({notes.Count} note(s))" : "")}");
        }
        else if (actual.Error is not null)
        {
            Console.WriteLine($"FAIL  {actual.Id,-40} {actual.Error}");
        }
        else
        {
            Console.WriteLine($"DIFF  {actual.Id,-40} {Describe(actual)}");
            foreach (var line in differences) Console.WriteLine($"        {line}");
        }

        if (verbose) foreach (var note in notes) Console.WriteLine($"        note: {note}");
    }

    Console.WriteLine("========================================================");
    Console.WriteLine($"{matched} of {outcomes.Count} checks match {reference.Source}");
    return matched == outcomes.Count ? 0 : 1;
}

// Cuts each result down to the rows its rule compares, and takes out what legitimately differs
// between two servers: the name of the reference database, and creation timestamps.
static CheckOutcome Reduce(Check check, CheckOutcome outcome, string database)
{
    var sets = outcome.Sets.Select(set =>
    {
        var rows = set.Rows
            .Select(row => row.Select((cell, i) => Normalize(set.Columns[i].Name, cell, database)).ToList())
            .ToList();

        rows = check.Rows switch
        {
            RowRule.Exact => rows,
            RowRule.ShapeOnly => [],
            _ => Keyed(set, rows, check.KeyColumn!, check.Keys!),
        };

        return set with { Rows = rows };
    }).ToList();

    return outcome with { Sets = sets };
}

// The database under test is DaffyDbReference on LocalDB and daffydb on DaffyDB. Both are written
// down as daffydb, so the name is the one difference between them that is not reported.
static string? Normalize(string column, string? cell, string database)
{
    if (column is "DATE_CREATED" or "DATE_MODIFIED" or "CREATED_DATE" or "MODIFIED_DATE") return cell is null ? null : "<varies>";
    return string.Equals(cell, database, StringComparison.OrdinalIgnoreCase) ? "daffydb" : cell;
}

// One row per key, in the order the keys are listed, so the two sides line up row for row. A key
// with no row still gets one, marked, so its absence is reported as a difference and not skipped.
static List<List<string?>> Keyed(ResultSetShape set, List<List<string?>> rows, string keyColumn, string[] keys)
{
    int index = set.Columns.FindIndex(c => c.Name.Equals(keyColumn, StringComparison.OrdinalIgnoreCase));
    if (index < 0) return [];

    return keys.Select(key =>
        rows.FirstOrDefault(r => string.Equals(r[index], key, StringComparison.OrdinalIgnoreCase))
        ?? set.Columns.Select((_, i) => i == index ? $"<missing {key}>" : null).ToList())
        .ToList();
}

static bool IsMissing(string? cell) => cell is not null && cell.StartsWith("<missing ", StringComparison.Ordinal);

static string Describe(CheckOutcome outcome)
{
    if (outcome.Error is not null) return outcome.Error;
    if (outcome.Sets.Count == 0) return "no result set";
    return string.Join("; ", outcome.Sets.Select(s => $"{s.Columns.Count} cols, {s.RowCount} row(s)"));
}

static void Print(CheckOutcome outcome)
{
    foreach (var set in outcome.Sets)
    {
        foreach (var column in set.Columns)
            Console.WriteLine($"        {column.Name,-28} {column.Type,-22} {column.Size,10} {(column.Nullable ? "null" : "not null")}");
        foreach (var row in set.Rows)
            Console.WriteLine($"        | {string.Join(" | ", row.Select(c => c ?? "NULL"))}");
    }
}

static string FirstLine(string text)
{
    int newline = text.IndexOfAny(['\r', '\n']);
    return newline < 0 ? text : text[..newline].TrimEnd();
}
