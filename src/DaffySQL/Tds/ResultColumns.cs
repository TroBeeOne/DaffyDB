namespace DaffySQL.Tds;

/// <summary>
/// One column of a result set, as a server describes it in COLMETADATA: what a value of it looks
/// like on the wire, and enough to declare a column like it.
/// </summary>
internal sealed record ResultColumn(string Name, uint UserType, ushort Flags, TypeInfo Type, string? UdtSchema = null)
{
    /// <summary>The user type SQL Server gives timestamp (rowversion) columns.</summary>
    public const uint TimestampUserType = 80;

    public bool Nullable => (Flags & 0x0001) != 0;
}

/// <summary>
/// Reads a result set's COLMETADATA token. Column types are described as in RPC parameters (see
/// <see cref="TypeInfoReader"/>) with two differences: a CLR type (UDT) carries its size and
/// assembly name as well as its name, and text, ntext and image are followed by the name of the
/// table they came from.
/// </summary>
internal static class ResultColumns
{
    /// <summary>
    /// The columns of a COLMETADATA token that starts at <paramref name="start"/>, with the offset
    /// just past it; or false when <paramref name="payload"/> ends before the token does.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> payload, int start, out List<ResultColumn> columns, out int end)
    {
        try
        {
            columns = Read(payload, start, out end);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            columns = [];
            end = start;
            return false;
        }
    }

    public static List<ResultColumn> Read(ReadOnlySpan<byte> payload, int start, out int end)
    {
        var cursor = new TdsCursor(payload);
        cursor.Seek(start);
        if (cursor.ReadByte() != TdsConstants.Token.ColMetadata)
            throw new InvalidDataException($"expected COLMETADATA at offset {start}");

        var columns = new List<ResultColumn>();
        int count = cursor.ReadUInt16();
        if (count == 0xFFFF) count = 0;   // "no metadata": a statement that returns no columns

        for (int i = 0; i < count; i++)
        {
            uint userType = cursor.ReadUInt32();
            ushort flags = cursor.ReadUInt16();

            TypeInfo type;
            string? udtSchema = null;
            if (payload[cursor.Position] == 0xF0)
            {
                cursor.ReadByte();
                int maxLength = cursor.ReadUInt16();
                cursor.ReadBVarChar();                  // database
                udtSchema = cursor.ReadBVarChar();
                string name = cursor.ReadBVarChar();
                cursor.ReadUsVarChar();                 // assembly-qualified name
                type = new TypeInfo(0xF0, maxLength) { UdtName = name };
            }
            else
            {
                type = TypeInfoReader.ReadTypeInfo(ref cursor);
            }

            if (type.Type is 0x23 or 0x63 or 0x22)
            {
                int parts = cursor.ReadByte();
                for (int p = 0; p < parts; p++) cursor.ReadUsVarChar();
            }

            columns.Add(new ResultColumn(cursor.ReadBVarChar(), userType, flags, type, udtSchema));
        }

        end = cursor.Position;
        return columns;
    }
}
