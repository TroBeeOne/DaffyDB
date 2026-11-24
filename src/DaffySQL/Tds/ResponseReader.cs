using System.Buffers.Binary;

namespace DaffySQL.Tds;

/// <summary>
/// Reads one server response - a message of TABULAR_RESULT packets - as a continuous run of bytes,
/// a packet at a time, synchronously.
///
/// DuckDB calls a scan on one of its own threads and waits for the chunk, so the read happens right
/// there, and SQL Server is read exactly as fast as DuckDB takes rows: there is no pump, no queue
/// and no buffer beyond the packet in hand. That is the difference from DaffyDuo, whose pump pushes
/// a server's bytes on to a client as they arrive.
///
/// Values come back as spans into the packet when they lie inside it, and are copied into a
/// scratch buffer only when they straddle two; either way a span is good until the next read.
/// </summary>
internal sealed class ResponseReader(Stream stream, int packetSize)
{
    private const int DoneLength = 13;   // token, status(2), curcmd(2), rowcount(8)

    private byte[] _packet = new byte[Math.Max(packetSize, TdsConstants.DefaultPacketSize)];
    private int _position, _end;
    private bool _last = true;
    private byte[] _scratch = new byte[256];

    // The last bytes of the response so far, to find the DONE it ends with even when its final
    // packet is shorter than one.
    private readonly byte[] _tail = new byte[DoneLength];
    private int _tailLength;

    /// <summary>The whole response has been read: the last packet arrived, and nothing of it is left.</summary>
    public bool AtEnd => _last && _position == _end;

    /// <summary>Some of the answer has arrived: the server has heard the request.</summary>
    public bool Received { get; private set; }

    /// <summary>
    /// The status of the DONE token the response ends with, once its last packet is in: whether it
    /// acknowledges an attention, whether a transaction is still open. Null before then.
    /// </summary>
    public ushort? FinalDoneStatus { get; private set; }

    /// <summary>The response ended with an attention's acknowledgement: a DONE with the ATTN bit.</summary>
    public bool EndedWithAcknowledgement =>
        FinalDoneStatus is { } status && (status & TdsConstants.DoneStatus.Attention) != 0;

    /// <summary>Starts on the answer to a request just sent.</summary>
    public void Begin()
    {
        _position = _end = 0;
        _last = false;
        _tailLength = 0;
        Received = false;
        FinalDoneStatus = null;
    }

    public byte PeekByte()
    {
        Ensure();
        return _packet[_position];
    }

    public byte ReadByte()
    {
        Ensure();
        return _packet[_position++];
    }

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Read(2));
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Read(4));
    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Read(4));
    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Read(8));

    /// <summary>The next <paramref name="count"/> bytes, good until the next read.</summary>
    public ReadOnlySpan<byte> Read(int count)
    {
        if (count == 0) return [];
        Ensure();
        if (_end - _position >= count)
        {
            var inPacket = _packet.AsSpan(_position, count);
            _position += count;
            return inPacket;
        }

        if (_scratch.Length < count) _scratch = new byte[Math.Max(count, _scratch.Length * 2)];
        ReadInto(_scratch.AsSpan(0, count));
        return _scratch.AsSpan(0, count);
    }

    /// <summary>Fills <paramref name="destination"/>, across as many packets as it takes.</summary>
    public void ReadInto(Span<byte> destination)
    {
        while (!destination.IsEmpty)
        {
            Ensure();
            int take = Math.Min(destination.Length, _end - _position);
            _packet.AsSpan(_position, take).CopyTo(destination);
            _position += take;
            destination = destination[take..];
        }
    }

    public void Skip(long count)
    {
        while (count > 0)
        {
            Ensure();
            int take = (int)Math.Min(count, _end - _position);
            _position += take;
            count -= take;
        }
    }

    /// <summary>
    /// Reads a structure whose length is only known once it has been read - COLMETADATA. The parser
    /// is handed every byte from here that has arrived; when it runs out (returns false) the next
    /// packet is added and it starts again. On success the bytes it used are consumed.
    /// </summary>
    public T ReadWhole<T>(WholeParser<T> parse)
    {
        Ensure();
        var gathered = new MemoryStream();
        gathered.Write(_packet, _position, _end - _position);

        while (true)
        {
            if (parse(gathered.GetBuffer().AsSpan(0, (int)gathered.Length), out T result, out int used))
            {
                // Everything gathered before the packet now in hand was needed - the parser asked for
                // more - so whatever it left over lies in that packet.
                _position = _end - ((int)gathered.Length - used);
                return result;
            }

            if (_last) throw new InvalidDataException("the response ended part-way through a token");
            NextPacket();
            gathered.Write(_packet, _position, _end - _position);
        }
    }

    public delegate bool WholeParser<T>(ReadOnlySpan<byte> data, out T result, out int used);

    /// <summary>
    /// Reads and discards the rest of the response, unparsed. Returns whether it ended with an
    /// attention acknowledgement, which is how the end of a cancelled response is recognised.
    /// </summary>
    public bool SkipToEnd()
    {
        _position = _end;
        while (!_last)
        {
            NextPacket();
            _position = _end;
        }
        return EndedWithAcknowledgement;
    }

    private void Ensure()
    {
        while (_position == _end)
        {
            if (_last) throw new InvalidDataException("the response ended part-way through a token");
            NextPacket();
        }
    }

    private void NextPacket()
    {
        Fill(_packet.AsSpan(0, TdsConstants.HeaderLength));
        byte type = _packet[0];
        byte status = _packet[1];
        int length = BinaryPrimitives.ReadUInt16BigEndian(_packet.AsSpan(2));   // big-endian, as always

        if (type != TdsConstants.PacketType.TabularResult)
            throw new InvalidDataException($"expected a response packet, got {TdsConstants.PacketTypeName(type)} (0x{type:X2})");
        if (length < TdsConstants.HeaderLength)
            throw new InvalidDataException($"a response packet claims {length} bytes, shorter than its header");
        if (length > _packet.Length) Array.Resize(ref _packet, length);

        Fill(_packet.AsSpan(TdsConstants.HeaderLength, length - TdsConstants.HeaderLength));
        _position = TdsConstants.HeaderLength;
        _end = length;
        _last = (status & TdsConstants.PacketStatus.EndOfMessage) != 0;
        Received = true;

        Remember(_packet.AsSpan(TdsConstants.HeaderLength, length - TdsConstants.HeaderLength));
        if (_last)
            FinalDoneStatus = _tailLength == DoneLength && _tail[0] == TdsConstants.Token.Done
                ? BinaryPrimitives.ReadUInt16LittleEndian(_tail.AsSpan(1))
                : null;
    }

    /// <summary>Keeps the last 13 bytes of the response read so far: where its final DONE will be.</summary>
    private void Remember(ReadOnlySpan<byte> payload)
    {
        if (payload.Length >= DoneLength)
        {
            payload[^DoneLength..].CopyTo(_tail);
            _tailLength = DoneLength;
            return;
        }

        int keep = Math.Min(_tailLength, DoneLength - payload.Length);
        _tail.AsSpan(_tailLength - keep, keep).CopyTo(_tail);
        payload.CopyTo(_tail.AsSpan(keep));
        _tailLength = keep + payload.Length;
    }

    private void Fill(Span<byte> target)
    {
        try
        {
            stream.ReadExactly(target);
        }
        catch (EndOfStreamException ex)
        {
            throw new IOException("the server closed the connection", ex);
        }
    }
}
