using System.Buffers.Binary;
using System.Text;

namespace DaffySQL.Tds;

/// <summary>The requests DaffySQL sends once logged in, framed and ready to write.</summary>
internal static class Requests
{
    /// <summary>An ATTENTION: stop what you are doing. A header and nothing else.</summary>
    public static readonly byte[] Attention =
        [TdsConstants.PacketType.Attention, TdsConstants.PacketStatus.EndOfMessage, 0, TdsConstants.HeaderLength, 0, 0, 1, 0];

    /// <summary>
    /// A SQL_BATCH: ALL_HEADERS (the transaction it runs in, one outstanding request), then the text
    /// in UTF-16LE, framed at the session's packet size. <paramref name="transaction"/> is the
    /// descriptor the server handed out when the session's transaction began, 0 for none: inside a
    /// transaction SQL Server wants it on every request. <paramref name="reset"/> sets
    /// RESETCONNECTION on the first packet, which makes the server put the session back as it was
    /// at login before running the batch - how a pooled session forgets its last user's temp
    /// tables and SET options, as SqlClient's pool does. Lifted from DaffyDuo's Backend.BuildBatch.
    /// </summary>
    public static byte[] Batch(string sql, bool reset, ulong transaction, int packetSize)
    {
        byte[] text = Encoding.Unicode.GetBytes(sql);
        var payload = new byte[22 + text.Length];
        var body = payload.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(body, 22);                        // ALL_HEADERS total
        BinaryPrimitives.WriteUInt32LittleEndian(body[4..], 18);                   // this header's length
        BinaryPrimitives.WriteUInt16LittleEndian(body[8..], TdsConstants.HeaderType.TransactionDescriptor);
        BinaryPrimitives.WriteUInt64LittleEndian(body[10..], transaction);
        BinaryPrimitives.WriteUInt32LittleEndian(body[18..], 1);                   // outstanding requests
        text.CopyTo(body[22..]);

        int perPacket = packetSize - TdsConstants.HeaderLength;
        int count = Math.Max(1, (payload.Length + perPacket - 1) / perPacket);
        var packets = new byte[payload.Length + count * TdsConstants.HeaderLength];
        for (int i = 0, at = 0; i < count; i++)
        {
            int chunk = Math.Min(perPacket, payload.Length - i * perPacket);
            var packet = packets.AsSpan(at, TdsConstants.HeaderLength + chunk);
            packet[0] = TdsConstants.PacketType.SqlBatch;
            packet[1] = (byte)((i == count - 1 ? TdsConstants.PacketStatus.EndOfMessage : 0)
                               | (reset && i == 0 ? TdsConstants.PacketStatus.ResetConnection : 0));
            BinaryPrimitives.WriteUInt16BigEndian(packet[2..], (ushort)packet.Length);
            packet[6] = (byte)(i + 1);
            payload.AsSpan(i * perPacket, chunk).CopyTo(packet[TdsConstants.HeaderLength..]);
            at += packet.Length;
        }
        return packets;
    }
}
