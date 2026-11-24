using System.Globalization;
using System.Numerics;
using System.Text;
using TdsProxy.Query;

namespace TdsProxy.Tds;

/// <summary>
/// Encodes the response token stream. Every layout here was verified byte-for-byte against a
/// transcript of a real SQL Server 2025 (LocalDB) talking to Microsoft.Data.SqlClient 6.1.1,
/// captured with the --relay mode.
/// </summary>
internal static class ResponseWriter
{
    /// <summary>TDS 7.4. Note the byte order is the reverse of the same value in LOGIN7.</summary>
    private static ReadOnlySpan<byte> TdsVersion74 => [0x74, 0x00, 0x00, 0x04];

    /// <summary>SQL_Latin1_General_CP1_CI_AS, as sent by a real server.</summary>
    public static ReadOnlySpan<byte> DefaultCollation => [0x09, 0x04, 0xD0, 0x00, 0x34];

    private const ushort SelectCurCmd = 0x00C1;

    public static void WriteLoginAck(TdsWriter writer, string serverName, byte major, byte minor, ushort build)
    {
        writer.WriteByte(TdsConstants.Token.LoginAck);
        int length = writer.ReserveUInt16();

        writer.WriteByte(0x01);                 // Interface: SQL_TSQL
        writer.WriteBytes(TdsVersion74);
        writer.WriteBVarChar(serverName);
        writer.WriteByte(major);
        writer.WriteByte(minor);
        writer.WriteByte((byte)(build >> 8));   // BuildNumHi
        writer.WriteByte((byte)(build & 0xFF)); // BuildNumLow

        writer.PatchLengthFrom(length);
    }

    public static void WriteEnvChange(TdsWriter writer, byte type, string newValue, string oldValue)
    {
        writer.WriteByte(TdsConstants.Token.EnvChange);
        int length = writer.ReserveUInt16();

        writer.WriteByte(type);
        writer.WriteBVarChar(newValue);
        writer.WriteBVarChar(oldValue);

        writer.PatchLengthFrom(length);
    }

    /// <summary>
    /// ENVCHANGE whose values are byte strings rather than text — used for the SQL collation (type
    /// 0x07), which carries a 5-byte collation rather than a character string.
    /// </summary>
    public static void WriteEnvChangeBytes(TdsWriter writer, byte type, ReadOnlySpan<byte> newValue)
    {
        writer.WriteByte(TdsConstants.Token.EnvChange);
        int length = writer.ReserveUInt16();

        writer.WriteByte(type);
        writer.WriteByte((byte)newValue.Length);
        writer.WriteBytes(newValue);
        writer.WriteByte(0);    // old value: empty

        writer.PatchLengthFrom(length);
    }

    public static void WriteInfo(TdsWriter writer, int number, byte state, string message, string serverName) =>
        WriteMessageToken(writer, TdsConstants.Token.Info, number, state, severity: 0, message, serverName);

    /// <summary>
    /// Severity (class) of 11 or higher is what makes SqlClient raise a SqlException rather than
    /// fire an InfoMessage. This is the difference between a readable failure and a silent hang.
    /// </summary>
    public static void WriteError(TdsWriter writer, int number, byte severity, string message, string serverName,
                                  byte state = 1, string procedure = "", int line = 1) =>
        WriteMessageToken(writer, TdsConstants.Token.Error, number, state, severity, message, serverName, procedure, line);

    private static void WriteMessageToken(
        TdsWriter writer, byte token, int number, byte state, byte severity, string message, string serverName,
        string procedure = "", int line = 1)
    {
        writer.WriteByte(token);
        int length = writer.ReserveUInt16();

        writer.WriteInt32(number);
        writer.WriteByte(state);
        writer.WriteByte(severity);
        writer.WriteUsVarChar(message);
        writer.WriteBVarChar(serverName);
        writer.WriteBVarChar(procedure);
        writer.WriteInt32(line);

        writer.PatchLengthFrom(length);
    }

    /// <summary>DONE row count is 8 bytes on TDS 7.2+; a 4-byte write here hangs the client.</summary>
    public static void WriteDone(TdsWriter writer, byte token, ushort status, ushort currentCommand, ulong rowCount)
    {
        writer.WriteByte(token);
        writer.WriteUInt16(status);
        writer.WriteUInt16(currentCommand);
        writer.WriteUInt64(rowCount);
    }

    public static void WriteReturnStatus(TdsWriter writer, int value)
    {
        writer.WriteByte(TdsConstants.Token.ReturnStatus);
        writer.WriteInt32(value);
    }

    /// <summary>
    /// RETURNVALUE: one OUTPUT parameter's value, after RETURNSTATUS and before DONEPROC. The
    /// layout is LocalDB's answer to sp_prepexec and sp_cursoropen: the parameter's ordinal, its
    /// name (empty when the client sent none), status 0x01 for an output parameter, a user type and
    /// flags of zero, then an int's TYPE_INFO and value.
    /// </summary>
    public static void WriteReturnValue(TdsWriter writer, ReturnValue value)
    {
        writer.WriteByte(TdsConstants.Token.ReturnValue);
        writer.WriteUInt16((ushort)value.Ordinal);
        writer.WriteBVarChar(value.Name);
        writer.WriteByte(0x01);
        writer.WriteUInt32(0);
        writer.WriteUInt16(0);

        writer.WriteByte(0x26);   // INTNTYPE
        writer.WriteByte(4);
        if (value.Value is not { } number) { writer.WriteByte(0); return; }
        writer.WriteByte(4);
        writer.WriteInt32(number);
    }

    /// <summary>
    /// COLMETADATA.
    ///
    /// A real server also emits a CekTable (column-encryption key table) right after the count, but
    /// only because it acknowledged the column-encryption feature in FEATUREEXTACK. We never send
    /// FEATUREEXTACK, so the client does not read one — sending it anyway shifts every column by two
    /// bytes and SqlClient reports the flags byte as an invalid type.
    /// </summary>
    public static void WriteColMetadata(TdsWriter writer, IReadOnlyList<ColumnDef> columns, IReadOnlyList<int> sizes)
    {
        writer.WriteByte(TdsConstants.Token.ColMetadata);
        writer.WriteUInt16((ushort)columns.Count);

        for (int i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            writer.WriteUInt32(0);   // UserType

            // fComputed (0x20) marks these as expression columns, which is what they are.
            //
            // fNullable (0x01) is always set, and must be: every type we emit is an "N" variant
            // (INTNTYPE, BITNTYPE, ...) whose row values carry a length prefix. A real server only
            // pairs those with a nullable column, and SqlClient relies on that pairing — declare an
            // N-type as non-nullable and it reads the length prefix as the first byte of the value,
            // then desyncs and reports "Internal connection fatal error".
            writer.WriteUInt16(0x0021);

            WriteTypeInfo(writer, column, sizes[i]);
            writer.WriteBVarChar(ColumnName(column.Name));
        }
    }

    /// <summary>
    /// The name sent for a column. DuckDB names a column it was given no alias for after the whole
    /// expression, and a translated expression can run to hundreds of characters - more than the
    /// one-byte length the protocol gives a column name, which corrupts the stream. A name longer
    /// than an identifier can be (128 characters) is sent empty instead, which is what SQL Server
    /// itself sends for a column with no alias.
    /// </summary>
    private static string ColumnName(string name) => name.Length > 128 ? "" : name;

    private static void WriteTypeInfo(TdsWriter writer, ColumnDef column, int maxBytes)
    {
        switch (column.Type)
        {
            case SqlColumnType.Int:
                writer.WriteByte(0x26);          // INTNTYPE
                writer.WriteByte(4);
                break;

            case SqlColumnType.BigInt:
                writer.WriteByte(0x26);          // INTNTYPE, 8 bytes
                writer.WriteByte(8);
                break;

            case SqlColumnType.Double:
                writer.WriteByte(0x6D);          // FLTNTYPE
                writer.WriteByte(8);
                break;

            case SqlColumnType.Decimal:
                writer.WriteByte(0x6A);          // DECIMALNTYPE
                writer.WriteByte(DecimalValueLength);
                writer.WriteByte(column.Precision);
                writer.WriteByte(column.Scale);
                break;

            case SqlColumnType.NVarChar:
                writer.WriteByte(0xE7);          // NVARCHARTYPE
                writer.WriteUInt16(DeclaredLength(maxBytes));
                writer.WriteBytes(DefaultCollation);
                break;

            case SqlColumnType.Bit:
                writer.WriteByte(0x68);          // BITNTYPE
                writer.WriteByte(1);
                break;

            case SqlColumnType.Date:
                writer.WriteByte(0x28);          // DATENTYPE carries no scale or length
                break;

            case SqlColumnType.DateTime2:
                writer.WriteByte(0x2A);          // DATETIME2NTYPE
                writer.WriteByte(7);             // scale
                break;

            case SqlColumnType.TinyInt:
                writer.WriteByte(0x26);          // INTNTYPE, 1 byte, unsigned as in SQL Server
                writer.WriteByte(1);
                break;

            case SqlColumnType.SmallInt:
                writer.WriteByte(0x26);          // INTNTYPE, 2 bytes
                writer.WriteByte(2);
                break;

            case SqlColumnType.Real:
                writer.WriteByte(0x6D);          // FLTNTYPE, 4 bytes
                writer.WriteByte(4);
                break;

            case SqlColumnType.Time:
                writer.WriteByte(0x29);          // TIMENTYPE
                writer.WriteByte(7);             // scale: 100ns units, five bytes on the wire
                break;

            case SqlColumnType.VarBinary:
                writer.WriteByte(0xA5);          // BIGVARBINTYPE
                writer.WriteUInt16(DeclaredLength(maxBytes));
                break;

            case SqlColumnType.UniqueIdentifier:
                writer.WriteByte(0x24);          // GUIDTYPE
                writer.WriteByte(16);
                break;

            case SqlColumnType.VarChar:
                writer.WriteByte(0xA7);          // BIGVARCHRTYPE: code page 1252, from the collation
                writer.WriteUInt16(DeclaredLength(maxBytes));
                writer.WriteBytes(DefaultCollation);
                break;

            case SqlColumnType.DateTime:
                writer.WriteByte(0x6F);          // DATETIMNTYPE
                writer.WriteByte(8);
                break;

            case SqlColumnType.Binary:
                writer.WriteByte(0xAD);          // BIGBINARYTYPE: fixed length, declared
                writer.WriteUInt16((ushort)maxBytes);
                break;

            default:
                throw new NotSupportedException($"no TYPE_INFO encoder for {column.Type}");
        }
    }

    /// <summary>
    /// <paramref name="sizes"/> are the byte lengths COLMETADATA declared, and every variable-length
    /// value is cut to fit its own. A value longer than its declaration does not fail cleanly: the
    /// client reads the declared length, and everything after that is read as the next token.
    /// </summary>
    public static void WriteRow(TdsWriter writer, IReadOnlyList<ColumnDef> columns, object?[] values,
                                IReadOnlyList<int> sizes)
    {
        writer.WriteByte(TdsConstants.Token.Row);

        for (int i = 0; i < columns.Count; i++)
        {
            WriteValue(writer, columns[i], values[i], sizes[i]);
        }
    }

    private static void WriteValue(TdsWriter writer, ColumnDef column, object? value, int maxBytes)
    {
        switch (column.Type)
        {
            case SqlColumnType.Int:
                if (value is null) { writer.WriteByte(0); break; }
                writer.WriteByte(4);
                writer.WriteInt32(Convert.ToInt32(value));
                break;

            case SqlColumnType.BigInt:
                if (value is null) { writer.WriteByte(0); break; }
                writer.WriteByte(8);
                writer.WriteUInt64((ulong)Convert.ToInt64(value));
                break;

            case SqlColumnType.Double:
                if (value is null) { writer.WriteByte(0); break; }
                writer.WriteByte(8);
                writer.WriteUInt64(BitConverter.DoubleToUInt64Bits(Convert.ToDouble(value)));
                break;

            case SqlColumnType.Decimal:
                if (value is null) { writer.WriteByte(0); break; }
                WriteDecimal(writer, Convert.ToDecimal(value), column.Scale);
                break;

            case SqlColumnType.Date:
                if (value is null) { writer.WriteByte(0); break; }
                WriteDate(writer, Convert.ToDateTime(value));
                break;

            case SqlColumnType.NVarChar when maxBytes == MaxSize:
                WritePlp(writer, value is null ? null : Encoding.Unicode.GetBytes(Text(value)));
                break;

            case SqlColumnType.NVarChar:
                if (value is null) { writer.WriteUInt16(0xFFFF); break; }   // 0xFFFF is NULL, not empty
                WriteShortBytes(writer, Encoding.Unicode.GetBytes(Fit(Text(value), maxBytes / 2)));
                break;

            case SqlColumnType.VarChar when maxBytes == MaxSize:
                WritePlp(writer, value is null ? null : Windows1252.GetBytes(Text(value)));
                break;

            case SqlColumnType.VarChar:
                if (value is null) { writer.WriteUInt16(0xFFFF); break; }
                WriteShortBytes(writer, Windows1252.GetBytes(Fit(Text(value), maxBytes)));
                break;

            case SqlColumnType.VarBinary when maxBytes == MaxSize:
                WritePlp(writer, (byte[]?)value);
                break;

            case SqlColumnType.VarBinary:
                if (value is null) { writer.WriteUInt16(0xFFFF); break; }
                var binary = (byte[])value;
                WriteShortBytes(writer, binary.AsSpan(0, Math.Min(binary.Length, maxBytes)));
                break;

            case SqlColumnType.TinyInt:
                if (value is null) { writer.WriteByte(0); break; }
                writer.WriteByte(1);
                writer.WriteByte(Convert.ToByte(value));
                break;

            case SqlColumnType.SmallInt:
                if (value is null) { writer.WriteByte(0); break; }
                writer.WriteByte(2);
                writer.WriteUInt16(unchecked((ushort)Convert.ToInt16(value)));
                break;

            case SqlColumnType.Real:
                if (value is null) { writer.WriteByte(0); break; }
                writer.WriteByte(4);
                writer.WriteUInt32(BitConverter.SingleToUInt32Bits(Convert.ToSingle(value)));
                break;

            case SqlColumnType.Time:
                if (value is null) { writer.WriteByte(0); break; }
                writer.WriteByte(5);
                ulong units = (ulong)((TimeSpan)value).Ticks;   // a tick is 100ns, which is scale 7
                for (int i = 0; i < 5; i++) writer.WriteByte((byte)(units >> (8 * i)));
                break;

            case SqlColumnType.UniqueIdentifier:
                if (value is null) { writer.WriteByte(0); break; }
                writer.WriteByte(16);
                // Guid's own byte order - the first three fields little-endian - is SQL Server's.
                writer.WriteBytes(((Guid)value).ToByteArray());
                break;

            case SqlColumnType.DateTime:
                if (value is null) { writer.WriteByte(0); break; }
                WriteDateTime(writer, Convert.ToDateTime(value));
                break;

            case SqlColumnType.Binary:
                if (value is null) { writer.WriteUInt16(0xFFFF); break; }
                // binary(n) is always n bytes: a short value is padded with zeros, as SQL Server pads it.
                var padded = new byte[maxBytes];
                var source = (byte[])value;
                source.AsSpan(0, Math.Min(source.Length, maxBytes)).CopyTo(padded);
                WriteShortBytes(writer, padded);
                break;

            case SqlColumnType.Bit:
                if (value is null) { writer.WriteByte(0); break; }
                writer.WriteByte(1);
                writer.WriteByte(Convert.ToBoolean(value) ? (byte)1 : (byte)0);
                break;

            case SqlColumnType.DateTime2:
                if (value is null) { writer.WriteByte(0); break; }
                WriteDateTime2(writer, Convert.ToDateTime(value));
                break;

            default:
                throw new NotSupportedException($"no value encoder for {column.Type}");
        }
    }

    /// <summary>DATETIME2(7): 5 bytes of 100ns units since midnight, then 3 bytes of days since 0001-01-01.</summary>
    private static void WriteDateTime2(TdsWriter writer, DateTime value)
    {
        writer.WriteByte(8);

        ulong time = (ulong)value.TimeOfDay.Ticks;
        for (int i = 0; i < 5; i++) writer.WriteByte((byte)(time >> (8 * i)));

        WriteDays(writer, value);
    }

    /// <summary>DATE: 3 bytes of days since 0001-01-01.</summary>
    private static void WriteDate(TdsWriter writer, DateTime value)
    {
        writer.WriteByte(3);
        WriteDays(writer, value);
    }

    private static void WriteDays(TdsWriter writer, DateTime value)
    {
        int days = (int)(value.Date - DateTime.MinValue).TotalDays;
        for (int i = 0; i < 3; i++) writer.WriteByte((byte)(days >> (8 * i)));
    }

    /// <summary>Sign byte plus a 16-byte little-endian magnitude, covering precision up to 38.</summary>
    private const byte DecimalValueLength = 17;

    private static void WriteDecimal(TdsWriter writer, decimal value, byte scale)
    {
        int[] bits = decimal.GetBits(value);
        byte valueScale = (byte)((bits[3] >> 16) & 0xFF);
        bool negative = (bits[3] & unchecked((int)0x80000000)) != 0;

        var magnitude = (new BigInteger((uint)bits[2]) << 64)
                      | (new BigInteger((uint)bits[1]) << 32)
                      | new BigInteger((uint)bits[0]);

        // Restate the magnitude at the scale declared in COLMETADATA.
        if (scale >= valueScale)
        {
            magnitude *= BigInteger.Pow(10, scale - valueScale);
        }
        else
        {
            var divisor = BigInteger.Pow(10, valueScale - scale);
            magnitude = BigInteger.Divide(magnitude + divisor / 2, divisor);   // round half away from zero
        }

        writer.WriteByte(DecimalValueLength);
        writer.WriteByte(negative ? (byte)0 : (byte)1);   // 1 is positive

        var bytes = magnitude.ToByteArray(isUnsigned: true, isBigEndian: false);
        for (int i = 0; i < DecimalValueLength - 1; i++)
            writer.WriteByte(i < bytes.Length ? bytes[i] : (byte)0);
    }

    /// <summary>
    /// DATETIME: days since 1900-01-01, then the time of day in three-hundredths of a second. The
    /// rounding can reach midnight, which belongs to the next day rather than to 300 * 86400.
    /// </summary>
    private static void WriteDateTime(TdsWriter writer, DateTime value)
    {
        int days = (int)(value.Date - new DateTime(1900, 1, 1)).TotalDays;
        long ticks = (long)Math.Round(value.TimeOfDay.Ticks * 300.0 / TimeSpan.TicksPerSecond);

        if (ticks >= 300L * 86400)
        {
            days++;
            ticks = 0;
        }

        writer.WriteByte(8);
        writer.WriteInt32(days);
        writer.WriteUInt32((uint)ticks);
    }

    /// <summary>The length-prefixed form shared by every non-max varchar, nvarchar and varbinary.</summary>
    private static void WriteShortBytes(TdsWriter writer, ReadOnlySpan<byte> bytes)
    {
        writer.WriteUInt16((ushort)bytes.Length);
        writer.WriteBytes(bytes);
    }

    /// <summary>
    /// PLP, the encoding of a (max) value: its total length in eight bytes, then the value in
    /// chunks - here just one - each led by its own four-byte length, then a zero-length chunk to
    /// end it. NULL is the all-ones total with no chunks at all.
    /// </summary>
    private static void WritePlp(TdsWriter writer, byte[]? bytes)
    {
        if (bytes is null)
        {
            writer.WriteUInt64(ulong.MaxValue);
            return;
        }

        writer.WriteUInt64((ulong)bytes.Length);
        if (bytes.Length > 0)
        {
            writer.WriteUInt32((uint)bytes.Length);
            writer.WriteBytes(bytes);
        }
        writer.WriteUInt32(0);
    }

    /// <summary>The largest value a non-max column can carry; longer values need a (max) column.</summary>
    public const int MaxTextBytes = 8000;

    /// <summary>The size <see cref="MeasureSizes"/> gives a column that must be sent as (max).</summary>
    public const int MaxSize = -1;

    /// <summary>A (max) column declares its length as 0xFFFF, which is how the client knows to read PLP.</summary>
    private static ushort DeclaredLength(int maxBytes) => maxBytes == MaxSize ? (ushort)0xFFFF : (ushort)maxBytes;

    /// <summary>
    /// The code page of the collation every varchar column declares. .NET only ships UTF-8 and
    /// Latin-1 until the code-page provider is registered, and Latin-1 is not quite 1252.
    /// </summary>
    private static readonly Encoding Windows1252 = CreateWindows1252();

    private static Encoding CreateWindows1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }

    private static string Text(object value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    private static string Fit(string value, int maxChars) => value.Length <= maxChars ? value : value[..maxChars];

    /// <summary>
    /// The declared byte length of every character and binary column. A size a column declares for
    /// itself is used as given, which is how a system procedure answers with sysname or varchar(254)
    /// exactly as SQL Server does.
    ///
    /// Any other text or varbinary column is declared as wide as a column can be short of (max):
    /// nvarchar(4000), varchar(8000), varbinary(8000). It does not depend on the rows, and it is
    /// what the catalog says the column is. A linked server needs both: it asks for a query's
    /// columns before it runs the query - from the catalog, or from sp_prepare, which has no rows
    /// - and refuses the query if the length it is then sent is any different. A column becomes
    /// (max) only when a value in it does not fit 8000 bytes, so a long value still arrives whole.
    ///
    /// A fixed-length binary column is sized from its widest value, since every value is padded
    /// to the declared length.
    /// </summary>
    public static IReadOnlyList<int> MeasureSizes(ResultSet result)
    {
        var sizes = new int[result.Columns.Count];

        for (int i = 0; i < result.Columns.Count; i++)
        {
            var column = result.Columns[i];
            if (column.Type is not (SqlColumnType.NVarChar or SqlColumnType.VarChar or SqlColumnType.VarBinary
                                    or SqlColumnType.Binary))
                continue;

            if (column.MaxLength is { } declared)
            {
                sizes[i] = declared == MaxSize ? MaxSize : Math.Clamp(declared, 1, MaxTextBytes);
                continue;
            }

            int widest = 1;
            foreach (var row in result.Rows)
            {
                if (row[i] is null) continue;

                int bytes = column.Type switch
                {
                    SqlColumnType.NVarChar => Text(row[i]!).Length * 2,
                    SqlColumnType.VarChar => Text(row[i]!).Length,
                    _ => ((byte[])row[i]!).Length,
                };
                if (bytes > widest) widest = bytes;
            }

            sizes[i] = widest > MaxTextBytes ? MaxSize
                     : column.Type == SqlColumnType.Binary ? widest
                     : MaxTextBytes;
        }

        return sizes;
    }

    /// <summary>
    /// A batch response: each result set is COLMETADATA, its ROWs, then DONE. Every DONE but the
    /// last carries the MORE bit, which is how the client knows another result set follows —
    /// verified against a real server answering "SELECT 1; SELECT 2".
    ///
    /// A batch that yields no result set at all (SET, USE, ...) must send DONE alone, with no
    /// COLMETADATA and no row count, or a client that knows the statement cannot return rows
    /// rejects the response. The ODBC driver reports that as "Invalid cursor state".
    /// </summary>
    public static byte[] BuildBatchResponse(QueryResult result, string serverName = "")
    {
        var writer = new TdsWriter();
        bool failed = result.Errors.Count > 0;

        if (result.ResultSets.Count == 0 && !failed)
        {
            WriteDone(writer, TdsConstants.Token.Done, TdsConstants.DoneStatus.Final, 0, 0);
            return writer.ToArray();
        }

        for (int i = 0; i < result.ResultSets.Count; i++)
        {
            var resultSet = result.ResultSets[i];
            bool isLast = i == result.ResultSets.Count - 1 && !failed;

            WriteBody(writer, resultSet);
            WriteDone(writer, TdsConstants.Token.Done,
                      isLast ? TdsConstants.DoneStatus.Count
                             : (ushort)(TdsConstants.DoneStatus.More | TdsConstants.DoneStatus.Count),
                      SelectCurCmd, (ulong)resultSet.Rows.Count);
        }

        if (failed)
        {
            WriteRaised(writer, result, serverName);
            WriteDone(writer, TdsConstants.Token.Done, TdsConstants.DoneStatus.Error, 0, 0);
        }

        return writer.ToArray();
    }

    /// <summary>
    /// An RPC response does not end like a batch response. Every result set is closed with
    /// DONEINPROC (MORE|COUNT) — including the last — and the request itself is terminated by
    /// RETURNSTATUS followed by DONEPROC. Send a bare DONE instead and the client keeps waiting.
    /// </summary>
    public static byte[] BuildRpcResponse(QueryResult result, string serverName = "")
    {
        const ushort procCurCmd = 0x00E0;
        var writer = new TdsWriter();

        foreach (var resultSet in result.ResultSets)
        {
            WriteBody(writer, resultSet);
            WriteDone(writer, TdsConstants.Token.DoneInProc,
                      TdsConstants.DoneStatus.More | TdsConstants.DoneStatus.Count,
                      SelectCurCmd, (ulong)resultSet.Rows.Count);
        }

        bool failed = result.Errors.Count > 0;
        if (failed) WriteRaised(writer, result, serverName);

        WriteReturnStatus(writer, failed ? 1 : 0);
        foreach (var value in result.ReturnValues) WriteReturnValue(writer, value);
        WriteDone(writer, TdsConstants.Token.DoneProc, failed ? TdsConstants.DoneStatus.Error : TdsConstants.DoneStatus.Final, procCurCmd, 0);

        return writer.ToArray();
    }

    private static void WriteRaised(TdsWriter writer, QueryResult result, string serverName)
    {
        foreach (var error in result.Errors)
            WriteError(writer, error.Number, error.Severity, error.Message, serverName, error.State, error.Procedure ?? "", error.Line);
    }

    private static void WriteBody(TdsWriter writer, ResultSet result)
    {
        var sizes = MeasureSizes(result);
        WriteColMetadata(writer, result.Columns, sizes);
        foreach (var row in result.Rows) WriteRow(writer, result.Columns, row, sizes);
    }
}
