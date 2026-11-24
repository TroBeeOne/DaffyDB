using System.Diagnostics;
using System.Runtime.InteropServices;
using DuckDB.NET.Data;

namespace DaffySQL.DuckDb;

/// <summary>
/// DuckDB's C API, as function pointers into the very DuckDB that DuckDB.NET loaded.
///
/// DaffySQL registers its functions through the raw C API rather than DuckDB.NET's
/// RegisterTableFunction, because the raw API can say what DuckDB.NET's cannot - DECIMAL(p,s) with
/// SQL Server's own precision and scale, TIMESTAMP_NS, which columns a query actually needs - and
/// rows go straight into DuckDB's vectors, with no .NET object per row. Everything DaffySQL calls
/// in DuckDB goes through this one class, so nothing else names DuckDB.NET's bindings, and a
/// change in them is a change here.
///
/// The pointers come from the DuckDB module already in the process, found by name and opened by
/// its full path, never by a fresh library search: a second copy of DuckDB in one process would
/// have its own globals, and a connection from one used with the other is undefined behaviour.
/// They are ordinary exports, but the signatures are those of an extension's function table,
/// which is what would let a loadable extension fill this class instead, should one ever exist.
///
/// Handles are nint, C's bool is byte and idx_t is ulong, so no call marshals anything. Callbacks
/// DuckDB makes into .NET are [UnmanagedCallersOnly] and must never let an exception out: DuckDB
/// is C++ underneath, and an exception crossing into it ends the process. Each one catches and
/// reports through the matching *SetError.
/// </summary>
internal static unsafe class DuckApi
{
    private static readonly Lock Gate = new();

    /// <summary>The DuckDB module the pointers point into.</summary>
    public static string? ModulePath { get; private set; }

    public static bool IsLoaded => ModulePath is not null;

    // ---- the library ------------------------------------------------------------------------

    public static delegate* unmanaged[Cdecl]<byte*> LibraryVersion;
    public static delegate* unmanaged[Cdecl]<void*, void> Free;

    /// <summary>Rows per vector: 2048 in every release so far, but asked rather than assumed.</summary>
    public static delegate* unmanaged[Cdecl]<ulong> VectorSize;

    // ---- logical types and values -----------------------------------------------------------

    public static delegate* unmanaged[Cdecl]<int, nint> CreateLogicalType;
    public static delegate* unmanaged[Cdecl]<byte, byte, nint> CreateDecimalType;
    public static delegate* unmanaged[Cdecl]<nint*, void> DestroyLogicalType;

    public static delegate* unmanaged[Cdecl]<nint, long> GetInt64;
    public static delegate* unmanaged[Cdecl]<nint, byte*> GetVarchar;
    public static delegate* unmanaged[Cdecl]<nint, byte> IsNullValue;
    public static delegate* unmanaged[Cdecl]<nint*, void> DestroyValue;

    // ---- client contexts: which connection a query is running on ----------------------------

    public static delegate* unmanaged[Cdecl]<nint, nint*, void> ConnectionGetClientContext;
    public static delegate* unmanaged[Cdecl]<nint, nint*, void> TableFunctionGetClientContext;
    public static delegate* unmanaged[Cdecl]<nint, ulong> ClientContextGetConnectionId;
    public static delegate* unmanaged[Cdecl]<nint*, void> DestroyClientContext;

    // ---- table functions --------------------------------------------------------------------

    public static delegate* unmanaged[Cdecl]<nint> CreateTableFunction;
    public static delegate* unmanaged[Cdecl]<nint*, void> DestroyTableFunction;
    public static delegate* unmanaged[Cdecl]<nint, byte*, void> TableFunctionSetName;
    public static delegate* unmanaged[Cdecl]<nint, nint, void> TableFunctionAddParameter;
    public static delegate* unmanaged[Cdecl]<nint, byte*, nint, void> TableFunctionAddNamedParameter;
    public static delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, void>, void> TableFunctionSetBind;
    public static delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, void>, void> TableFunctionSetInit;
    public static delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, nint, void>, void> TableFunctionSetFunction;
    public static delegate* unmanaged[Cdecl]<nint, byte, void> TableFunctionSupportsProjectionPushdown;
    public static delegate* unmanaged[Cdecl]<nint, nint, int> RegisterTableFunction;

    // Bind: once per query, before anything runs. Reads the arguments and declares the columns.
    public static delegate* unmanaged[Cdecl]<nint, byte*, nint, void> BindAddResultColumn;
    public static delegate* unmanaged[Cdecl]<nint, ulong> BindGetParameterCount;
    public static delegate* unmanaged[Cdecl]<nint, ulong, nint> BindGetParameter;
    public static delegate* unmanaged[Cdecl]<nint, byte*, nint> BindGetNamedParameter;
    public static delegate* unmanaged[Cdecl]<nint, void*, delegate* unmanaged[Cdecl]<void*, void>, void> BindSetBindData;
    public static delegate* unmanaged[Cdecl]<nint, ulong, byte, void> BindSetCardinality;
    public static delegate* unmanaged[Cdecl]<nint, byte*, void> BindSetError;

    // Init: once per scan, when it starts. Learns which columns the query needs.
    public static delegate* unmanaged[Cdecl]<nint, void*> InitGetBindData;
    public static delegate* unmanaged[Cdecl]<nint, void*, delegate* unmanaged[Cdecl]<void*, void>, void> InitSetInitData;
    public static delegate* unmanaged[Cdecl]<nint, ulong> InitGetColumnCount;
    public static delegate* unmanaged[Cdecl]<nint, ulong, ulong> InitGetColumnIndex;
    public static delegate* unmanaged[Cdecl]<nint, byte*, void> InitSetError;

    // Function: called for each chunk of rows until one comes back empty.
    public static delegate* unmanaged[Cdecl]<nint, void*> FunctionGetBindData;
    public static delegate* unmanaged[Cdecl]<nint, void*> FunctionGetInitData;
    public static delegate* unmanaged[Cdecl]<nint, byte*, void> FunctionSetError;

    // ---- scalar functions -------------------------------------------------------------------

    public static delegate* unmanaged[Cdecl]<nint> CreateScalarFunction;
    public static delegate* unmanaged[Cdecl]<nint*, void> DestroyScalarFunction;
    public static delegate* unmanaged[Cdecl]<nint, byte*, void> ScalarFunctionSetName;
    public static delegate* unmanaged[Cdecl]<nint, nint, void> ScalarFunctionAddParameter;
    public static delegate* unmanaged[Cdecl]<nint, nint, void> ScalarFunctionSetReturnType;
    public static delegate* unmanaged[Cdecl]<nint, void> ScalarFunctionSetVolatile;
    public static delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, nint, nint, void>, void> ScalarFunctionSetFunction;
    public static delegate* unmanaged[Cdecl]<nint, byte*, void> ScalarFunctionSetError;
    public static delegate* unmanaged[Cdecl]<nint, nint, int> RegisterScalarFunction;

    // ---- data chunks and vectors ------------------------------------------------------------

    public static delegate* unmanaged[Cdecl]<nint, ulong> DataChunkGetSize;
    public static delegate* unmanaged[Cdecl]<nint, ulong, void> DataChunkSetSize;
    public static delegate* unmanaged[Cdecl]<nint, ulong, nint> DataChunkGetVector;
    public static delegate* unmanaged[Cdecl]<nint, void*> VectorGetData;
    public static delegate* unmanaged[Cdecl]<nint, ulong*> VectorGetValidity;
    public static delegate* unmanaged[Cdecl]<nint, void> VectorEnsureValidityWritable;
    public static delegate* unmanaged[Cdecl]<nint, ulong, byte*, ulong, void> VectorAssignStringElementLen;
    public static delegate* unmanaged[Cdecl]<ulong*, ulong, byte> ValidityRowIsValid;
    public static delegate* unmanaged[Cdecl]<ulong*, ulong, void> ValiditySetRowInvalid;

    /// <summary>
    /// Resolves every pointer, once per process. <paramref name="connection"/> must be open: that is
    /// what makes DuckDB.NET load DuckDB, and it is that copy this binds to.
    /// </summary>
    public static void Load(DuckDBConnection connection)
    {
        lock (Gate)
        {
            if (IsLoaded) return;
            if (connection.State != System.Data.ConnectionState.Open)
                throw new InvalidOperationException("DuckApi.Load needs an open connection, so that DuckDB is already loaded");

            var modules = FindModules();
            string path = modules.Count switch
            {
                1 => modules[0],
                0 => throw new InvalidOperationException("no DuckDB module is loaded in this process"),
                _ => throw new InvalidOperationException($"{modules.Count} DuckDB modules are loaded in this process: {string.Join(", ", modules)}"),
            };

            nint library = NativeLibrary.Load(path);
            var missing = new List<string>();

            nint Get(string name)
            {
                if (NativeLibrary.TryGetExport(library, name, out nint address)) return address;
                missing.Add(name);
                return 0;
            }

            LibraryVersion = (delegate* unmanaged[Cdecl]<byte*>)Get("duckdb_library_version");
            Free = (delegate* unmanaged[Cdecl]<void*, void>)Get("duckdb_free");
            VectorSize = (delegate* unmanaged[Cdecl]<ulong>)Get("duckdb_vector_size");

            CreateLogicalType = (delegate* unmanaged[Cdecl]<int, nint>)Get("duckdb_create_logical_type");
            CreateDecimalType = (delegate* unmanaged[Cdecl]<byte, byte, nint>)Get("duckdb_create_decimal_type");
            DestroyLogicalType = (delegate* unmanaged[Cdecl]<nint*, void>)Get("duckdb_destroy_logical_type");

            GetInt64 = (delegate* unmanaged[Cdecl]<nint, long>)Get("duckdb_get_int64");
            GetVarchar = (delegate* unmanaged[Cdecl]<nint, byte*>)Get("duckdb_get_varchar");
            IsNullValue = (delegate* unmanaged[Cdecl]<nint, byte>)Get("duckdb_is_null_value");
            DestroyValue = (delegate* unmanaged[Cdecl]<nint*, void>)Get("duckdb_destroy_value");

            ConnectionGetClientContext = (delegate* unmanaged[Cdecl]<nint, nint*, void>)Get("duckdb_connection_get_client_context");
            TableFunctionGetClientContext = (delegate* unmanaged[Cdecl]<nint, nint*, void>)Get("duckdb_table_function_get_client_context");
            ClientContextGetConnectionId = (delegate* unmanaged[Cdecl]<nint, ulong>)Get("duckdb_client_context_get_connection_id");
            DestroyClientContext = (delegate* unmanaged[Cdecl]<nint*, void>)Get("duckdb_destroy_client_context");

            CreateTableFunction = (delegate* unmanaged[Cdecl]<nint>)Get("duckdb_create_table_function");
            DestroyTableFunction = (delegate* unmanaged[Cdecl]<nint*, void>)Get("duckdb_destroy_table_function");
            TableFunctionSetName = (delegate* unmanaged[Cdecl]<nint, byte*, void>)Get("duckdb_table_function_set_name");
            TableFunctionAddParameter = (delegate* unmanaged[Cdecl]<nint, nint, void>)Get("duckdb_table_function_add_parameter");
            TableFunctionAddNamedParameter = (delegate* unmanaged[Cdecl]<nint, byte*, nint, void>)Get("duckdb_table_function_add_named_parameter");
            TableFunctionSetBind = (delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, void>, void>)Get("duckdb_table_function_set_bind");
            TableFunctionSetInit = (delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, void>, void>)Get("duckdb_table_function_set_init");
            TableFunctionSetFunction = (delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, nint, void>, void>)Get("duckdb_table_function_set_function");
            TableFunctionSupportsProjectionPushdown = (delegate* unmanaged[Cdecl]<nint, byte, void>)Get("duckdb_table_function_supports_projection_pushdown");
            RegisterTableFunction = (delegate* unmanaged[Cdecl]<nint, nint, int>)Get("duckdb_register_table_function");

            BindAddResultColumn = (delegate* unmanaged[Cdecl]<nint, byte*, nint, void>)Get("duckdb_bind_add_result_column");
            BindGetParameterCount = (delegate* unmanaged[Cdecl]<nint, ulong>)Get("duckdb_bind_get_parameter_count");
            BindGetParameter = (delegate* unmanaged[Cdecl]<nint, ulong, nint>)Get("duckdb_bind_get_parameter");
            BindGetNamedParameter = (delegate* unmanaged[Cdecl]<nint, byte*, nint>)Get("duckdb_bind_get_named_parameter");
            BindSetBindData = (delegate* unmanaged[Cdecl]<nint, void*, delegate* unmanaged[Cdecl]<void*, void>, void>)Get("duckdb_bind_set_bind_data");
            BindSetCardinality = (delegate* unmanaged[Cdecl]<nint, ulong, byte, void>)Get("duckdb_bind_set_cardinality");
            BindSetError = (delegate* unmanaged[Cdecl]<nint, byte*, void>)Get("duckdb_bind_set_error");

            InitGetBindData = (delegate* unmanaged[Cdecl]<nint, void*>)Get("duckdb_init_get_bind_data");
            InitSetInitData = (delegate* unmanaged[Cdecl]<nint, void*, delegate* unmanaged[Cdecl]<void*, void>, void>)Get("duckdb_init_set_init_data");
            InitGetColumnCount = (delegate* unmanaged[Cdecl]<nint, ulong>)Get("duckdb_init_get_column_count");
            InitGetColumnIndex = (delegate* unmanaged[Cdecl]<nint, ulong, ulong>)Get("duckdb_init_get_column_index");
            InitSetError = (delegate* unmanaged[Cdecl]<nint, byte*, void>)Get("duckdb_init_set_error");

            FunctionGetBindData = (delegate* unmanaged[Cdecl]<nint, void*>)Get("duckdb_function_get_bind_data");
            FunctionGetInitData = (delegate* unmanaged[Cdecl]<nint, void*>)Get("duckdb_function_get_init_data");
            FunctionSetError = (delegate* unmanaged[Cdecl]<nint, byte*, void>)Get("duckdb_function_set_error");

            CreateScalarFunction = (delegate* unmanaged[Cdecl]<nint>)Get("duckdb_create_scalar_function");
            DestroyScalarFunction = (delegate* unmanaged[Cdecl]<nint*, void>)Get("duckdb_destroy_scalar_function");
            ScalarFunctionSetName = (delegate* unmanaged[Cdecl]<nint, byte*, void>)Get("duckdb_scalar_function_set_name");
            ScalarFunctionAddParameter = (delegate* unmanaged[Cdecl]<nint, nint, void>)Get("duckdb_scalar_function_add_parameter");
            ScalarFunctionSetReturnType = (delegate* unmanaged[Cdecl]<nint, nint, void>)Get("duckdb_scalar_function_set_return_type");
            ScalarFunctionSetVolatile = (delegate* unmanaged[Cdecl]<nint, void>)Get("duckdb_scalar_function_set_volatile");
            ScalarFunctionSetFunction = (delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, nint, nint, void>, void>)Get("duckdb_scalar_function_set_function");
            ScalarFunctionSetError = (delegate* unmanaged[Cdecl]<nint, byte*, void>)Get("duckdb_scalar_function_set_error");
            RegisterScalarFunction = (delegate* unmanaged[Cdecl]<nint, nint, int>)Get("duckdb_register_scalar_function");

            DataChunkGetSize = (delegate* unmanaged[Cdecl]<nint, ulong>)Get("duckdb_data_chunk_get_size");
            DataChunkSetSize = (delegate* unmanaged[Cdecl]<nint, ulong, void>)Get("duckdb_data_chunk_set_size");
            DataChunkGetVector = (delegate* unmanaged[Cdecl]<nint, ulong, nint>)Get("duckdb_data_chunk_get_vector");
            VectorGetData = (delegate* unmanaged[Cdecl]<nint, void*>)Get("duckdb_vector_get_data");
            VectorGetValidity = (delegate* unmanaged[Cdecl]<nint, ulong*>)Get("duckdb_vector_get_validity");
            VectorEnsureValidityWritable = (delegate* unmanaged[Cdecl]<nint, void>)Get("duckdb_vector_ensure_validity_writable");
            VectorAssignStringElementLen = (delegate* unmanaged[Cdecl]<nint, ulong, byte*, ulong, void>)Get("duckdb_vector_assign_string_element_len");
            ValidityRowIsValid = (delegate* unmanaged[Cdecl]<ulong*, ulong, byte>)Get("duckdb_validity_row_is_valid");
            ValiditySetRowInvalid = (delegate* unmanaged[Cdecl]<ulong*, ulong, void>)Get("duckdb_validity_set_row_invalid");

            if (missing.Count > 0)
            {
                NativeLibrary.Free(library);
                throw new MissingMethodException($"{path} does not export {string.Join(", ", missing)}");
            }

            // The library stays loaded for the life of the process, as DuckDB.NET's own use keeps it.
            ModulePath = path;
        }
    }

    /// <summary>Every DuckDB module mapped into this process, by full path. One, when all is well.</summary>
    public static List<string> FindModules()
    {
        string[] names = ["duckdb.dll", "libduckdb.so", "libduckdb.dylib"];
        using var process = Process.GetCurrentProcess();
        return process.Modules.Cast<ProcessModule>()
            .Select(m => m.FileName)
            .Where(p => names.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The duckdb_connection under a DuckDB.NET connection.</summary>
    public static nint Handle(DuckDBConnection connection) => connection.NativeConnection.DangerousGetHandle();

    /// <summary>
    /// DuckDB's id for a connection: what a table function's bind sees as the connection its query
    /// runs on, so a scan can be found again from the connection a cancel arrives on.
    /// </summary>
    public static ulong ConnectionId(DuckDBConnection connection)
    {
        nint context = 0;
        ConnectionGetClientContext(Handle(connection), &context);
        try { return ClientContextGetConnectionId(context); }
        finally { DestroyClientContext(&context); }
    }

    public static string Version() => Marshal.PtrToStringUTF8((nint)LibraryVersion()) ?? "";

    /// <summary>A logical type the caller destroys; DuckDB copies whatever it is handed.</summary>
    public static nint CreateType(DuckType type, byte width = 0, byte scale = 0) =>
        type == DuckType.Decimal ? CreateDecimalType(width, scale) : CreateLogicalType((int)type);

    /// <summary>Reports an error through one of the *SetError functions. DuckDB copies the message.</summary>
    public static void SetError(delegate* unmanaged[Cdecl]<nint, byte*, void> setError, nint info, string message)
    {
        using var text = new NativeUtf8(message);
        setError(info, text.Pointer);
    }

    /// <summary>A parameter's value as a long, consuming it; null when absent or SQL NULL.</summary>
    public static long? TakeInt64(nint value)
    {
        if (value == 0) return null;
        try { return IsNullValue(value) != 0 ? null : GetInt64(value); }
        finally { DestroyValue(&value); }
    }

    /// <summary>A parameter's value as text, consuming it; null when absent or SQL NULL.</summary>
    public static string? TakeString(nint value)
    {
        if (value == 0) return null;
        try
        {
            if (IsNullValue(value) != 0) return null;
            byte* text = GetVarchar(value);
            try { return Marshal.PtrToStringUTF8((nint)text); }
            finally { Free(text); }
        }
        finally { DestroyValue(&value); }
    }

    /// <summary>A named parameter, or 0 when the query did not give it.</summary>
    public static nint NamedParameter(nint info, string name)
    {
        using var text = new NativeUtf8(name);
        return BindGetNamedParameter(info, text.Pointer);
    }
}
