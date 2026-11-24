using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DaffySQL.Client;

namespace DaffySQL.DuckDb;

/// <summary>
///   daffy_connect(name VARCHAR, connection_string VARCHAR) -> VARCHAR
///   daffy_disconnect(name VARCHAR) -> VARCHAR
///   daffy_connections() -> TABLE
///
/// Names a SQL Server, forgets one, and lists them. daffy_connect logs in before it answers, so a
/// wrong password or an unreachable server is reported there, not at the first query. A password
/// typed into SQL can stay in a history file; code that has one in hand can call
/// <see cref="DaffySql.Connect"/> instead, and SQL only ever sees the name.
/// </summary>
internal static unsafe class ConnectionFunctions
{
    public static void Register(nint connection)
    {
        RegisterScalar(connection, "daffy_connect", parameters: 2, &Connect);
        RegisterScalar(connection, "daffy_disconnect", parameters: 1, &Disconnect);

        nint table = DuckApi.CreateTableFunction();
        try
        {
            DuckFunction.SetName(table, "daffy_connections", DuckApi.TableFunctionSetName);
            DuckApi.TableFunctionSetBind(table, &ListBind);
            DuckApi.TableFunctionSetInit(table, &ListInit);
            DuckApi.TableFunctionSetFunction(table, &ListScan);
            if (DuckApi.RegisterTableFunction(connection, table) != 0)
                throw new InvalidOperationException("DuckDB refused to register daffy_connections");
        }
        finally
        {
            DuckApi.DestroyTableFunction(&table);
        }
    }

    /// <summary>VARCHAR in, VARCHAR out, and volatile: it acts, so DuckDB must call it, not fold it into a constant.</summary>
    private static void RegisterScalar(nint connection, string name, int parameters,
                                       delegate* unmanaged[Cdecl]<nint, nint, nint, void> function)
    {
        nint scalar = DuckApi.CreateScalarFunction();
        try
        {
            DuckFunction.SetName(scalar, name, DuckApi.ScalarFunctionSetName);
            for (int i = 0; i < parameters; i++) DuckFunction.AddParameter(scalar, DuckType.Varchar, DuckApi.ScalarFunctionAddParameter);
            nint varchar = DuckApi.CreateType(DuckType.Varchar);
            DuckApi.ScalarFunctionSetReturnType(scalar, varchar);
            DuckApi.DestroyLogicalType(&varchar);
            DuckApi.ScalarFunctionSetVolatile(scalar);
            DuckApi.ScalarFunctionSetFunction(scalar, function);
            if (DuckApi.RegisterScalarFunction(connection, scalar) != 0)
                throw new InvalidOperationException($"DuckDB refused to register {name}");
        }
        finally
        {
            DuckApi.DestroyScalarFunction(&scalar);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Connect(nint info, nint input, nint output)
    {
        try
        {
            nint names = DuckApi.DataChunkGetVector(input, 0), strings = DuckApi.DataChunkGetVector(input, 1);
            for (ulong row = 0, rows = DuckApi.DataChunkGetSize(input); row < rows; row++)
            {
                string name = DuckFunction.ReadString(names, row) ?? throw new ArgumentException("daffy_connect needs a name, not NULL");
                string connectionString = DuckFunction.ReadString(strings, row)
                                          ?? throw new ArgumentException("daffy_connect needs a connection string, not NULL");
                DuckFunction.WriteString(output, row, DaffySql.Connect(name, connectionString));
            }
        }
        catch (Exception ex)
        {
            DuckApi.SetError(DuckApi.ScalarFunctionSetError, info, ex is ConnectException ? $"could not log in: {ex.Message}" : ex.Message);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Disconnect(nint info, nint input, nint output)
    {
        try
        {
            nint names = DuckApi.DataChunkGetVector(input, 0);
            for (ulong row = 0, rows = DuckApi.DataChunkGetSize(input); row < rows; row++)
            {
                string name = DuckFunction.ReadString(names, row) ?? throw new ArgumentException("daffy_disconnect needs a name, not NULL");
                if (!Connections.Remove(name, out int closed))
                    throw new InvalidOperationException($"there is no connection named '{name}'");
                DuckFunction.WriteString(output, row, $"{name}: disconnected, {closed} idle session(s) closed");
            }
        }
        catch (Exception ex)
        {
            DuckApi.SetError(DuckApi.ScalarFunctionSetError, info, ex.Message);
        }
    }

    // ---- daffy_connections() ----------------------------------------------------------------

    private sealed record Listed(string Name, string Server, string? Database, string Login, string Security,
                                 string? ServerVersion, string? RedirectedTo, int SessionsOpen, int SessionsIdle);

    private sealed class ListScanState(List<Listed> rows)
    {
        public List<Listed> Rows { get; } = rows;
        public int Next;
    }

    private static readonly (string Name, DuckType Type)[] ListColumns =
    [
        ("name", DuckType.Varchar), ("server", DuckType.Varchar), ("database", DuckType.Varchar), ("login", DuckType.Varchar),
        ("security", DuckType.Varchar), ("server_version", DuckType.Varchar), ("redirected_to", DuckType.Varchar),
        ("sessions_open", DuckType.Integer),
        ("sessions_idle", DuckType.Integer),
    ];

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ListBind(nint info)
    {
        try
        {
            foreach (var (name, type) in ListColumns) DuckFunction.AddResultColumn(info, name, type);
        }
        catch (Exception ex)
        {
            DuckApi.SetError(DuckApi.BindSetError, info, ex.Message);
        }
    }

    /// <summary>The list is taken when the scan starts, so the counts are as of then.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ListInit(nint info)
    {
        try
        {
            var rows = Connections.All.Select(c => new Listed(
                c.Name,
                c.Endpoint.Port == ServerEndpoint.DefaultPort ? c.Endpoint.Host : $"{c.Endpoint.Host},{c.Endpoint.Port}",
                c.Endpoint.Database,
                c.Endpoint.IsEntra ? c.Endpoint.ClientId : c.Endpoint.UserName,
                c.Endpoint.DescribeSecurity() + (c.Tls is null ? "" : $" ({c.Tls})"),
                c.LastLogin is { } login ? $"{login.ServerProgram.Trim()} {login.ServerVersion}" : null,
                c.RoutedTo?.ToString(),
                c.Open,
                c.Idle)).ToList();
            DuckApi.InitSetInitData(info, DuckFunction.Keep(new ListScanState(rows)), DuckFunction.Release);
        }
        catch (Exception ex)
        {
            DuckApi.SetError(DuckApi.InitSetError, info, ex.Message);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ListScan(nint info, nint chunk)
    {
        try
        {
            var scan = DuckFunction.Kept<ListScanState>(DuckApi.FunctionGetInitData(info));
            int count = Math.Min(scan.Rows.Count - scan.Next, (int)DuckApi.VectorSize());
            nint Vector(int column) => DuckApi.DataChunkGetVector(chunk, (ulong)column);

            for (int i = 0; i < count; i++)
            {
                var row = scan.Rows[scan.Next + i];
                ulong at = (ulong)i;
                DuckFunction.WriteString(Vector(0), at, row.Name);
                DuckFunction.WriteString(Vector(1), at, row.Server);
                DuckFunction.WriteString(Vector(2), at, row.Database);
                DuckFunction.WriteString(Vector(3), at, row.Login);
                DuckFunction.WriteString(Vector(4), at, row.Security);
                DuckFunction.WriteString(Vector(5), at, row.ServerVersion);
                DuckFunction.WriteString(Vector(6), at, row.RedirectedTo);
                ((int*)DuckApi.VectorGetData(Vector(7)))[i] = row.SessionsOpen;
                ((int*)DuckApi.VectorGetData(Vector(8)))[i] = row.SessionsIdle;
            }

            scan.Next += count;
            DuckApi.DataChunkSetSize(chunk, (ulong)count);
        }
        catch (Exception ex)
        {
            DuckApi.SetError(DuckApi.FunctionSetError, info, ex.Message);
        }
    }
}
