using TdsProxy.Query;
using TdsProxy.Tds;

namespace TdsProxy.Sql;

/// <summary>
/// Column declarations for system procedures, named after the SQL Server types they stand for.
/// Sizes are declared in bytes, as TDS declares them: sysname is nvarchar(128), so 256.
/// </summary>
internal static class Shape
{
    /// <summary>sysname.</summary>
    public static ColumnDef Name(string name) => new(name, SqlColumnType.NVarChar, true, MaxLength: 256);

    public static ColumnDef NVarChar(string name, int characters) =>
        new(name, SqlColumnType.NVarChar, true, MaxLength: characters * 2);

    public static ColumnDef NVarCharMax(string name) =>
        new(name, SqlColumnType.NVarChar, true, MaxLength: ResponseWriter.MaxSize);

    public static ColumnDef VarChar(string name, int length) => new(name, SqlColumnType.VarChar, true, MaxLength: length);

    public static ColumnDef Binary(string name, int length) => new(name, SqlColumnType.Binary, true, MaxLength: length);

    public static ColumnDef Bit(string name) => new(name, SqlColumnType.Bit, true);

    public static ColumnDef Tiny(string name) => new(name, SqlColumnType.TinyInt, true);

    public static ColumnDef Small(string name) => new(name, SqlColumnType.SmallInt, true);

    public static ColumnDef Int(string name) => new(name, SqlColumnType.Int, true);

    public static ColumnDef Guid(string name) => new(name, SqlColumnType.UniqueIdentifier, true);

    public static ColumnDef DateTime(string name) => new(name, SqlColumnType.DateTime, true);
}
