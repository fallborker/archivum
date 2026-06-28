namespace ArchivumLib;

public readonly struct ArchivumDescriptor
{
    public long Size { get; init; }
    public long Offset { get; init; }
    public string Name { get; init; }
    public string Extension { get; init; }
}
