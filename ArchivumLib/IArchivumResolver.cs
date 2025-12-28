namespace ArchivumLib;

public interface IArchivumResolver
{
    public object ResolveUndefined(ArchivumDescriptor descriptor, byte[] bytes);
}
