namespace ArchivumLib;

internal class ArchivumSetUpException : Exception
{
    private const string MESSAGE = "Archivum has either already been set up and you're trying to set it up twice, or not been set up at all and therefore using any of its methods will result in an exception being thrown.";

    public ArchivumSetUpException() : base(MESSAGE) { /* ... */}
} 

internal class ArchivumGenerationOutsideDebugException : Exception
{
    private const string MESSAGE = "Cannot generate an Archivum file when the output target is not set to Debug.";

    public ArchivumGenerationOutsideDebugException() : base(MESSAGE) { /* ... */ }
}