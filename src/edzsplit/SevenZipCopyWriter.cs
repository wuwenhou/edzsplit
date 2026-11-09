using System.Text;

namespace EdzSplit;

/// <summary>One file to place into a generated 7z container.</summary>
internal sealed class SevenZipCopyEntry
{
    public string Key = "";
    public long Size;
    public Func<Stream> Open = () => Stream.Null;
}

/// <summary>
/// Minimal managed 7z writer that emits the smallest valid 7z: one folder,
/// one packed stream, the "Copy" coder (stored, no compression) and a single
/// file list. 7-Zip and EPLAN both accept this layout (it is the layout the
/// user's Tool-Oumijia add-in already used for filtered EDZ export).
///
/// EPLAN EDZ files always use the 7z container and store entry names with
/// backslashes (e.g. "items\macro\Rittal\AX\AX2519010.ema"). This writer
/// preserves those names byte-for-byte on every OS.
/// </summary>
internal static class SevenZipCopyWriter
{
    private const byte K_HEADER = 0x01;
    private const byte K_MAIN_STREAMS_INFO = 0x04;
    private const byte K_FILES_INFO = 0x05;
    private const byte K_PACK_INFO = 0x06;
    private const byte K_UNPACK_INFO = 0x07;
    private const byte K_SUBSTREAMS_INFO = 0x08;
    private const byte K_SIZE = 0x09;
    private const byte K_CRC = 0x0A;
    private const byte K_FOLDER = 0x0B;
    private const byte K_CODERS_UNPACK_SIZE = 0x0C;
    private const byte K_NUM_UNPACK_STREAM = 0x0D;
    private const byte K_NAME = 0x11;
    private const byte K_END = 0x00;

    private const byte CODER_COPY = 0x00;

    public static void Write(string filePath, IList<SevenZipCopyEntry> entries)
    {
        if (entries.Count == 0) throw new ArgumentException("At least one entry is required.", nameof(entries));

        // Official EDZ files store entry names with forward slashes (UTF-16LE), e.g.
        // "items/partxml/THB.0060002.part.xml". Normalize every key to that form.
        entries = entries.Select(e => new SevenZipCopyEntry { Key = e.Key.Replace('\\', '/'), Size = e.Size, Open = e.Open }).ToList();

        using (var output = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16))
        {
            WriteSignatureHeader(output);

            long totalSize = 0;
            uint crc = Crc32.Initial;
            var buffer = new byte[128 * 1024];

            foreach (var entry in entries)
            {
                using var source = entry.Open();
                if (source == null)
                    throw new InvalidOperationException("Cannot open EDZ entry: " + entry.Key);

                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    crc = Crc32.Append(crc, buffer, 0, read);
                    totalSize += read;
                }
            }

            var header = BuildHeader(entries, totalSize, Crc32.Finish(crc));

            // StartHeader: NextHeaderOffset / NextHeaderSize / NextHeaderCRC at bytes 12..31.
            output.Seek(12, SeekOrigin.Begin);
            WriteUInt64(output, (ulong)totalSize);
            WriteUInt64(output, (ulong)header.Length);
            WriteUInt32(output, Crc32.Compute(header, 0, header.Length));

            // StartHeaderCRC covers bytes 12..31.
            var startHeader = new byte[20];
            output.Seek(12, SeekOrigin.Begin);
            ReadExactly(output, startHeader, startHeader.Length);
            output.Seek(8, SeekOrigin.Begin);
            WriteUInt32(output, Crc32.Compute(startHeader, 0, startHeader.Length));

            output.Seek(0, SeekOrigin.End);
            output.Write(header, 0, header.Length);
        }
    }

    private static void WriteSignatureHeader(Stream output)
    {
        var signature = new byte[32];
        signature[0] = 0x37; // '7'
        signature[1] = 0x7A; // 'z'
        signature[2] = 0xBC;
        signature[3] = 0xAF;
        signature[4] = 0x27;
        signature[5] = 0x1C;
        signature[6] = 0x00; // major version
        signature[7] = 0x04; // minor version
        output.Write(signature, 0, signature.Length);
    }

    private static byte[] BuildHeader(IList<SevenZipCopyEntry> entries, long totalSize, uint folderCrc)
    {
        using var header = new MemoryStream();
        header.WriteByte(K_HEADER);
        header.WriteByte(K_MAIN_STREAMS_INFO);

        header.WriteByte(K_PACK_INFO);
        WriteNumber(header, 0);
        WriteNumber(header, 1);
        header.WriteByte(K_SIZE);
        WriteNumber(header, (ulong)totalSize);
        header.WriteByte(K_END);

        header.WriteByte(K_UNPACK_INFO);
        header.WriteByte(K_FOLDER);
        WriteNumber(header, 1);
        header.WriteByte(0x00);
        WriteNumber(header, 1);
        header.WriteByte(0x01);
        header.WriteByte(CODER_COPY);
        header.WriteByte(K_CODERS_UNPACK_SIZE);
        WriteNumber(header, (ulong)totalSize);
        header.WriteByte(K_CRC);
        header.WriteByte(0x01);
        WriteUInt32(header, folderCrc);
        header.WriteByte(K_END);

        header.WriteByte(K_SUBSTREAMS_INFO);
        if (entries.Count > 1)
        {
            header.WriteByte(K_NUM_UNPACK_STREAM);
            WriteNumber(header, (ulong)entries.Count);
            header.WriteByte(K_SIZE);
            for (var i = 0; i < entries.Count - 1; i++)
                WriteNumber(header, (ulong)entries[i].Size);
        }
        header.WriteByte(K_END);
        header.WriteByte(K_END);

        header.WriteByte(K_FILES_INFO);
        WriteNumber(header, (ulong)entries.Count);
        header.WriteByte(K_NAME);
        long nameBytes = 1;
        foreach (var entry in entries)
            nameBytes += (long)Encoding.Unicode.GetByteCount(entry.Key) + 2;
        WriteNumber(header, (ulong)nameBytes);
        header.WriteByte(0x00);

        foreach (var entry in entries)
        {
            var name = Encoding.Unicode.GetBytes(entry.Key);
            header.Write(name, 0, name.Length);
            header.WriteByte(0x00);
            header.WriteByte(0x00);
        }
        header.WriteByte(K_END);
        header.WriteByte(K_END);

        return header.ToArray();
    }

    private static void WriteNumber(Stream stream, ulong value)
    {
        byte firstByte = 0;
        byte mask = 0x80;
        int i;
        for (i = 0; i < 8; i++)
        {
            if (value < ((ulong)1 << (7 * (i + 1))))
            {
                firstByte |= (byte)(value >> (8 * i));
                break;
            }
            firstByte |= mask;
            mask >>= 1;
        }
        stream.WriteByte(firstByte);
        for (; i > 0; i--)
        {
            stream.WriteByte((byte)value);
            value >>= 8;
        }
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        stream.WriteByte((byte)value);
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 24));
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        for (var i = 0; i < 8; i++) stream.WriteByte((byte)(value >> (8 * i)));
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int count)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = stream.Read(buffer, offset, count - offset);
            if (read <= 0) throw new EndOfStreamException("Unexpected end of file while patching 7z header.");
            offset += read;
        }
    }
}

/// <summary>Standard IEEE CRC-32 (same polynomial 7z and ZIP use).</summary>
internal static class Crc32
{
    public const uint Initial = 0xFFFFFFFF;
    private static readonly uint[] Table = BuildTable();

    public static uint Append(uint crc, byte[] data, int offset, int count)
    {
        for (var i = 0; i < count; i++)
            crc = Table[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    public static uint Finish(uint crc) => crc ^ 0xFFFFFFFF;

    public static uint Compute(byte[] data, int offset, int count)
        => Finish(Append(Initial, data, offset, count));

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? (value >> 1) ^ 0xEDB88320u : value >> 1;
            table[i] = value;
        }
        return table;
    }
}
