namespace DaffySQL.Tds;

/// <summary>A whole message, and the SPID the server stamped on its packets.</summary>
internal sealed record ReceivedMessage(TdsMessage Message, ushort Spid);

/// <summary>
/// Reads whole TDS messages during the login - the PRELOGIN answer, the login response - where
/// each one is small and has to be understood as a whole before deciding what to do next.
///
/// Once the login is over the session reads responses itself, a packet at a time
/// (<see cref="ResponseReader"/>), so <see cref="Drain"/> hands back anything read but not used.
/// In practice that is nothing - a server does not speak until it is asked something - and the
/// session refuses to start if it is not.
/// </summary>
internal sealed class TdsMessageReader(Stream stream)
{
    private readonly PacketFramer _framer = new();
    private readonly MessageAssembler _assembler = new();
    private readonly Queue<TdsPacket> _packets = new();
    private readonly byte[] _buffer = new byte[16 * 1024];

    /// <summary>Replaced when a TDS 7.4 connection switches to TLS part-way through.</summary>
    public Stream Stream { get; set; } = stream;

    /// <summary>Null means the connection closed before a whole message arrived.</summary>
    public async Task<ReceivedMessage?> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            while (_packets.TryDequeue(out var packet))
                if (_assembler.Push(packet) is { } message)
                    return new ReceivedMessage(message, packet.Spid);

            int read = await Stream.ReadAsync(_buffer, cancellationToken);
            if (read == 0) return null;

            foreach (var packet in _framer.Push(_buffer.AsSpan(0, read))) _packets.Enqueue(packet);

            if (_framer.Desynced)
                throw new InvalidDataException("the server sent bytes that are not a TDS packet");
        }
    }

    /// <summary>The number of bytes read but not yet returned. Zero once a login is complete.</summary>
    public int Pending => _packets.Sum(p => p.Payload.Length + TdsConstants.HeaderLength) + _framer.Pending;
}
