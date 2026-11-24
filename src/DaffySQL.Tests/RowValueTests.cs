using System.Buffers.Binary;
using System.Text;
using DaffySQL.Tds;

namespace DaffySQL.Tests;

/// <summary>
/// A row read as .NET values, for DaffySql.Run: where each value starts and ends in a row's own form,
/// and that a value is found whole however packets cut it. What each type's bytes mean is
/// TypeInfoReader's, and verify-sqlserver.ps1 compares those with a real server's answers.
/// </summary>
public class RowValueTests
{
    private static ResultColumn Column(TypeInfo type) => new("c", 0, 0, type);

    private static object?[] Read(byte[] row, IReadOnlyList<ResultColumn> columns, bool nullBitmap = false, params int[] cuts)
    {
        var reader = new ResponseReader(new MemoryStream(Packets(row, cuts)), TdsConstants.DefaultPacketSize);
        reader.Begin();
        var values = RowValues.Read(reader, columns, nullBitmap);
        Assert.True(reader.AtEnd, "the row should use every byte it was given");
        return values;
    }

    [Fact]
    public void FixedWidthValuesHaveNoLength()
    {
        byte[] row = [200, 1, .. Le(7), .. Le(9L)];
        var values = Read(row, [Column(new TypeInfo(0x30, 1)), Column(new TypeInfo(0x32, 1)), Column(new TypeInfo(0x38, 4)), Column(new TypeInfo(0x7F, 8))]);
        Assert.Equal<object?>([(byte)200, true, 7, 9L], values);
    }

    [Fact]
    public void AByteLengthOfZeroIsNull()
    {
        byte[] row = [0, 4, .. Le(5)];
        var values = Read(row, [Column(new TypeInfo(0x26, 4)), Column(new TypeInfo(0x26, 4))]);
        Assert.Null(values[0]);
        Assert.Equal(5, values[1]);
    }

    [Fact]
    public void ATwoByteLengthOfAllOnesIsNullAndZeroIsEmpty()
    {
        var text = new TypeInfo(0xE7, 100, Collation: new byte[5]);
        byte[] row = [0xFF, 0xFF, 0, 0, 4, 0, .. Encoding.Unicode.GetBytes("hi")];
        Assert.Equal<object?>([null, "", "hi"], Read(row, [Column(text), Column(text), Column(text)]));
    }

    [Fact]
    public void BinaryIsBytes()
    {
        byte[] row = [2, 0, 1, 0xFF];
        Assert.Equal(new byte[] { 1, 0xFF }, Read(row, [Column(new TypeInfo(0xA5, 16))])[0]);
    }

    [Fact]
    public void ABitmapSaysWhichAreNull()
    {
        // Columns 0 and 2 NULL; 1 and 3 have values, with no NULL form of their own to read past.
        byte[] row = [0b0101, .. Le(1), .. Le(3)];
        var values = Read(row, [Column(new TypeInfo(0x38, 4)), Column(new TypeInfo(0x38, 4)), Column(new TypeInfo(0x38, 4)), Column(new TypeInfo(0x38, 4))], nullBitmap: true);
        Assert.Equal<object?>([null, 1, null, 3], values);
    }

    [Fact]
    public void PlpGathersItsChunksAcrossPackets()
    {
        byte[] first = Encoding.Unicode.GetBytes("long ");
        byte[] second = Encoding.Unicode.GetBytes("value");
        byte[] row = [.. Le((ulong)(first.Length + second.Length)), .. Le((uint)first.Length), .. first, .. Le((uint)second.Length), .. second, .. Le(0u)];

        var max = new TypeInfo(0xE7, 0xFFFF, Collation: new byte[5]);
        Assert.Equal("long value", Read(row, [Column(max)], cuts: [3, 10, 14, 20])[0]);
    }

    [Fact]
    public void PlpOfAllOnesIsNull()
    {
        Assert.Null(Read([.. Le(ulong.MaxValue)], [Column(new TypeInfo(0xA5, 0xFFFF))])[0]);
    }

    [Fact]
    public void XmlIsUtf16AndAClrTypeIsItsBytes()
    {
        byte[] xml = Encoding.Unicode.GetBytes("<a/>");
        byte[] row = [.. Le((ulong)xml.Length), .. Le((uint)xml.Length), .. xml, .. Le(0u),
                      .. Le(2UL), .. Le(2u), 7, 8, .. Le(0u)];
        var values = Read(row, [Column(new TypeInfo(0xF1, 0xFFFF)), Column(new TypeInfo(0xF0, 0xFFFF))]);
        Assert.Equal("<a/>", values[0]);
        Assert.Equal(new byte[] { 7, 8 }, values[1]);
    }

    [Fact]
    public void TextHasAPointerAndATimestampBeforeItsLength()
    {
        byte[] body = Encoding.Unicode.GetBytes("old");
        byte[] row = [16, .. new byte[16], .. new byte[8], .. Le(body.Length), .. body,   // ntext
                      0,                                                                   // NULL
                      16, .. new byte[16], .. new byte[8], .. Le(2), 1, 2];                // image
        var values = Read(row, [Column(new TypeInfo(0x63, 1000, Collation: new byte[5])), Column(new TypeInfo(0x63, 1000, Collation: new byte[5])), Column(new TypeInfo(0x22, 1000))]);
        Assert.Equal("old", values[0]);
        Assert.Null(values[1]);
        Assert.Equal(new byte[] { 1, 2 }, values[2]);
    }

    [Fact]
    public void AVariantIsItsValueAsText()
    {
        // Base type int, no properties, then the value.
        byte[] body = [0x38, 0, .. Le(42)];
        byte[] row = [.. Le(body.Length), .. body, .. Le(0)];
        Assert.Equal<object?>(["42", null], Read(row, [Column(new TypeInfo(0x62, 8016)), Column(new TypeInfo(0x62, 8016))]));
    }

    [Fact]
    public void AValueIsReadWholeWhereverPacketsCutIt()
    {
        var text = new TypeInfo(0xE7, 100, Collation: new byte[5]);
        byte[] hello = Encoding.Unicode.GetBytes("hello");
        byte[] row = [.. Le(5), (byte)hello.Length, 0, .. hello];
        for (int cut = 1; cut < row.Length; cut++)
            Assert.Equal<object?>([5, "hello"], Read(row, [Column(new TypeInfo(0x38, 4)), Column(text)], cuts: [cut]));
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static byte[] Le(int value) { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, value); return b; }
    private static byte[] Le(uint value) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, value); return b; }
    private static byte[] Le(long value) { var b = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, value); return b; }
    private static byte[] Le(ulong value) { var b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, value); return b; }

    /// <summary>A message as TABULAR_RESULT packets, cut at the given payload offsets.</summary>
    private static byte[] Packets(byte[] message, int[] cuts)
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
}
