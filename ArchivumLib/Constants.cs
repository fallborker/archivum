namespace ArchivumLib;

internal static class Constants
{
    public const int INT_SIZE = 4;
    public const int LONG_SIZE = 8;
    public const int HASH_SIZE = 16;
    public const int CRC_SIZE = 4;

    public static ReadOnlySpan<byte> PreambleMagic => "ARCH"u8;

    public const byte FLAG_COMMENT = 0b_0000_0001;
    public const byte FLAG_OBFUSCATED = 0b_0000_0010;
}
