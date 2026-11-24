using System.Text;

namespace TdsProxy.Tds;

internal sealed record Login7Info(
    uint PacketSize, string HostName, string UserName, string Password, string AppName,
    string ServerName, string Database, bool UsesIntegratedSecurity);

/// <summary>
/// LOGIN7 is a fixed 94-byte header followed by an offset/length table pointing into a
/// variable-length blob. Nothing here is validated; the fields are read so that the log shows who
/// connected, the negotiated packet size is honoured, and a handler can decide about the caller.
/// </summary>
internal static class Login7
{
    private const int OffsetTableStart = 36;

    public static Login7Info Parse(byte[] payload)
    {
        var cursor = new TdsCursor(payload);
        cursor.Skip(8);                       // Length, TDSVersion
        uint packetSize = cursor.ReadUInt32();

        string hostName = ReadField(payload, 0);
        string userName = ReadField(payload, 1);
        string password = ReadPassword(payload, 2);
        string appName = ReadField(payload, 3);
        string serverName = ReadField(payload, 4);
        string database = ReadField(payload, 8);

        ushort sspiLength = ReadUInt16(payload, 80);

        // Empty when the client named no database, which is not the same as naming master: the
        // server then chooses, and DaffyDB chooses the one with the data in it.
        return new Login7Info(
            packetSize, hostName, userName, password, appName, serverName, database, sspiLength > 0);
    }

    /// <summary>
    /// The password field is scrambled rather than encrypted: each byte of the UTF-16LE text is
    /// nibble-swapped and then XORed with 0xA5. Undoing it is the same two steps in reverse, and it
    /// is worth being blunt about what that means — on an unencrypted connection the password is
    /// plainly readable by anyone on the wire. This recovers it so a handler can act on it.
    /// </summary>
    private static string ReadPassword(byte[] payload, int index)
    {
        int entry = OffsetTableStart + index * 4;
        if (entry + 4 > payload.Length) return "";

        ushort offset = ReadUInt16(payload, entry);
        ushort chars = ReadUInt16(payload, entry + 2);
        if (chars == 0 || offset + chars * 2 > payload.Length) return "";

        var bytes = new byte[chars * 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            byte scrambled = (byte)(payload[offset + i] ^ 0xA5);
            bytes[i] = (byte)((scrambled >> 4) | (scrambled << 4));
        }

        return Encoding.Unicode.GetString(bytes);
    }

    /// <summary>Field index follows the LOGIN7 offset table order: host, user, password, app, server, ...</summary>
    private static string ReadField(byte[] payload, int index)
    {
        int entry = OffsetTableStart + index * 4;
        if (entry + 4 > payload.Length) return "";

        ushort offset = ReadUInt16(payload, entry);
        ushort chars = ReadUInt16(payload, entry + 2);
        if (chars == 0 || offset + chars * 2 > payload.Length) return "";

        return Encoding.Unicode.GetString(payload, offset, chars * 2);
    }

    private static ushort ReadUInt16(byte[] payload, int offset) =>
        (ushort)(payload[offset] | (payload[offset + 1] << 8));
}
