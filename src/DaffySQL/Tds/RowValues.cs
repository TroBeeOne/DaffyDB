using System.Text;
using DaffySQL.DuckDb;

namespace DaffySQL.Tds;

/// <summary>
/// Reads a row of a result as .NET values, for a host that wants an answer itself: DuckDB's rows go
/// through <see cref="RowWriter"/> instead, straight into its vectors. This finds where each value
/// starts and ends, which is all a row's form adds, across as many packets as a value takes, and
/// leaves <see cref="TypeInfoReader"/> to make a value of the bytes.
/// </summary>
internal static class RowValues
{
    /// <summary>One row, its token already read. NBCROW starts with a bitmap of the columns that are NULL.</summary>
    public static object?[] Read(ResponseReader reader, IReadOnlyList<ResultColumn> columns, bool nullBitmap)
    {
        byte[]? bitmap = null;
        if (nullBitmap)
        {
            bitmap = new byte[(columns.Count + 7) / 8];
            reader.ReadInto(bitmap);
        }

        var row = new object?[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            if (bitmap is not null && (bitmap[i >> 3] & (1 << (i & 7))) != 0) continue;
            row[i] = Value(reader, columns[i].Type);
        }
        return row;
    }

    private static object? Value(ResponseReader reader, TypeInfo type)
    {
        if (type.IsPlp)
        {
            byte[]? data = Plp(reader);
            return data is null ? null : type.Type switch
            {
                0xF1 => Encoding.Unicode.GetString(data),   // xml is UTF-16
                0xF0 => data,                               // a CLR type: SQL Server's own serialization
                _ => TypeInfoReader.Character(type, data),
            };
        }

        switch (type.Type)
        {
            // Fixed width, no length: what a server sends for a NOT NULL column.
            case 0x30 or 0x32 or 0x34 or 0x38 or 0x3A or 0x3B or 0x7A or 0x3C or 0x3D or 0x3E or 0x7F:
                return TypeInfoReader.ReadFixed(type.Type, reader.Read(type.MaxLength));

            // A 2-byte length, 0xFFFF meaning NULL.
            case 0xA7 or 0xAF or 0xE7 or 0xEF or 0xA5 or 0xAD:
            {
                ushort length = reader.ReadUInt16();
                return length == 0xFFFF ? null : TypeInfoReader.Character(type, reader.Read(length));
            }

            // text, ntext and image: a text pointer, a timestamp, then a 4-byte length.
            case 0x23 or 0x63 or 0x22:
            {
                byte pointer = reader.ReadByte();
                if (pointer == 0) return null;
                reader.Skip(pointer + 8);
                int length = reader.ReadInt32();
                byte form = type.Type switch { 0x23 => 0xA7, 0x63 => 0xE7, _ => 0xA5 };
                return TypeInfoReader.Character(type with { Type = form }, reader.Read(length));
            }

            // A 4-byte length, 0 meaning NULL, then a value that carries its own type.
            case 0x62:
            {
                int length = reader.ReadInt32();
                return length == 0 ? null : RowWriter.VariantText(TypeInfoReader.VariantValue(reader.Read(length)));
            }

            // Everything else: a byte length, 0 for NULL.
            default:
            {
                int size = reader.ReadByte();
                return size == 0 ? null : TypeInfoReader.ByteLengthValue(type, reader.Read(size));
            }
        }
    }

    /// <summary>PLP: an 8-byte total, all ones for NULL, then chunks with 4-byte lengths until a zero one.</summary>
    private static byte[]? Plp(ResponseReader reader)
    {
        if (reader.ReadUInt64() == ulong.MaxValue) return null;

        using var gathered = new MemoryStream();
        while (true)
        {
            uint chunk = reader.ReadUInt32();
            if (chunk == 0) return gathered.ToArray();

            int start = (int)gathered.Length;
            gathered.SetLength(start + chunk);
            reader.ReadInto(gathered.GetBuffer().AsSpan(start, (int)chunk));
        }
    }
}
