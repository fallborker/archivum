namespace ArchivumLib;

internal static class Crc32
{
    private static readonly uint[] Table = GenerateTable();

    private static uint[] GenerateTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (0xEDB88320 ^ (crc >> 1)) : (crc >> 1);
            }
            table[i] = crc;
        }
        return table;
    }

    public static uint Compute(Stream stream, long length)
    {
        uint crc = 0xFFFFFFFF;
        byte[] buffer = new byte[4096];
        long remaining = length;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = stream.Read(buffer, 0, toRead);
            if (read == 0) break;
            for (int i = 0; i < read; i++)
            {
                crc = Table[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
            }
            remaining -= read;
        }
        return crc ^ 0xFFFFFFFF;
    }
}
