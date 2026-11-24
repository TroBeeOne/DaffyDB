using System.Text;

namespace TdsProxy.Tds;

/// <summary>
/// Reads an RPC parameter's TYPE_INFO followed by its TYPE_VARBYTE value.
///
/// Parameterized commands arrive as the "N" (nullable) variants — INTNTYPE rather than INT4TYPE and
/// so on — because a parameter can always be null. Anything not handled here throws
/// <see cref="TdsUnsupportedException"/>, which the session turns into an ERROR token so the client
/// sees a real message instead of hanging.
/// </summary>
internal static class TypeInfoReader
{
    public static (byte TypeByte, object? Value) Read(ref TdsCursor cursor)
    {
        byte type = cursor.ReadByte();

        return type switch
        {
            // Fixed-length types carry their length implicitly and are never null.
            0x30 => (type, (int)cursor.ReadByte()),
            0x32 => (type, cursor.ReadByte() != 0),
            0x34 => (type, (int)(short)cursor.ReadUInt16()),
            0x38 => (type, cursor.ReadInt32()),
            0x7F => (type, (long)cursor.ReadUInt64()),
            0x3B => (type, BitConverter.ToSingle(cursor.ReadBytes(4))),
            0x3E => (type, BitConverter.ToDouble(cursor.ReadBytes(8))),
            0x3A => (type, ReadSmallDateTime(cursor.ReadBytes(4))),
            0x3D => (type, ReadDateTime(cursor.ReadBytes(8))),
            0x7A => (type, ReadMoney4(cursor.ReadBytes(4))),
            0x3C => (type, ReadMoney(cursor.ReadBytes(8))),

            0x26 => (type, ReadByteLengthValue(ref cursor, type, cursor.ReadByte())),  // INTN
            0x68 => (type, ReadByteLengthValue(ref cursor, type, cursor.ReadByte())),  // BITN
            0x6D => (type, ReadByteLengthValue(ref cursor, type, cursor.ReadByte())),  // FLTN
            0x6E => (type, ReadByteLengthValue(ref cursor, type, cursor.ReadByte())),  // MONEYN
            0x6F => (type, ReadByteLengthValue(ref cursor, type, cursor.ReadByte())),  // DATETIMN
            0x24 => (type, ReadByteLengthValue(ref cursor, type, cursor.ReadByte())),  // UNIQUEIDENTIFIER

            0x28 => (type, ReadTemporal(ref cursor, type, scale: 0)),                  // DATEN, no scale
            0x29 or 0x2A or 0x2B => (type, ReadTemporal(ref cursor, type, cursor.ReadByte())),

            0x6A or 0x6C => (type, ReadDecimal(ref cursor)),                           // DECIMALN / NUMERICN

            0xE7 or 0xEF => (type, ReadString(ref cursor, unicode: true)),             // NVARCHAR / NCHAR
            0xA7 or 0xAF => (type, ReadString(ref cursor, unicode: false)),            // VARCHAR / CHAR
            0xA5 or 0xAD => (type, ReadBinary(ref cursor)),                            // VARBINARY / BINARY

            // The legacy large types. SqlClient still sends an sp_executesql statement longer than
            // 4000 characters as NTEXT, and SMO's query for a table's columns is that long - so
            // expanding Columns in Object Explorer depends on this.
            0x63 => (type, ReadLongText(ref cursor, unicode: true)),                   // NTEXT
            0x23 => (type, ReadLongText(ref cursor, unicode: false)),                  // TEXT
            0x22 => (type, ReadLongBinary(ref cursor)),                                // IMAGE

            _ => throw new TdsUnsupportedException($"parameter type 0x{type:X2} is not supported by this proxy"),
        };
    }

    private static object? ReadByteLengthValue(ref TdsCursor cursor, byte type, byte maxLength)
    {
        byte length = cursor.ReadByte();
        if (length == 0) return null;   // a zero-length value is NULL for the N-types

        var data = cursor.ReadBytes(length);

        return type switch
        {
            0x26 => length switch
            {
                1 => (int)data[0],
                2 => (int)BitConverter.ToInt16(data),
                4 => BitConverter.ToInt32(data),
                8 => BitConverter.ToInt64(data),
                _ => throw new TdsUnsupportedException($"INTN length {length}"),
            },
            0x68 => data[0] != 0,
            0x6D => length == 4 ? BitConverter.ToSingle(data) : BitConverter.ToDouble(data),
            0x6E => length == 4 ? ReadMoney4(data) : ReadMoney(data),
            0x6F => length == 4 ? ReadSmallDateTime(data) : ReadDateTime(data),
            0x24 => new Guid(data),
            _ => throw new TdsUnsupportedException($"type 0x{type:X2} (declared max {maxLength})"),
        };
    }

    private static object? ReadTemporal(ref TdsCursor cursor, byte type, byte scale)
    {
        byte length = cursor.ReadByte();
        if (length == 0) return null;

        var data = cursor.ReadBytes(length);

        return type switch
        {
            0x28 => DateTime.MinValue.AddDays(ReadDays(data)),
            0x29 => DateTime.MinValue.AddTicks(ReadTimeTicks(data, scale)),
            0x2A => DateTime.MinValue
                        .AddDays(ReadDays(data[^3..]))
                        .AddTicks(ReadTimeTicks(data[..^3], scale)),
            0x2B => new DateTimeOffset(
                        DateTime.MinValue
                            .AddDays(ReadDays(data[^5..^2]))
                            .AddTicks(ReadTimeTicks(data[..^5], scale)),
                        TimeSpan.FromMinutes(BitConverter.ToInt16(data[^2..]))),
            _ => throw new TdsUnsupportedException($"temporal type 0x{type:X2}"),
        };
    }

    private static int ReadDays(ReadOnlySpan<byte> data) => data[0] | (data[1] << 8) | (data[2] << 16);

    private static long ReadTimeTicks(ReadOnlySpan<byte> data, byte scale)
    {
        long units = 0;
        for (int i = data.Length - 1; i >= 0; i--) units = (units << 8) | data[i];

        // Stored in 10^-scale second units; DateTime ticks are 100ns (10^-7).
        for (int i = scale; i < 7; i++) units *= 10;
        return units;
    }

    private static object? ReadDecimal(ref TdsCursor cursor)
    {
        cursor.ReadByte();                   // declared max length
        cursor.ReadByte();                   // precision
        byte scale = cursor.ReadByte();

        byte length = cursor.ReadByte();
        if (length == 0) return null;

        bool positive = cursor.ReadByte() != 0;
        var magnitude = cursor.ReadBytes(length - 1);
        if (magnitude.Length > 12)
            throw new TdsUnsupportedException("decimal wider than 96 bits");

        Span<int> parts = stackalloc int[3];
        for (int i = 0; i < magnitude.Length; i++)
            parts[i / 4] |= magnitude[i] << (8 * (i % 4));

        return new decimal(parts[0], parts[1], parts[2], !positive, scale);
    }

    private static object? ReadString(ref TdsCursor cursor, bool unicode)
    {
        ushort maxLength = cursor.ReadUInt16();
        cursor.ReadBytes(5);                 // collation

        byte[]? data;
        if (maxLength == 0xFFFF)
        {
            data = ReadPlp(ref cursor);      // nvarchar(max) / varchar(max)
        }
        else
        {
            ushort length = cursor.ReadUInt16();
            if (length == 0xFFFF) return null;   // 0xFFFF is NULL; 0x0000 is the empty string
            data = cursor.ReadBytes(length).ToArray();
        }

        return data is null ? null : unicode ? Encoding.Unicode.GetString(data) : Encoding.Latin1.GetString(data);
    }

    private static object? ReadBinary(ref TdsCursor cursor)
    {
        ushort maxLength = cursor.ReadUInt16();
        if (maxLength == 0xFFFF) return ReadPlp(ref cursor);   // varbinary(max)

        ushort length = cursor.ReadUInt16();
        if (length == 0xFFFF) return null;
        return cursor.ReadBytes(length).ToArray();
    }

    /// <summary>
    /// A (max) parameter's value: an eight-byte total length - all ones for NULL, all ones but the
    /// last bit when the sender did not know it in advance - then chunks, each led by its four-byte
    /// length, until a chunk of length zero. The total is only a hint; the chunks are the value.
    /// </summary>
    private static byte[]? ReadPlp(ref TdsCursor cursor)
    {
        ulong total = cursor.ReadUInt64();
        if (total == ulong.MaxValue) return null;

        using var value = new MemoryStream(total < int.MaxValue ? (int)total : 0);
        while (true)
        {
            uint chunk = cursor.ReadUInt32();
            if (chunk == 0) break;
            value.Write(cursor.ReadBytes((int)chunk));
        }

        return value.ToArray();
    }

    /// <summary>
    /// TEXT and NTEXT: a four-byte declared size and a collation, then the value as a four-byte
    /// length - all ones for NULL - and the bytes. In a ROW these carry a text pointer; as a
    /// parameter they do not.
    /// </summary>
    private static object? ReadLongText(ref TdsCursor cursor, bool unicode)
    {
        cursor.ReadUInt32();                 // declared max length
        cursor.ReadBytes(5);                 // collation

        uint length = cursor.ReadUInt32();
        if (length == uint.MaxValue) return null;

        var data = cursor.ReadBytes((int)length);
        return unicode ? Encoding.Unicode.GetString(data) : Encoding.Latin1.GetString(data);
    }

    private static object? ReadLongBinary(ref TdsCursor cursor)
    {
        cursor.ReadUInt32();                 // declared max length

        uint length = cursor.ReadUInt32();
        if (length == uint.MaxValue) return null;
        return cursor.ReadBytes((int)length).ToArray();
    }

    private static DateTime ReadDateTime(ReadOnlySpan<byte> data)
    {
        int days = BitConverter.ToInt32(data);
        uint threeHundredths = BitConverter.ToUInt32(data[4..]);
        return new DateTime(1900, 1, 1).AddDays(days).AddMilliseconds(threeHundredths * 10.0 / 3.0);
    }

    private static DateTime ReadSmallDateTime(ReadOnlySpan<byte> data)
    {
        ushort days = BitConverter.ToUInt16(data);
        ushort minutes = BitConverter.ToUInt16(data[2..]);
        return new DateTime(1900, 1, 1).AddDays(days).AddMinutes(minutes);
    }

    private static decimal ReadMoney(ReadOnlySpan<byte> data)
    {
        int high = BitConverter.ToInt32(data);
        uint low = BitConverter.ToUInt32(data[4..]);
        return ((long)high << 32 | low) / 10000m;
    }

    private static decimal ReadMoney4(ReadOnlySpan<byte> data) => BitConverter.ToInt32(data) / 10000m;
}

/// <summary>Something the client sent that phase 1 deliberately does not implement.</summary>
internal sealed class TdsUnsupportedException(string message) : Exception(message);
