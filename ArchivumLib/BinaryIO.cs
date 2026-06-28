using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using static ArchivumLib.Constants;

namespace ArchivumLib;

internal static class BinaryIO
{
    public static int ReadInt32(Stream s)
    {
        Span<byte> buf = stackalloc byte[INT_SIZE];
        s.ReadExactly(buf);

        return BinaryPrimitives.ReadInt32LittleEndian(buf);
    }

    public static long ReadInt64(Stream s)
    {
        Span<byte> buf = stackalloc byte[LONG_SIZE];
        s.ReadExactly(buf);

        return BinaryPrimitives.ReadInt64LittleEndian(buf);
    }

    public static byte[] ReadBytes(Stream s)
    {
        int totalLen = ReadInt32(s);

        byte[] bytes = new byte[totalLen];
        s.ReadExactly(bytes);

        return bytes;
    }

    public static string ReadString(Stream s)
    {
        byte[] bytes = ReadBytes(s);

        return Encoding.UTF8.GetString(bytes);
    }

    public static int WriteInt32(Stream s, int value)
    {
        Span<byte> buf = stackalloc byte[INT_SIZE];
        BinaryPrimitives.WriteInt32LittleEndian(buf, value);
        s.Write(buf);

        return INT_SIZE;
    }

    public static int WriteInt64(Stream s, long value)
    {
        Span<byte> buf = stackalloc byte[LONG_SIZE];
        BinaryPrimitives.WriteInt64LittleEndian(buf, value);
        s.Write(buf);

        return LONG_SIZE;
    }

    public static int WriteBytes(Stream s, byte[] bytes)
    {
        int totalLen = WriteInt32(s, bytes.Length) + bytes.Length;
        s.Write(bytes);

        return totalLen;
    }

    public static int WriteString(Stream s, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);

        return WriteBytes(s, bytes);
    }

    public static void XorBlock(byte[] data, byte[] key, int keyOffset)
    {
        for (int i = 0; i < data.Length; i++)
            data[i] ^= key[(keyOffset + i) % key.Length];
    }

    public static byte[]? DeriveKey(string? key)
    {
        return key != null
            ? SHA256.HashData(Encoding.UTF8.GetBytes(key))
            : null;
    }
}
