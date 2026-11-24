using System.Buffers.Binary;
using DaffySQL.Client;
using DaffySQL.Tds;

namespace DaffySQL.Tests;

/// <summary>
/// What DaffySQL reads and writes that DaffyDuo did not: the pull reader's way across packet
/// boundaries, how it recognises a cancel's acknowledgement, and the table names daffy_scan sends.
/// </summary>
public class WireTests
{
    private static readonly byte[] Acknowledgement = Done(TdsConstants.DoneStatus.Attention);

    [Fact]
    public void AnAcknowledgementIsFoundWhenItsPacketIsShorterThanItIs()
    {
        // The 13-byte DONE split 3 + 10 across the last two packets.
        byte[] message = [.. new byte[40], .. Acknowledgement];
        var reader = Reader(Packets(message, 43));

        reader.Begin();
        Assert.True(reader.SkipToEnd());
        Assert.Equal(TdsConstants.DoneStatus.Attention, reader.FinalDoneStatus);
    }

    [Fact]
    public void AnEndThatIsNotAnAcknowledgementIsNotTakenForOne()
    {
        var reader = Reader(Packets([.. new byte[20], .. Done(TdsConstants.DoneStatus.Count)], 7, 25));
        reader.Begin();
        Assert.False(reader.SkipToEnd());
        Assert.Equal(TdsConstants.DoneStatus.Count, reader.FinalDoneStatus);
    }

    [Fact]
    public void ValuesAreReadAcrossPacketBoundaries()
    {
        byte[] message = [.. Enumerable.Range(0, 100).Select(i => (byte)i)];
        var reader = Reader(Packets(message, 3, 5, 50));

        reader.Begin();
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(1)), reader.ReadUInt32());   // 1..4, across 3|5
        Assert.Equal(message[5..60], reader.Read(55).ToArray());                                           // across 50
        reader.Skip(39);
        Assert.Equal(99, reader.ReadByte());
        Assert.True(reader.AtEnd);
        Assert.Throws<InvalidDataException>(() => reader.ReadByte());
    }

    [Fact]
    public void AStructureOfUnknownLengthIsGatheredAcrossPackets()
    {
        // "Parse" needs 30 bytes, and the first packet has 10: it is asked again with more.
        byte[] message = [.. Enumerable.Range(0, 64).Select(i => (byte)i)];
        var reader = Reader(Packets(message, 10, 20));
        int attempts = 0;

        reader.Begin();
        int value = reader.ReadWhole((ReadOnlySpan<byte> data, out int result, out int used) =>
        {
            attempts++;
            used = 30;
            result = data.Length >= 30 ? data[29] : -1;
            return data.Length >= 30;
        });

        Assert.Equal(29, value);
        Assert.Equal(3, attempts);
        Assert.Equal(30, reader.ReadByte());   // the next byte after what was used
    }

    [Theory]
    [InlineData("Customers", "[Customers]")]
    [InlineData("dbo.Customers", "[dbo].[Customers]")]
    [InlineData("Sales.dbo.Customers", "[Sales].[dbo].[Customers]")]
    [InlineData("Sales..Customers", "[Sales]..[Customers]")]
    [InlineData("[My Table]", "[My Table]")]
    [InlineData("dbo.[Odd ]]Name]", "[dbo].[Odd ]]Name]")]
    [InlineData("\"quoted\".x", "[quoted].[x]")]
    public void TableNamesAreParsedAndQuoted(string name, string quoted) => Assert.Equal(quoted, SqlNames.Table(name));

    [Theory]
    [InlineData("dbo.Customers; DROP TABLE x")]
    [InlineData("dbo.Customers'")]
    [InlineData("a.b.c.d")]
    [InlineData("dbo.")]
    [InlineData(".x")]
    [InlineData("x y")]
    [InlineData("[unterminated")]
    [InlineData("#temp")]
    [InlineData("")]
    public void WhatIsNotATableNameIsRefused(string name) => Assert.Throws<ArgumentException>(() => SqlNames.Table(name));

    // ---- helpers ----------------------------------------------------------------------------

    private static byte[] Done(ushort status)
    {
        var done = new byte[13];
        done[0] = TdsConstants.Token.Done;
        BinaryPrimitives.WriteUInt16LittleEndian(done.AsSpan(1), status);
        return done;
    }

    /// <summary>A message as TABULAR_RESULT packets, cut at the given payload offsets.</summary>
    private static byte[] Packets(byte[] message, params int[] cuts)
    {
        var output = new MemoryStream();
        int[] edges = [0, .. cuts, message.Length];
        for (int i = 0; i + 1 < edges.Length; i++)
        {
            int length = edges[i + 1] - edges[i];
            var header = new byte[TdsConstants.HeaderLength];
            header[0] = TdsConstants.PacketType.TabularResult;
            header[1] = i + 2 == edges.Length ? TdsConstants.PacketStatus.EndOfMessage : TdsConstants.PacketStatus.Normal;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)(length + TdsConstants.HeaderLength));
            output.Write(header);
            output.Write(message, edges[i], length);
        }
        return output.ToArray();
    }

    private static ResponseReader Reader(byte[] wire) => new(new MemoryStream(wire), TdsConstants.DefaultPacketSize);
}
