using System.Text.RegularExpressions;

namespace TdsProxy.Sql;

/// <summary>
/// The databases DaffyDB serves, laid out the way a SQL Server is: the data is in a database of its
/// own, daffydb, and master holds only the system catalog.
///
/// It was all one database named master until Power BI's SQL Server connector turned up. That
/// connector lists databases with "where name not in ('master', 'model', 'msdb', 'tempdb')", and
/// with master the only one there was nothing left to show. ODBC and OLE DB never noticed, because
/// they ask for the tables of whatever database they connected to.
///
/// Each is a DuckDB catalog of the same name, so a three-part name such as daffydb.dbo.orders or
/// master.sys.databases resolves as written.
///
/// msdb is the third, holding just enough for SSMS's policy checks. It has to be a database a
/// session can be in, not only a name to qualify with: SSMS says USE [msdb] before asking about
/// SQL Server Agent, and runs whatever it asks next from there.
/// </summary>
internal static class Databases
{
    public const string Master = "master";

    public const string Msdb = "msdb";

    /// <summary>Where every mounted file lands, and where a login that names no database starts.</summary>
    public const string Data = "daffydb";

    /// <summary>
    /// What the database says about itself, as its MS_Description - the extended property tools show
    /// as a description.
    /// </summary>
    public const string Description = "DaffyDB: a TDS listener in front of a local DuckDB engine. This is not SQL Server.";

    /// <summary>
    /// Each database with the id SQL Server would give it: master is always 1 and msdb 4, and user
    /// databases start at 5. tempdb and model (2 and 3) are not here.
    /// </summary>
    public static IReadOnlyList<(string Name, int Id)> All { get; } = [(Master, 1), (Msdb, 4), (Data, 5)];

    /// <summary>SQL Server's error for USE naming a database that does not exist.</summary>
    public const int NotFoundNumber = 911;

    /// <summary>SQL Server's error for a login naming a database that does not exist.</summary>
    public const int LoginNotFoundNumber = 4060;

    private static readonly Regex UsePattern = new(
        @"^USE\s+(?<name>\[[^\]]+\]|""[^""]+""|[\w@#$]+)\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>USE [name], with or without the brackets and the semicolon.</summary>
    public static bool TryParseUse(string statement, out string name)
    {
        var match = UsePattern.Match(statement.Trim());
        name = match.Success ? match.Groups["name"].Value : "";
        return match.Success;
    }

    /// <summary>
    /// The database a name refers to, spelled as DaffyDB spells it, or null if there is none. The
    /// name may arrive bracketed or quoted, and SQL Server compares database names case-insensitively.
    /// </summary>
    public static string? Resolve(string? name)
    {
        string bare = (name ?? "").Trim();
        if (bare.Length >= 2 && ((bare[0] == '[' && bare[^1] == ']') || (bare[0] == '"' && bare[^1] == '"')))
            bare = bare[1..^1];

        foreach (var (known, _) in All)
            if (known.Equals(bare, StringComparison.OrdinalIgnoreCase)) return known;

        return null;
    }

    public static int IdOf(string name) =>
        All.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Id;
}
