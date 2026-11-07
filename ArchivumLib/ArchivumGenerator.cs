using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using static ArchivumLib.BinaryIO;
using static ArchivumLib.Constants;

namespace ArchivumLib;

internal class ArchivumGenerator(
    string sourceFolder,
    string outputFileName,
    string[] extensionWhitelist,
    string? key = null,
    string? comment = null
)
{
    private readonly byte[]? _xorKey = DeriveKey(key);
    private readonly HashSet<string> _extWhitelist = extensionWhitelist
        .Select(ext => $".{ext.Trim().Replace(".", "")}")
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private int WriteArchivumDescriptor(Stream s, ArchivumDescriptor descriptor)
    {
        int totalLen =
            WriteInt64(s, descriptor.Size)
            + WriteInt64(s, descriptor.Offset)
            + WriteString(s, descriptor.Name)
            + WriteString(s, descriptor.Extension);

        return totalLen;
    }

    public void Generate()
    {
        if (!Path.Exists(sourceFolder))
        {
            throw new DirectoryNotFoundException();
        }

        byte BuildFlags() =>
            (byte)((comment != null ? FLAG_COMMENT : 0) | (_xorKey != null ? FLAG_OBFUSCATED : 0));

        byte[] computeHash;
        try
        {
            var hashSource = new StringBuilder();
            foreach (
                string file in Directory
                    .EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories)
                    .OrderBy(p => p, StringComparer.Ordinal)
            )
            {
                var fileInfo = new FileInfo(file);

                hashSource
                    .Append(fileInfo.FullName)
                    .Append(fileInfo.Length)
                    .Append(fileInfo.LastWriteTimeUtc);
            }

            using var md5 = MD5.Create();
            computeHash = md5.ComputeHash(Encoding.UTF8.GetBytes(hashSource.ToString()));
        }
        catch
        {
            Console.WriteLine("Could not calculate the hash due to an exception!");
            throw;
        }

        string fullOutputFilePath = Path.Combine(AppContext.BaseDirectory, outputFileName);

        // If a file already exists and we're changing it, we must first check if any changes are necessary.
        bool fileExists = File.Exists(fullOutputFilePath);
        bool doNotGenerate = fileExists;
        if (fileExists)
        {
            Span<byte> buffer = stackalloc byte[HASH_SIZE];

            try
            {
                byte newFlags = BuildFlags();

                using (var fs = File.OpenRead(fullOutputFilePath))
                {
                    Span<byte> magic = stackalloc byte[4];
                    fs.ReadExactly(magic);

                    byte oldFlags = (byte)fs.ReadByte();

                    if (oldFlags != newFlags)
                        doNotGenerate = false;

                    string? oldComment = null;
                    if ((oldFlags & FLAG_COMMENT) != 0)
                        oldComment = ReadString(fs);

                    if (doNotGenerate && comment != oldComment)
                        doNotGenerate = false;

                    fs.ReadExactly(buffer);
                }

                if (doNotGenerate && !computeHash.AsSpan().SequenceEqual(buffer))
                    doNotGenerate = false;
            }
            catch
            {
                Console.WriteLine(
                    "An internal error occurred while attempting to read the file hash. The file will be forcibly regenerated."
                );
                doNotGenerate = false;
            }
        }

        if (doNotGenerate)
        {
            Console.WriteLine(
                "No changes were detected during the resource file generation. Skipping..."
            );
            return;
        }

        using var resourceBytes = new MemoryStream();
        using var tableBytes = new MemoryStream();
        long offset = 0;
        int processedFileCount = 0;

        var files = Directory
            .EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories)
            .Where(file => _extWhitelist.Contains(Path.GetExtension(file)))
            .Select(file =>
            {
                string directory = Path.GetDirectoryName(file)!;
                string relativeDir = Path.GetRelativePath(sourceFolder, directory);
                string prefix =
                    relativeDir == "." ? "" : relativeDir.Replace(Path.DirectorySeparatorChar, '/');
                string fileName = Path.GetFileNameWithoutExtension(file);
                return (Path: file, Prefix: prefix, Name: fileName);
            });

        foreach (var (path, prefix, name) in files)
        {
            long originalPosition = resourceBytes.Position;

            using (
                var gzip = new GZipStream(resourceBytes, CompressionLevel.Optimal, leaveOpen: true)
            )
            {
                byte[] fileData = File.ReadAllBytes(path);
                gzip.Write(fileData);
            }

            long size = resourceBytes.Position - originalPosition;

            WriteArchivumDescriptor(
                tableBytes,
                new ArchivumDescriptor()
                {
                    Name = $"{prefix}{(prefix == "" ? "" : "/")}{name}",
                    Size = size,
                    Offset = offset,
                    Extension = Path.GetExtension(path),
                }
            );

            offset += size;
            processedFileCount++;
        }

        if (processedFileCount == 0)
        {
            return;
        }

        using (var fs = File.Create(fullOutputFilePath))
        {
            byte flags = BuildFlags();

            fs.Write(PreambleMagic);
            fs.WriteByte(flags);

            if (comment != null)
                WriteString(fs, comment);

            long crcPosition = fs.Position;
            Span<byte> crcBuf = stackalloc byte[CRC_SIZE];
            fs.Write(crcBuf);

            fs.Write(computeHash, 0, computeHash.Length);

            byte[] rawTable = tableBytes.ToArray();
            byte[] rawBody = resourceBytes.ToArray();

            if (_xorKey != null)
            {
                XorBlock(rawTable, _xorKey, 8);
                XorBlock(rawBody, _xorKey, 0);
            }

            WriteInt32(fs, processedFileCount);
            WriteBytes(fs, rawTable);
            WriteBytes(fs, rawBody);

            long dataEnd = fs.Position;
            fs.Position = crcPosition + CRC_SIZE;
            uint crc = Crc32.Compute(fs, dataEnd - (crcPosition + CRC_SIZE));
            fs.Position = crcPosition;
            BinaryPrimitives.WriteUInt32LittleEndian(crcBuf, crc);
            fs.Write(crcBuf);
        }

        Console.WriteLine("Generated the resource file successfully!");
    }
}
