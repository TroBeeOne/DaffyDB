using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace DaffySQL.DuckDb;

/// <summary>
/// duckdb_hugeint: a 128-bit integer as DuckDB lays it out in a vector, low half first. It is the
/// storage of DECIMAL wider than 18 digits - SQL Server's money is DECIMAL(19,4), so it is common -
/// and of UUID.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DuckHugeInt
{
    public ulong Lower;
    public long Upper;

    public static DuckHugeInt From(Int128 value) => new() { Lower = (ulong)value, Upper = (long)(value >> 64) };

    /// <summary>
    /// DuckDB keeps a UUID as the 128-bit big-endian number its text spells, with the top bit
    /// flipped so that signed comparison sorts UUIDs the way their text sorts.
    /// </summary>
    public static DuckHugeInt FromUuid(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes, bigEndian: true, out _);
        return new DuckHugeInt
        {
            Upper = BinaryPrimitives.ReadInt64BigEndian(bytes) ^ long.MinValue,
            Lower = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]),
        };
    }
}

/// <summary>
/// duckdb_string_t, a VARCHAR or BLOB element as it sits in a vector: 16 bytes, the length first.
/// Up to 12 bytes are kept inline after the length; anything longer is a 4-byte prefix and a
/// pointer. Writing goes through duckdb_vector_assign_string_element_len, which copies, so this is
/// only ever read.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal unsafe struct DuckString
{
    public const int InlineLength = 12;

    [FieldOffset(0)] public uint Length;
    [FieldOffset(8)] public byte* Pointer;

    public static ReadOnlySpan<byte> Bytes(DuckString* value) => value->Length <= InlineLength
        ? new ReadOnlySpan<byte>((byte*)value + 4, (int)value->Length)
        : new ReadOnlySpan<byte>(value->Pointer, (int)value->Length);

    public static string Text(DuckString* value) => Encoding.UTF8.GetString(Bytes(value));
}

/// <summary>A NUL-terminated UTF-8 copy of a string, for the C API's const char* parameters.</summary>
internal readonly unsafe struct NativeUtf8 : IDisposable
{
    public NativeUtf8(string value)
    {
        int length = Encoding.UTF8.GetByteCount(value);
        Pointer = (byte*)NativeMemory.Alloc((nuint)length + 1);
        Encoding.UTF8.GetBytes(value, new Span<byte>(Pointer, length));
        Pointer[length] = 0;
    }

    public byte* Pointer { get; }

    public void Dispose() => NativeMemory.Free(Pointer);
}
