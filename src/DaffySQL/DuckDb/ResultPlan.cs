using System.Text;
using DaffySQL.Tds;

namespace DaffySQL.DuckDb;

/// <summary>How a value of a column is laid out on the wire, which decides how it is read.</summary>
internal enum ValueKind
{
    // Fixed width, no length byte: what a server sends for a NOT NULL column.
    UTinyInt, SmallInt, Integer, BigInt, Bit, Real, Float, Money, SmallMoney, DateTime, SmallDateTime,

    // A byte length first, 0 meaning NULL: the nullable "N" forms and the newer types.
    IntN, BitN, FloatN, MoneyN, DateTimeN, Date, Time, DateTime2, DateTimeOffset, Decimal, Guid,

    // A 2-byte length first, 0xFFFF meaning NULL.
    Chars, Binary,

    // PLP: an 8-byte total, then chunks - the max types, xml, CLR types.
    PlpChars, PlpBinary,

    // text, ntext and image: a text pointer, a timestamp, then a 4-byte length.
    Text, Image,

    // A 4-byte length, 0 meaning NULL, then a value that carries its own type.
    Variant,
}

/// <summary>How the bytes of a character column are encoded.</summary>
internal enum CharEncoding { Utf16, Utf8, CodePage }

/// <summary>
/// One column of a SQL Server result as DuckDB will see it: its name, the DuckDB type it is
/// declared as, and how its values are read.
/// </summary>
internal sealed record ColumnPlan(string Name, string SqlType, ValueKind Kind, DuckType DuckType, byte Width = 0, byte Scale = 0)
{
    /// <summary>The time and datetime2 family: digits of a second kept on the wire.</summary>
    public byte TimeScale { get; init; }

    public CharEncoding Chars { get; init; }
    public Encoding? CodePage { get; init; }

    /// <summary>The DuckDB type, as DuckDB names it: what DESCRIBE shows, and what a rerun's columns are compared by.</summary>
    public string DuckSignature => DuckType switch
    {
        DuckType.Decimal => $"DECIMAL({Width},{Scale})",
        DuckType.TimestampTz => "TIMESTAMP WITH TIME ZONE",
        _ => DuckType.ToString().ToUpperInvariant(),
    };
}

/// <summary>
/// The DuckDB side of a SQL Server result: which DuckDB type each SQL Server type becomes.
///
///   tinyint -> UTINYINT (SQL Server's is unsigned); smallint, int, bigint, bit, real, float -> the same
///   decimal(p,s), numeric(p,s) -> DECIMAL(p,s), every precision up to 38 as it is
///   money -> DECIMAL(19,4), smallmoney -> DECIMAL(10,4): their exact range and scale
///   date -> DATE; time -> TIME; datetime2, datetime, smalldatetime -> TIMESTAMP
///   datetimeoffset -> TIMESTAMP WITH TIME ZONE: the instant is kept, the offset it was written in is not
///   uniqueidentifier -> UUID
///   char, varchar, nchar, nvarchar, text, ntext, xml, sql_variant -> VARCHAR
///   binary, varbinary, image, rowversion, CLR types (geography, geometry, hierarchyid) -> BLOB
///
/// DuckDB's TIME and TIMESTAMP count microseconds, so time(7) and datetime2(7) lose their last
/// digit (100 ns), truncated. TIMESTAMP_NS would keep it, but only spans 1677 to 2262, and SQL
/// Server data is full of 0001-01-01 and 9999-12-31. datetime keeps its 1/300 s ticks, rounded to
/// the nearest microsecond. A CLR type arrives as SQL Server's own serialization; cast it in the
/// T-SQL (.STAsBinary(), .ToString()) for something portable.
/// </summary>
internal static class ResultPlan
{
    public static ColumnPlan[] For(IReadOnlyList<ResultColumn> columns)
    {
        var plans = new ColumnPlan[columns.Count];
        var names = UniqueNames(columns.Select(c => c.Name).ToList());
        for (int i = 0; i < columns.Count; i++) plans[i] = For(columns[i], names[i]);
        return plans;
    }

    private static ColumnPlan For(ResultColumn column, string name)
    {
        var type = column.Type;
        string sql = column.UserType == ResultColumn.TimestampUserType ? "rowversion" : type.SqlName;

        ColumnPlan Plan(ValueKind kind, DuckType duck, byte width = 0, byte scale = 0) => new(name, sql, kind, duck, width, scale);

        switch (type.Type)
        {
            case 0x30: return Plan(ValueKind.UTinyInt, DuckType.UTinyInt);
            case 0x34: return Plan(ValueKind.SmallInt, DuckType.SmallInt);
            case 0x38: return Plan(ValueKind.Integer, DuckType.Integer);
            case 0x7F: return Plan(ValueKind.BigInt, DuckType.BigInt);
            case 0x26:
                return Plan(ValueKind.IntN, type.MaxLength switch
                {
                    1 => DuckType.UTinyInt, 2 => DuckType.SmallInt, 4 => DuckType.Integer, _ => DuckType.BigInt,
                });

            case 0x32: return Plan(ValueKind.Bit, DuckType.Boolean);
            case 0x68: return Plan(ValueKind.BitN, DuckType.Boolean);

            case 0x3B: return Plan(ValueKind.Real, DuckType.Float);
            case 0x3E: return Plan(ValueKind.Float, DuckType.Double);
            case 0x6D: return Plan(ValueKind.FloatN, type.MaxLength == 4 ? DuckType.Float : DuckType.Double);

            case 0x3C: return Plan(ValueKind.Money, DuckType.Decimal, 19, 4);
            case 0x7A: return Plan(ValueKind.SmallMoney, DuckType.Decimal, 10, 4);
            case 0x6E: return Plan(ValueKind.MoneyN, DuckType.Decimal, type.MaxLength == 4 ? (byte)10 : (byte)19, 4);

            case 0x3D: return Plan(ValueKind.DateTime, DuckType.Timestamp);
            case 0x3A: return Plan(ValueKind.SmallDateTime, DuckType.Timestamp);
            case 0x6F: return Plan(ValueKind.DateTimeN, DuckType.Timestamp);

            case 0x28: return Plan(ValueKind.Date, DuckType.Date);
            case 0x29: return Plan(ValueKind.Time, DuckType.Time) with { TimeScale = type.Scale };
            case 0x2A: return Plan(ValueKind.DateTime2, DuckType.Timestamp) with { TimeScale = type.Scale };
            case 0x2B: return Plan(ValueKind.DateTimeOffset, DuckType.TimestampTz) with { TimeScale = type.Scale };

            case 0x6A or 0x6C or 0x37 or 0x3F:
                return Plan(ValueKind.Decimal, DuckType.Decimal, Math.Max((byte)1, type.Precision), type.Scale);

            case 0x24: return Plan(ValueKind.Guid, DuckType.Uuid);

            case 0xA7 or 0xAF:
                return CharPlan(Plan(type.IsPlp ? ValueKind.PlpChars : ValueKind.Chars, DuckType.Varchar), type.Collation);
            case 0xE7 or 0xEF:
                return Plan(type.IsPlp ? ValueKind.PlpChars : ValueKind.Chars, DuckType.Varchar) with { Chars = CharEncoding.Utf16 };
            case 0xA5 or 0xAD:
                return Plan(type.IsPlp ? ValueKind.PlpBinary : ValueKind.Binary, DuckType.Blob);

            case 0xF1: return Plan(ValueKind.PlpChars, DuckType.Varchar) with { Chars = CharEncoding.Utf16 };   // xml is UTF-16
            case 0xF0: return Plan(ValueKind.PlpBinary, DuckType.Blob);

            case 0x23: return CharPlan(Plan(ValueKind.Text, DuckType.Varchar), type.Collation);
            case 0x63: return Plan(ValueKind.Text, DuckType.Varchar) with { Chars = CharEncoding.Utf16 };
            case 0x22: return Plan(ValueKind.Image, DuckType.Blob);

            case 0x62: return Plan(ValueKind.Variant, DuckType.Varchar);

            default:
                throw new TdsUnsupportedException(
                    $"column '{column.Name}' is of a type DaffySQL cannot read ({sql}); CAST it to another type in the query");
        }
    }

    /// <summary>
    /// varchar's bytes are in its collation's code page - or UTF-8, for a _UTF8 collation, which
    /// DaffySQL asks the server for at login rather than having it converted.
    /// </summary>
    private static ColumnPlan CharPlan(ColumnPlan plan, byte[]? collation)
    {
        var encoding = TypeInfoReader.EncodingFor(collation);
        return encoding.CodePage == Encoding.UTF8.CodePage
            ? plan with { Chars = CharEncoding.Utf8 }
            : plan with { Chars = CharEncoding.CodePage, CodePage = encoding };
    }

    /// <summary>
    /// DuckDB needs a name for every column, and refers to them case-insensitively, so a column
    /// SQL Server left unnamed becomes column0, column1... by position, as DuckDB names CSV columns
    /// without a header, and a repeated name gets _1, _2.
    /// </summary>
    private static List<string> UniqueNames(List<string> names)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<string>(names.Count);
        for (int i = 0; i < names.Count; i++)
        {
            string name = names[i].Length > 0 ? names[i] : $"column{i}";
            string candidate = name;
            for (int n = 1; !taken.Add(candidate); n++) candidate = $"{name}_{n}";
            unique.Add(candidate);
        }
        return unique;
    }
}
