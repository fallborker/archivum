namespace ArchivumLib;

internal static class Constants
{
    public const int INT_SIZE = 4;
    public const int LONG_SIZE = 8;
    public const int HASH_SIZE = 16;
    public const int MAGIC_SIZE = 4;
    public const int CRC_SIZE = 4;

    public static ReadOnlySpan<byte> PreambleMagic => [0x41, 0x52, 0x43, 0x48]; // "ARCH"
    public static ReadOnlySpan<byte> CrcFooterMagic => [0x43, 0x52, 0x43, 0x43]; // "CRCC"
}
