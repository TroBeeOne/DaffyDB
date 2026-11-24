using System.Text;
using TdsProxy.Query;

namespace TdsProxy.Tds;

/// <summary>Turns a SQL_BATCH or RPC message into the <see cref="SqlRequest"/> the seam consumes.</summary>
internal static class RequestParser
{
    public static SqlRequest ParseBatch(byte[] payload)
    {
        int offset = AllHeaders.BodyOffset(payload);
        string sql = Encoding.Unicode.GetString(payload.AsSpan(offset));
        return new SqlRequest(RequestKind.Batch, sql, []);
    }

    public static SqlRequest ParseRpc(byte[] payload)
    {
        int offset = AllHeaders.BodyOffset(payload);
        var cursor = new TdsCursor(payload.AsSpan(offset));

        ushort nameLengthOrSwitch = cursor.ReadUInt16();
        ushort procId = 0;
        string procName;

        if (nameLengthOrSwitch == TdsConstants.RpcProcIdSwitch)
        {
            procId = cursor.ReadUInt16();
            procName = TdsConstants.ProcId.Name(procId);
        }
        else
        {
            procName = Encoding.Unicode.GetString(cursor.ReadBytes(nameLengthOrSwitch * 2));
        }

        cursor.ReadUInt16();   // OptionFlags

        var raw = new List<(string Name, byte TypeByte, object? Value, bool IsOutput)>();
        while (cursor.Remaining > 0)
        {
            byte nameLength = cursor.ReadByte();
            if (nameLength == 0xFF) break;   // start of another RPC in the same batch

            string name = nameLength == 0 ? "" : Encoding.Unicode.GetString(cursor.ReadBytes(nameLength * 2));
            byte status = cursor.ReadByte(); // StatusFlags (0x01 = by-ref, an OUTPUT parameter; 0x02 = default)

            var (typeByte, value) = TypeInfoReader.Read(ref cursor);
            raw.Add((name, typeByte, value, (status & 0x01) != 0));
        }

        // sp_executesql carries the statement and its parameter declarations as the first two
        // unnamed arguments; everything after them is an actual parameter value. SqlClient names it
        // by ProcID, but a client may equally spell it out, qualified or not.
        bool executeSql = procId == TdsConstants.ProcId.ExecuteSql ||
            procName.Split('.').Last().Trim('[', ']').Equals("sp_executesql", StringComparison.OrdinalIgnoreCase);

        if (executeSql && raw.Count >= 1)
        {
            string sql = raw[0].Value as string ?? "";
            string declarations = raw.Count >= 2 ? raw[1].Value as string ?? "" : "";
            var declaredTypes = ParseDeclarations(declarations);

            var parameters = raw.Skip(2)
                .Select(p => new SqlParameterValue(
                    p.Name,
                    declaredTypes.GetValueOrDefault(p.Name, DescribeTdsType(p.TypeByte)),
                    p.TypeByte,
                    p.Value) { IsOutput = p.IsOutput })
                .ToList();

            return new SqlRequest(RequestKind.Rpc, sql, parameters);
        }

        // Anything else is a procedure called by name - [sys].sp_tables, sp_columns_100 and the
        // rest - or by a ProcID other than sp_executesql. Its arguments keep their names where the
        // client gave them (@ODBCVer = 3) and are otherwise positional.
        var positional = raw
            .Select(p => new SqlParameterValue(p.Name, DescribeTdsType(p.TypeByte), p.TypeByte, p.Value)
            {
                IsOutput = p.IsOutput,
            })
            .ToList();

        return new SqlRequest(RequestKind.Rpc, procName, positional) { Procedure = procName };
    }

    private static Dictionary<string, string> ParseDeclarations(string declarations)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, type) in Declarations(declarations)) map[name] = type;
        return map;
    }

    /// <summary>
    /// Splits an sp_executesql @params string such as "@year int,@total decimal(18,2)" into its
    /// names and types, in order. Commas inside parentheses belong to the type, not the separator.
    /// </summary>
    public static List<(string Name, string Type)> Declarations(string declarations)
    {
        var list = new List<(string Name, string Type)>();
        int depth = 0, start = 0;

        for (int i = 0; i <= declarations.Length; i++)
        {
            if (i < declarations.Length)
            {
                char c = declarations[i];
                if (c == '(') { depth++; continue; }
                if (c == ')') { depth--; continue; }
                if (c != ',' || depth != 0) continue;
            }

            string entry = declarations[start..i].Trim();
            start = i + 1;
            if (entry.Length == 0) continue;

            int space = entry.IndexOf(' ');
            if (space < 0) list.Add((entry, ""));
            else list.Add((entry[..space], entry[(space + 1)..].Trim()));
        }

        return list;
    }

    private static string DescribeTdsType(byte typeByte) => typeByte switch
    {
        0x26 or 0x30 or 0x34 or 0x38 or 0x7F => "int",
        0x68 or 0x32 => "bit",
        0xE7 or 0xEF => "nvarchar",
        0xA7 or 0xAF => "varchar",
        0xA5 or 0xAD => "varbinary",
        0x63 => "ntext",
        0x23 => "text",
        0x22 => "image",
        0x6A or 0x6C => "decimal",
        0x6D or 0x3B or 0x3E => "float",
        0x6E or 0x3C or 0x7A => "money",
        0x28 => "date",
        0x29 => "time",
        0x2A => "datetime2",
        0x2B => "datetimeoffset",
        0x6F or 0x3D or 0x3A => "datetime",
        0x24 => "uniqueidentifier",
        _ => $"type0x{typeByte:X2}",
    };
}
