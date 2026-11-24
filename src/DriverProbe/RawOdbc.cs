using System.Runtime.InteropServices;
using System.Text;

namespace DriverProbe;

/// <summary>
/// The ODBC API, called directly rather than through System.Data.Odbc.
///
/// Power BI's ODBC connector calls the catalog functions itself - SQLTables, SQLColumns,
/// SQLGetTypeInfo, SQLPrimaryKeys, SQLForeignKeys - and the driver turns each one into the stored
/// procedure call the captures show. System.Data.Odbc reaches the first three through GetSchema,
/// reshapes what they return, and has no way to reach the last two at all. So replaying Power BI
/// means going through odbc32.dll, exactly as it does.
///
/// Every value is fetched as SQL_C_WCHAR: the comparison is of what an application would read,
/// and text is the one representation every column can be converted to.
/// </summary>
internal sealed class RawOdbc : IDisposable
{
    private const string Library = "odbc32.dll";

    private const short HandleEnv = 1, HandleDbc = 2, HandleStmt = 3;
    private const int AttrOdbcVersion = 200, AttrLoginTimeout = 103, AttrQueryTimeout = 0;

    /// <summary>ODBC 3.8, which is what a current application such as Power BI declares.</summary>
    private const int OdbcVersion380 = 380;

    private const short Nts = -3;
    private const short Success = 0, SuccessWithInfo = 1, NoData = 100;
    private const short CWChar = -8;
    private const int NullData = -1, NoTotal = -4;

    public const short AllTypes = 0;

    private IntPtr _environment;
    private IntPtr _connection;
    private bool _connected;

    public RawOdbc(string connectionString)
    {
        try
        {
            Ensure(SQLAllocHandle(HandleEnv, IntPtr.Zero, out _environment), HandleEnv, IntPtr.Zero);
            Ensure(SQLSetEnvAttr(_environment, AttrOdbcVersion, OdbcVersion380, 0), HandleEnv, _environment);
            Ensure(SQLAllocHandle(HandleDbc, _environment, out _connection), HandleEnv, _environment);
            SQLSetConnectAttrW(_connection, AttrLoginTimeout, 15, 0);

            var completed = new char[2048];
            Ensure(SQLDriverConnectW(_connection, IntPtr.Zero, connectionString, Nts, completed,
                                     (short)completed.Length, out _, 0), HandleDbc, _connection);
            _connected = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public List<ResultSetShape> Tables(string? catalog, string? schema, string? table, string? type) =>
        Run(s => SQLTablesW(s, catalog, Length(catalog), schema, Length(schema), table, Length(table), type, Length(type)));

    public List<ResultSetShape> Columns(string? catalog, string? schema, string? table, string? column) =>
        Run(s => SQLColumnsW(s, catalog, Length(catalog), schema, Length(schema), table, Length(table), column, Length(column)));

    public List<ResultSetShape> TypeInfo(short dataType) => Run(s => SQLGetTypeInfoW(s, dataType));

    public List<ResultSetShape> PrimaryKeys(string? catalog, string? schema, string? table) =>
        Run(s => SQLPrimaryKeysW(s, catalog, Length(catalog), schema, Length(schema), table, Length(table)));

    public List<ResultSetShape> ForeignKeys(string? pkCatalog, string? pkSchema, string? pkTable,
                                            string? fkCatalog, string? fkSchema, string? fkTable) =>
        Run(s => SQLForeignKeysW(s, pkCatalog, Length(pkCatalog), pkSchema, Length(pkSchema), pkTable, Length(pkTable),
                                 fkCatalog, Length(fkCatalog), fkSchema, Length(fkSchema), fkTable, Length(fkTable)));

    public List<ResultSetShape> Execute(string sql) => Run(s => SQLExecDirectW(s, sql, Nts));

    /// <summary>A null argument is a NULL pointer - "no restriction" - which is not the same as "".</summary>
    private static short Length(string? value) => value is null ? (short)0 : Nts;

    private List<ResultSetShape> Run(Func<IntPtr, short> call)
    {
        Ensure(SQLAllocHandle(HandleStmt, _connection, out IntPtr statement), HandleDbc, _connection);
        try
        {
            SQLSetStmtAttrW(statement, AttrQueryTimeout, 15, 0);
            Ensure(call(statement), HandleStmt, statement);

            var sets = new List<ResultSetShape>();
            while (true)
            {
                Ensure(SQLNumResultCols(statement, out short columns), HandleStmt, statement);
                if (columns > 0) sets.Add(ReadSet(statement, columns));

                short more = SQLMoreResults(statement);
                if (more == NoData) break;
                Ensure(more, HandleStmt, statement);
            }

            return sets;
        }
        finally
        {
            SQLFreeHandle(HandleStmt, statement);
        }
    }

    private static ResultSetShape ReadSet(IntPtr statement, short count)
    {
        var columns = new List<ColumnShape>();
        for (ushort i = 1; i <= count; i++)
        {
            var name = new char[256];
            Ensure(SQLDescribeColW(statement, i, name, (short)name.Length, out short nameLength, out short type,
                                   out nuint size, out _, out short nullable), HandleStmt, statement);
            columns.Add(new ColumnShape(new string(name, 0, nameLength), TypeName(type), (long)size, nullable == 1));
        }

        var rows = new List<List<string?>>();
        while (true)
        {
            short fetched = SQLFetch(statement);
            if (fetched == NoData) break;
            Ensure(fetched, HandleStmt, statement);

            var row = new List<string?>(count);
            for (ushort i = 1; i <= count; i++) row.Add(ReadText(statement, i));
            rows.Add(row);
        }

        return new ResultSetShape(columns, rows.Count, rows);
    }

    /// <summary>
    /// SQLGetData hands back a long value in pieces: SUCCESS_WITH_INFO while there is more, SUCCESS
    /// on the last piece, NO_DATA once it is all read. Each full piece is the buffer less the two-byte
    /// terminator the driver always writes.
    /// </summary>
    private static string? ReadText(IntPtr statement, ushort column)
    {
        var text = new StringBuilder();
        var buffer = new byte[8192];

        while (true)
        {
            short result = SQLGetData(statement, column, CWChar, buffer, buffer.Length, out nint indicator);
            if (result == NoData) break;
            Ensure(result, HandleStmt, statement);
            if (indicator == NullData) return null;

            int bytes = indicator == NoTotal || indicator > buffer.Length - 2 ? buffer.Length - 2 : (int)indicator;
            text.Append(Encoding.Unicode.GetString(buffer, 0, bytes));

            if (result == Success) break;
        }

        return text.ToString();
    }

    private static string TypeName(short type) => type switch
    {
        1 => "SQL_CHAR", 12 => "SQL_VARCHAR", -1 => "SQL_LONGVARCHAR",
        -8 => "SQL_WCHAR", -9 => "SQL_WVARCHAR", -10 => "SQL_WLONGVARCHAR",
        -7 => "SQL_BIT", -6 => "SQL_TINYINT", 5 => "SQL_SMALLINT", 4 => "SQL_INTEGER", -5 => "SQL_BIGINT",
        7 => "SQL_REAL", 6 => "SQL_FLOAT", 8 => "SQL_DOUBLE", 2 => "SQL_NUMERIC", 3 => "SQL_DECIMAL",
        91 => "SQL_TYPE_DATE", 92 => "SQL_TYPE_TIME", 93 => "SQL_TYPE_TIMESTAMP",
        -154 => "SQL_SS_TIME2", -155 => "SQL_SS_TIMESTAMPOFFSET",
        -2 => "SQL_BINARY", -3 => "SQL_VARBINARY", -4 => "SQL_LONGVARBINARY", -11 => "SQL_GUID",
        _ => $"SQL_TYPE({type})",
    };

    private static void Ensure(short result, short handleType, IntPtr handle)
    {
        if (result is Success or SuccessWithInfo) return;
        throw new InvalidOperationException(Diagnostics(handleType, handle, result));
    }

    private static string Diagnostics(short handleType, IntPtr handle, short result)
    {
        var messages = new List<string>();
        if (handle != IntPtr.Zero)
        {
            for (short record = 1; record <= 4; record++)
            {
                var state = new char[6];
                var message = new char[1024];
                short got = SQLGetDiagRecW(handleType, handle, record, state, out _, message, (short)message.Length, out short length);
                if (got is not (Success or SuccessWithInfo)) break;
                messages.Add($"[{new string(state, 0, 5)}] {new string(message, 0, Math.Min((int)length, message.Length))}");
            }
        }

        return messages.Count > 0 ? string.Join(" | ", messages) : $"ODBC call failed with {result}";
    }

    public void Dispose()
    {
        if (_connected) SQLDisconnect(_connection);
        if (_connection != IntPtr.Zero) SQLFreeHandle(HandleDbc, _connection);
        if (_environment != IntPtr.Zero) SQLFreeHandle(HandleEnv, _environment);
        _connected = false;
        _connection = _environment = IntPtr.Zero;
    }

    [DllImport(Library)] private static extern short SQLAllocHandle(short handleType, IntPtr input, out IntPtr output);
    [DllImport(Library)] private static extern short SQLFreeHandle(short handleType, IntPtr handle);
    [DllImport(Library)] private static extern short SQLSetEnvAttr(IntPtr environment, int attribute, IntPtr value, int length);
    [DllImport(Library)] private static extern short SQLSetConnectAttrW(IntPtr connection, int attribute, IntPtr value, int length);
    [DllImport(Library)] private static extern short SQLSetStmtAttrW(IntPtr statement, int attribute, IntPtr value, int length);
    [DllImport(Library)] private static extern short SQLDisconnect(IntPtr connection);
    [DllImport(Library)] private static extern short SQLNumResultCols(IntPtr statement, out short count);
    [DllImport(Library)] private static extern short SQLFetch(IntPtr statement);
    [DllImport(Library)] private static extern short SQLMoreResults(IntPtr statement);
    [DllImport(Library)] private static extern short SQLGetTypeInfoW(IntPtr statement, short dataType);

    [DllImport(Library)]
    private static extern short SQLGetData(IntPtr statement, ushort column, short targetType,
                                           [Out] byte[] buffer, nint capacity, out nint indicator);

    [DllImport(Library, CharSet = CharSet.Unicode)]
    private static extern short SQLDriverConnectW(IntPtr connection, IntPtr window, string input, short inputLength,
                                                  [Out] char[] output, short outputCapacity, out short outputLength,
                                                  ushort completion);

    [DllImport(Library, CharSet = CharSet.Unicode)]
    private static extern short SQLExecDirectW(IntPtr statement, string text, int length);

    [DllImport(Library, CharSet = CharSet.Unicode)]
    private static extern short SQLDescribeColW(IntPtr statement, ushort column, [Out] char[] name, short capacity,
                                                out short nameLength, out short dataType, out nuint size,
                                                out short digits, out short nullable);

    [DllImport(Library, CharSet = CharSet.Unicode)]
    private static extern short SQLGetDiagRecW(short handleType, IntPtr handle, short record, [Out] char[] state,
                                               out int nativeError, [Out] char[] message, short capacity, out short length);

    [DllImport(Library, CharSet = CharSet.Unicode)]
    private static extern short SQLTablesW(IntPtr statement, string? catalog, short catalogLength, string? schema,
                                           short schemaLength, string? table, short tableLength, string? type, short typeLength);

    [DllImport(Library, CharSet = CharSet.Unicode)]
    private static extern short SQLColumnsW(IntPtr statement, string? catalog, short catalogLength, string? schema,
                                            short schemaLength, string? table, short tableLength, string? column, short columnLength);

    [DllImport(Library, CharSet = CharSet.Unicode)]
    private static extern short SQLPrimaryKeysW(IntPtr statement, string? catalog, short catalogLength, string? schema,
                                                short schemaLength, string? table, short tableLength);

    [DllImport(Library, CharSet = CharSet.Unicode)]
    private static extern short SQLForeignKeysW(IntPtr statement,
                                                string? pkCatalog, short pkCatalogLength, string? pkSchema, short pkSchemaLength,
                                                string? pkTable, short pkTableLength,
                                                string? fkCatalog, short fkCatalogLength, string? fkSchema, short fkSchemaLength,
                                                string? fkTable, short fkTableLength);
}
