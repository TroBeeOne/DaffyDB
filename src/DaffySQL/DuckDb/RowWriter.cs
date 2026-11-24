using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;
using DaffySQL.Tds;

namespace DaffySQL.DuckDb;

/// <summary>
/// Decodes ROW and NBCROW tokens straight into the vectors of the chunk DuckDB is filling - no
/// .NET object per value, and nothing kept between chunks but scratch buffers.
///
/// Every column's bytes are read, because that is the only way to reach the next one, but a column
/// the query does not need (DuckDB's projection) is skipped rather than converted: no UTF-8 for a
/// string nobody reads.
///
/// One per scan: the scratch buffers are not shared between threads.
/// </summary>
internal sealed unsafe class RowWriter
{
    private const long MicrosPerDay = 86_400_000_000;
    private const int DaysFrom0001To1970 = 719_162;
    private const int DaysFrom1900To1970 = 25_567;
    private static readonly long[] PowersOf10 = [1, 10, 100, 1_000, 10_000, 100_000, 1_000_000, 10_000_000];
    private static readonly byte[] Empty = [0];

    private readonly ColumnPlan[] _plans;
    private readonly int[] _slotOf;       // column -> its vector in the chunk, or -1 when not projected
    private readonly int[] _columnOf;     // vector in the chunk -> column
    private readonly nint[] _vectors;
    private readonly nint[] _data;
    private readonly nint[] _validity;    // 0 until the first NULL of the chunk makes it writable

    private byte[] _bitmap = new byte[8];
    private byte[] _bytes = new byte[1024];
    private char[] _chars = new char[512];
    private byte[] _plp = new byte[8192];

    /// <param name="projection">The columns DuckDB asked for, in the order of its chunk's vectors.</param>
    public RowWriter(ColumnPlan[] plans, IReadOnlyList<int> projection)
    {
        _plans = plans;
        _slotOf = Enumerable.Repeat(-1, plans.Length).ToArray();
        _columnOf = projection.ToArray();
        for (int slot = 0; slot < _columnOf.Length; slot++)
            if (_columnOf[slot] < plans.Length) _slotOf[_columnOf[slot]] = slot;
        _vectors = new nint[_columnOf.Length];
        _data = new nint[_columnOf.Length];
        _validity = new nint[_columnOf.Length];
    }

    /// <summary>Takes the vectors of the chunk the next rows go into.</summary>
    public void Begin(nint chunk)
    {
        for (int slot = 0; slot < _vectors.Length; slot++)
        {
            _vectors[slot] = DuckApi.DataChunkGetVector(chunk, (ulong)slot);
            _data[slot] = (nint)DuckApi.VectorGetData(_vectors[slot]);
            _validity[slot] = 0;
        }
    }

    /// <summary>One row, its token already read. NBCROW starts with a bitmap of the columns that are NULL.</summary>
    public void Row(ResponseReader reader, bool nullBitmap, int row)
    {
        if (nullBitmap)
        {
            int length = (_plans.Length + 7) / 8;
            if (_bitmap.Length < length) _bitmap = new byte[length];
            reader.ReadInto(_bitmap.AsSpan(0, length));
        }

        for (int column = 0; column < _plans.Length; column++)
        {
            int slot = _slotOf[column];
            if (nullBitmap && (_bitmap[column >> 3] & (1 << (column & 7))) != 0)
            {
                if (slot >= 0) SetNull(slot, row);
                continue;
            }
            Value(reader, _plans[column], slot, row);
        }
    }

    private void Value(ResponseReader reader, ColumnPlan plan, int slot, int row)
    {
        void* data = slot >= 0 ? (void*)_data[slot] : null;
        switch (plan.Kind)
        {
            case ValueKind.UTinyInt: Put(data, row, reader.ReadByte()); return;
            case ValueKind.SmallInt: Put(data, row, (short)reader.ReadUInt16()); return;
            case ValueKind.Integer: Put(data, row, reader.ReadInt32()); return;
            case ValueKind.BigInt: Put(data, row, (long)reader.ReadUInt64()); return;
            case ValueKind.Bit: Put(data, row, reader.ReadByte() != 0 ? (byte)1 : (byte)0); return;
            case ValueKind.Real: Put(data, row, BinaryPrimitives.ReadSingleLittleEndian(reader.Read(4))); return;
            case ValueKind.Float: Put(data, row, BinaryPrimitives.ReadDoubleLittleEndian(reader.Read(8))); return;
            case ValueKind.Money: PutDecimal(data, row, plan, Money(reader.Read(8))); return;
            case ValueKind.SmallMoney: PutDecimal(data, row, plan, reader.ReadInt32()); return;
            case ValueKind.DateTime: Put(data, row, DateTimeMicros(reader.Read(8))); return;
            case ValueKind.SmallDateTime: Put(data, row, SmallDateTimeMicros(reader.Read(4))); return;
        }

        if (plan.Kind is ValueKind.Chars or ValueKind.Binary)
        {
            ushort length = reader.ReadUInt16();
            if (length == 0xFFFF) Null(slot, row);
            else if (slot < 0) reader.Skip(length);
            else PutBytes(plan, slot, row, reader.Read(length));
            return;
        }

        if (plan.Kind is ValueKind.PlpChars or ValueKind.PlpBinary)
        {
            ulong total = reader.ReadUInt64();
            if (total == ulong.MaxValue) Null(slot, row);
            else if (slot < 0) ReadPlp(reader, keep: false);
            else PutBytes(plan, slot, row, ReadPlp(reader, keep: true));
            return;
        }

        if (plan.Kind is ValueKind.Text or ValueKind.Image)
        {
            byte pointer = reader.ReadByte();
            if (pointer == 0)
            {
                Null(slot, row);
                return;
            }
            reader.Skip(pointer + 8);   // the text pointer, then its timestamp
            int length = reader.ReadInt32();
            if (slot < 0) reader.Skip(length);
            else PutBytes(plan, slot, row, reader.Read(length));
            return;
        }

        if (plan.Kind == ValueKind.Variant)
        {
            int length = reader.ReadInt32();
            if (length == 0) Null(slot, row);
            else if (slot < 0) reader.Skip(length);
            else PutUtf16(slot, row, VariantText(TypeInfoReader.VariantValue(reader.Read(length))));
            return;
        }

        // Everything else: a byte length, 0 for NULL.
        int size = reader.ReadByte();
        if (size == 0)
        {
            Null(slot, row);
            return;
        }

        var value = reader.Read(size);
        if (slot < 0) return;

        switch (plan.Kind)
        {
            case ValueKind.IntN:
                long integer = size switch
                {
                    1 => value[0],
                    2 => BinaryPrimitives.ReadInt16LittleEndian(value),
                    4 => BinaryPrimitives.ReadInt32LittleEndian(value),
                    _ => BinaryPrimitives.ReadInt64LittleEndian(value),
                };
                switch (plan.DuckType)
                {
                    case DuckType.UTinyInt: Put(data, row, (byte)integer); break;
                    case DuckType.SmallInt: Put(data, row, (short)integer); break;
                    case DuckType.Integer: Put(data, row, (int)integer); break;
                    default: Put(data, row, integer); break;
                }
                return;

            case ValueKind.BitN:
                Put(data, row, value[0] != 0 ? (byte)1 : (byte)0);
                return;

            case ValueKind.FloatN:
                double real = size == 4 ? BinaryPrimitives.ReadSingleLittleEndian(value) : BinaryPrimitives.ReadDoubleLittleEndian(value);
                if (plan.DuckType == DuckType.Float) Put(data, row, (float)real);
                else Put(data, row, real);
                return;

            case ValueKind.MoneyN:
                PutDecimal(data, row, plan, size == 4 ? BinaryPrimitives.ReadInt32LittleEndian(value) : Money(value));
                return;

            case ValueKind.DateTimeN:
                Put(data, row, size == 4 ? SmallDateTimeMicros(value) : DateTimeMicros(value));
                return;

            case ValueKind.Date:
                Put(data, row, Days(value) - DaysFrom0001To1970);
                return;

            case ValueKind.Time:
                Put(data, row, TimeMicros(value, plan.TimeScale));
                return;

            case ValueKind.DateTime2:
                Put(data, row, (Days(value[^3..]) - (long)DaysFrom0001To1970) * MicrosPerDay + TimeMicros(value[..^3], plan.TimeScale));
                return;

            case ValueKind.DateTimeOffset:
                // The date and time are UTC; the trailing 2-byte offset only says how it was written.
                Put(data, row, (Days(value[^5..^2]) - (long)DaysFrom0001To1970) * MicrosPerDay + TimeMicros(value[..^5], plan.TimeScale));
                return;

            case ValueKind.Decimal:
            {
                // Sign (1 positive, 0 negative), then the magnitude, little-endian: 4, 8, 12 or 16 bytes.
                Span<byte> magnitude = stackalloc byte[16];
                magnitude.Clear();
                value[1..].CopyTo(magnitude);
                var unscaled = (Int128)new UInt128(BinaryPrimitives.ReadUInt64LittleEndian(magnitude[8..]),
                                                   BinaryPrimitives.ReadUInt64LittleEndian(magnitude));
                PutDecimal(data, row, plan, value[0] == 0 ? -unscaled : unscaled);
                return;
            }

            case ValueKind.Guid:
                ((DuckHugeInt*)data)[row] = DuckHugeInt.FromUuid(new Guid(value));
                return;

            default:
                throw new InvalidDataException($"no reader for {plan.SqlType}");
        }
    }

    // ---- writing into vectors ---------------------------------------------------------------

    private static void Put<T>(void* data, int row, T value) where T : unmanaged
    {
        if (data is not null) ((T*)data)[row] = value;
    }

    /// <summary>An unscaled decimal in the storage DuckDB uses for the width: 2, 4, 8 or 16 bytes.</summary>
    private static void PutDecimal(void* data, int row, ColumnPlan plan, Int128 unscaled)
    {
        if (data is null) return;
        if (plan.Width <= 4) ((short*)data)[row] = (short)unscaled;
        else if (plan.Width <= 9) ((int*)data)[row] = (int)unscaled;
        else if (plan.Width <= 18) ((long*)data)[row] = (long)unscaled;
        else ((DuckHugeInt*)data)[row] = DuckHugeInt.From(unscaled);
    }

    private void Null(int slot, int row)
    {
        if (slot >= 0) SetNull(slot, row);
    }

    private void SetNull(int slot, int row)
    {
        if (_validity[slot] == 0)
        {
            DuckApi.VectorEnsureValidityWritable(_vectors[slot]);
            _validity[slot] = (nint)DuckApi.VectorGetValidity(_vectors[slot]);
        }
        DuckApi.ValiditySetRowInvalid((ulong*)_validity[slot], (ulong)row);
    }

    private void PutBytes(ColumnPlan plan, int slot, int row, ReadOnlySpan<byte> bytes)
    {
        if (plan.DuckType == DuckType.Blob)
        {
            Assign(slot, row, bytes);
            return;
        }

        switch (plan.Chars)
        {
            case CharEncoding.Utf16:
                PutUtf16(slot, row, MemoryMarshal.Cast<byte, char>(bytes[..(bytes.Length & ~1)]));
                break;

            case CharEncoding.Utf8 when Utf8.IsValid(bytes):
                Assign(slot, row, bytes);
                break;

            case CharEncoding.Utf8:
                // Not valid UTF-8 after all - DuckDB would refuse it - so through a decode that
                // replaces what cannot be read, as every other path does.
                PutUtf16(slot, row, Encoding.UTF8.GetString(bytes));
                break;

            default:
                // ASCII is the same in every code page SQL Server uses, and is already UTF-8.
                if (System.Text.Ascii.IsValid(bytes))
                {
                    Assign(slot, row, bytes);
                    break;
                }
                var codePage = plan.CodePage!;
                int count = codePage.GetMaxCharCount(bytes.Length);
                if (_chars.Length < count) _chars = new char[Math.Max(count, _chars.Length * 2)];
                int chars = codePage.GetChars(bytes, _chars);
                PutUtf16(slot, row, _chars.AsSpan(0, chars));
                break;
        }
    }

    private void PutUtf16(int slot, int row, ReadOnlySpan<char> text)
    {
        int most = Encoding.UTF8.GetMaxByteCount(text.Length);
        if (_bytes.Length < most) _bytes = new byte[Math.Max(most, _bytes.Length * 2)];
        int length = Encoding.UTF8.GetBytes(text, _bytes);
        Assign(slot, row, _bytes.AsSpan(0, length));
    }

    /// <summary>DuckDB copies the bytes into the vector; a zero-length value still gets a real pointer.</summary>
    private void Assign(int slot, int row, ReadOnlySpan<byte> bytes)
    {
        fixed (byte* pointer = bytes.IsEmpty ? Empty : bytes)
            DuckApi.VectorAssignStringElementLen(_vectors[slot], (ulong)row, pointer, (ulong)bytes.Length);
    }

    /// <summary>PLP chunks, each with a 4-byte length, until a zero one. Gathered only when the value is wanted.</summary>
    private ReadOnlySpan<byte> ReadPlp(ResponseReader reader, bool keep)
    {
        int length = 0;
        while (true)
        {
            uint chunk = reader.ReadUInt32();
            if (chunk == 0) break;
            if (!keep)
            {
                reader.Skip(chunk);
                continue;
            }
            if (_plp.Length < length + (int)chunk) Array.Resize(ref _plp, Math.Max(length + (int)chunk, _plp.Length * 2));
            reader.ReadInto(_plp.AsSpan(length, (int)chunk));
            length += (int)chunk;
        }
        return _plp.AsSpan(0, length);
    }

    // ---- SQL Server's encodings -------------------------------------------------------------

    /// <summary>money: the high 4 bytes first, then the low; the whole in ten-thousandths.</summary>
    private static long Money(ReadOnlySpan<byte> data) =>
        (long)BinaryPrimitives.ReadInt32LittleEndian(data) << 32 | BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);

    private static int Days(ReadOnlySpan<byte> data) => data[0] | (data[1] << 8) | (data[2] << 16);

    /// <summary>A time-of-day in 10^-scale seconds, 3 to 5 bytes little-endian, as microseconds - the 7th digit truncated.</summary>
    private static long TimeMicros(ReadOnlySpan<byte> data, byte scale)
    {
        long units = 0;
        for (int i = data.Length - 1; i >= 0; i--) units = (units << 8) | data[i];
        return scale <= 6 ? units * PowersOf10[6 - scale] : units / PowersOf10[scale - 6];
    }

    /// <summary>datetime: days since 1900-01-01, then 1/300ths of a second since midnight, rounded to the nearest microsecond.</summary>
    private static long DateTimeMicros(ReadOnlySpan<byte> data)
    {
        long days = BinaryPrimitives.ReadInt32LittleEndian(data) - DaysFrom1900To1970;
        long ticks = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        return days * MicrosPerDay + (ticks * 10_000 + 1) / 3;
    }

    /// <summary>smalldatetime: days since 1900-01-01, then minutes since midnight.</summary>
    private static long SmallDateTimeMicros(ReadOnlySpan<byte> data)
    {
        long days = BinaryPrimitives.ReadUInt16LittleEndian(data) - DaysFrom1900To1970;
        long minutes = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        return days * MicrosPerDay + minutes * 60_000_000;
    }

    /// <summary>A sql_variant's value as text, as SQL Server would CAST it to nvarchar where that is plain.</summary>
    internal static string VariantText(object? value) => value switch
    {
        null => "",
        bool b => b ? "1" : "0",
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.'),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeSpan t => t.ToString(@"hh\:mm\:ss\.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.'),
        DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF zzz", CultureInfo.InvariantCulture).Replace(". ", " "),
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        RawValue raw => "0x" + Convert.ToHexString(raw.Bytes),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
