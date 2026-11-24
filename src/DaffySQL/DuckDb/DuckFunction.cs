using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace DaffySQL.DuckDb;

/// <summary>What every function DaffySQL registers needs: state DuckDB holds for it, and the small
/// repeated steps of declaring parameters and columns and moving strings in and out of vectors.</summary>
internal static unsafe class DuckFunction
{
    private static readonly byte[] Empty = [0];

    /// <summary>A .NET object as the void* DuckDB keeps, until DuckDB calls <see cref="Release"/> on it.</summary>
    public static void* Keep(object state) => (void*)GCHandle.ToIntPtr(GCHandle.Alloc(state));

    public static T Kept<T>(void* state) where T : class => (T)GCHandle.FromIntPtr((nint)state).Target!;

    /// <summary>The delete callback for anything <see cref="Keep"/> handed DuckDB: disposed if disposable, then let go.</summary>
    public static readonly delegate* unmanaged[Cdecl]<void*, void> Release = &ReleaseState;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ReleaseState(void* state)
    {
        var handle = GCHandle.FromIntPtr((nint)state);
        try
        {
            (handle.Target as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug("state.release_failed", ex.Message);
        }
        finally
        {
            handle.Free();
        }
    }

    public static void SetName(nint function, string name, delegate* unmanaged[Cdecl]<nint, byte*, void> setName)
    {
        using var text = new NativeUtf8(name);
        setName(function, text.Pointer);
    }

    public static void AddParameter(nint function, DuckType type, delegate* unmanaged[Cdecl]<nint, nint, void> addParameter)
    {
        nint logical = DuckApi.CreateType(type);
        addParameter(function, logical);
        DuckApi.DestroyLogicalType(&logical);
    }

    public static void AddNamedParameter(nint function, string name, DuckType type)
    {
        nint logical = DuckApi.CreateType(type);
        using var text = new NativeUtf8(name);
        DuckApi.TableFunctionAddNamedParameter(function, text.Pointer, logical);
        DuckApi.DestroyLogicalType(&logical);
    }

    public static void AddResultColumn(nint info, string name, DuckType type, byte width = 0, byte scale = 0)
    {
        nint logical = DuckApi.CreateType(type, width, scale);
        using var text = new NativeUtf8(name);
        DuckApi.BindAddResultColumn(info, text.Pointer, logical);
        DuckApi.DestroyLogicalType(&logical);
    }

    /// <summary>The DuckDB connection a table function's query runs on, from its bind: the key a cancel finds it by.</summary>
    public static ulong ConnectionId(nint bindInfo)
    {
        nint context = 0;
        DuckApi.TableFunctionGetClientContext(bindInfo, &context);
        try { return DuckApi.ClientContextGetConnectionId(context); }
        finally { DuckApi.DestroyClientContext(&context); }
    }

    /// <summary>A VARCHAR parameter of a table function, consumed; refused when NULL, naming what it should have been.</summary>
    public static string Parameter(nint bindInfo, int index, string function, string what) =>
        DuckApi.TakeString(DuckApi.BindGetParameter(bindInfo, (ulong)index))
        ?? throw new ArgumentException($"{function} needs {what}, not NULL");

    /// <summary>A VARCHAR argument of a scalar function; null for SQL NULL.</summary>
    public static string? ReadString(nint vector, ulong row)
    {
        ulong* validity = DuckApi.VectorGetValidity(vector);   // null: every row valid
        if (validity is not null && DuckApi.ValidityRowIsValid(validity, row) == 0) return null;
        return DuckString.Text((DuckString*)DuckApi.VectorGetData(vector) + row);
    }

    public static void WriteString(nint vector, ulong row, string? value)
    {
        if (value is null)
        {
            DuckApi.VectorEnsureValidityWritable(vector);
            DuckApi.ValiditySetRowInvalid(DuckApi.VectorGetValidity(vector), row);
            return;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(value);
        fixed (byte* pointer = bytes.Length == 0 ? Empty : bytes)
            DuckApi.VectorAssignStringElementLen(vector, row, pointer, (ulong)bytes.Length);
    }
}
