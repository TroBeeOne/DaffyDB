using System.Buffers.Binary;
using System.Text;

namespace DaffySQL.Tds;

/// <summary>
/// A LOGIN7 message, as DaffySQL builds and sends it.
///
/// Lifted from DaffyDuo, which reads the client's LOGIN7 and writes it back out with four fields
/// swapped; DaffySQL has no client in front of it, so only the writing half is here, and the
/// fields are filled in from the connection string (see <see cref="Client.TdsConnector"/>).
///
/// Layout: a fixed 94-byte header holding scalar fields and an offset/length table, then the
/// variable-length data the table points into. Offsets are from the start of the message; string
/// lengths are in characters (UTF-16LE), SSPI's in bytes. When the fExtension bit is set, the
/// "extension" slot points at a 4-byte offset, which in turn points at the FeatureExt block.
/// </summary>
internal sealed class Login7
{
    public const int FixedLength = 94;

    // OptionFlags3
    private const byte ExtensionFlag = 0x10;

    public uint TdsVersion { get; set; }
    public uint PacketSize { get; set; }
    public uint ClientProgVer { get; set; }
    public uint ClientPid { get; set; }
    public uint ConnectionId { get; set; }
    public byte OptionFlags1 { get; set; }
    public byte OptionFlags2 { get; set; }
    public byte TypeFlags { get; set; }
    public byte OptionFlags3 { get; set; }
    public int ClientTimeZone { get; set; }
    public uint ClientLcid { get; set; }

    public string HostName { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string AppName { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string ClientInterfaceName { get; set; } = "";
    public string Language { get; set; } = "";
    public string Database { get; set; } = "";
    public byte[] ClientId { get; set; } = new byte[6];
    public byte[] Sspi { get; set; } = [];
    public string AttachDbFile { get; set; } = "";
    public string ChangePassword { get; set; } = "";

    /// <summary>The FeatureExt block from its first feature through the 0xFF terminator. Null for none.</summary>
    public byte[]? FeatureExt { get; set; }

    /// <summary>Feature ids in the FeatureExt block, in the order sent.</summary>
    public IReadOnlyList<byte> FeatureIds
    {
        get
        {
            var ids = new List<byte>();
            if (FeatureExt is null) return ids;

            int position = 0;
            while (position < FeatureExt.Length)
            {
                byte id = FeatureExt[position++];
                if (id == TdsConstants.FeatureId.Terminator || position + 4 > FeatureExt.Length) break;
                ids.Add(id);
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(FeatureExt.AsSpan(position));
                position += 4 + (int)length;
            }

            return ids;
        }
    }

    /// <summary>Adds a feature to the FeatureExt block, ahead of its terminator, creating the block if there is none.</summary>
    public void AddFeature(byte id, ReadOnlySpan<byte> data)
    {
        var block = new TdsWriter();
        if (FeatureExt is { Length: > 0 } existing)
        {
            int position = 0;
            while (position < existing.Length && existing[position] != TdsConstants.FeatureId.Terminator && position + 5 <= existing.Length)
                position += 5 + (int)BinaryPrimitives.ReadUInt32LittleEndian(existing.AsSpan(position + 1));
            block.WriteBytes(existing.AsSpan(0, Math.Min(position, existing.Length)));
        }

        block.WriteByte(id);
        block.WriteUInt32((uint)data.Length);
        block.WriteBytes(data);
        block.WriteByte(TdsConstants.FeatureId.Terminator);
        FeatureExt = block.ToArray();
    }

    public string TdsVersionName => TdsVersion switch
    {
        0x74000004 => "7.4",
        0x08000000 => "8.0",
        _ => $"0x{TdsVersion:X8}",
    };

    public Login7 Clone()
    {
        var copy = (Login7)MemberwiseClone();
        copy.ClientId = (byte[])ClientId.Clone();
        copy.Sspi = (byte[])Sspi.Clone();
        copy.FeatureExt = (byte[]?)FeatureExt?.Clone();
        return copy;
    }

    /// <summary>Writes the message in the same order Microsoft.Data.SqlClient does.</summary>
    public byte[] Build()
    {
        var data = new TdsWriter();
        var table = new TdsWriter();

        int NextOffset() => FixedLength + data.Length;

        void Text(string value)
        {
            table.WriteUInt16((ushort)NextOffset());
            table.WriteUInt16((ushort)value.Length);
            data.WriteBytes(Encoding.Unicode.GetBytes(value));
        }

        void Scrambled(string value)
        {
            table.WriteUInt16((ushort)NextOffset());
            table.WriteUInt16((ushort)value.Length);
            data.WriteBytes(Scramble(value));
        }

        bool hasFeatures = FeatureExt is { Length: > 0 };

        Text(HostName);
        Text(UserName);
        Scrambled(Password);
        Text(AppName);
        Text(ServerName);

        // The extension slot holds a 4-byte offset to the FeatureExt block, which goes last; its
        // value is patched once everything in between has been laid out.
        int featureOffsetPosition = -1;
        table.WriteUInt16((ushort)NextOffset());
        if (hasFeatures)
        {
            table.WriteUInt16(4);
            featureOffsetPosition = data.Length;
            data.WriteUInt32(0);
        }
        else
        {
            table.WriteUInt16(0);
        }

        Text(ClientInterfaceName);
        Text(Language);
        Text(Database);
        table.WriteBytes(ClientId.Length == 6 ? ClientId : new byte[6]);

        table.WriteUInt16((ushort)NextOffset());
        table.WriteUInt16((ushort)Math.Min(Sspi.Length, 0xFFFF));
        data.WriteBytes(Sspi);

        Text(AttachDbFile);
        Scrambled(ChangePassword);
        table.WriteUInt32(Sspi.Length >= 0xFFFF ? (uint)Sspi.Length : 0);   // cbSSPILong

        if (hasFeatures)
        {
            data.PatchUInt32(featureOffsetPosition, (uint)NextOffset());
            data.WriteBytes(FeatureExt!);
        }

        byte optionFlags3 = hasFeatures
            ? (byte)(OptionFlags3 | ExtensionFlag)
            : (byte)(OptionFlags3 & ~ExtensionFlag);

        var message = new TdsWriter();
        message.WriteUInt32((uint)(FixedLength + data.Length));
        message.WriteUInt32(TdsVersion);
        message.WriteUInt32(PacketSize);
        message.WriteUInt32(ClientProgVer);
        message.WriteUInt32(ClientPid);
        message.WriteUInt32(ConnectionId);
        message.WriteByte(OptionFlags1);
        message.WriteByte(OptionFlags2);
        message.WriteByte(TypeFlags);
        message.WriteByte(optionFlags3);
        message.WriteInt32(ClientTimeZone);
        message.WriteUInt32(ClientLcid);
        message.WriteBytes(table.AsMemory().Span);
        message.WriteBytes(data.AsMemory().Span);

        return message.ToArray();
    }

    /// <summary>
    /// The password is scrambled rather than encrypted: each byte of the UTF-16LE text has its
    /// nibbles swapped and is then XORed with 0xA5. On an unencrypted connection that makes it
    /// readable by anyone on the wire, which is why Encrypt=Mandatory is the default.
    /// </summary>
    public static byte[] Scramble(string value)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(value);
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(((bytes[i] << 4) | (bytes[i] >> 4)) ^ 0xA5);
        return bytes;
    }

    /// <summary>Safe to log: says whether a password was sent, never what it was.</summary>
    public override string ToString() =>
        $"user='{UserName}' host='{HostName}' app='{AppName}' server='{ServerName}' db='{Database}' " +
        $"tds={TdsVersionName} packetSize={PacketSize} " +
        $"features=[{string.Join(",", FeatureIds.Select(TdsConstants.FeatureId.Name))}]";
}
