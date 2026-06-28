using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using static ArchivumLib.BinaryIO;
using static ArchivumLib.Constants;

namespace ArchivumLib;

internal class ArchivumGenerator
{
    private readonly string _source;
    private readonly string _outputFileName;
    private readonly string? _comment;
    private readonly byte[]? _xorKey;
    private readonly HashSet<string> _extWhitelist;

    private int WriteArchivumDescriptor(Stream s, ArchivumDescriptor descriptor)
    {
        int totalLen =
            WriteInt64(s, descriptor.Size)
            + WriteInt64(s, descriptor.Offset)
            + WriteString(s, descriptor.Name)
            + WriteString(s, descriptor.Extension);

        return totalLen;
    }

    public ArchivumGenerator(
        string sourceFolder,
        string outputFileName,
        string[] extensionWhitelist,
        string? key = null,
        string? comment = null
    )
    {
        _source = sourceFolder;
        _outputFileName = outputFileName;
        _comment = comment;
        _xorKey = DeriveKey(key);

        _extWhitelist = extensionWhitelist
            .Select(ext => $".{ext.Trim().Replace(".", "")}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public void Generate()
    {
        if (!Path.Exists(_source))
        {
            throw new DirectoryNotFoundException();
        }

        byte BuildFlags() =>
            (byte)((_comment != null ? FLAG_COMMENT : 0) | (_xorKey != null ? FLAG_OBFUSCATED : 0));

        byte[] computeHash;
        try
        {
            StringBuilder hashSource = new StringBuilder();
            foreach (
                string file in Directory
                    .EnumerateFiles(_source, "*", SearchOption.AllDirectories)
                    .OrderBy(p => p, StringComparer.Ordinal)
            )
            {
                FileInfo fileInfo = new FileInfo(file);

                hashSource
                    .Append(fileInfo.FullName)
                    .Append(fileInfo.Length)
                    .Append(fileInfo.LastWriteTimeUtc);
            }

            using MD5 md5 = MD5.Create();
            computeHash = md5.ComputeHash(Encoding.UTF8.GetBytes(hashSource.ToString()));
        }
        catch
        {
            Console.WriteLine("Could not calculate the hash due to an exception!");
            throw;
        }

        // If a file already exists and we're changing it, we must first check if any changes are necessary.
        bool fileExists = File.Exists(_outputFileName);
        bool doNotGenerate = fileExists;
        if (fileExists)
        {
            Span<byte> buffer = stackalloc byte[HASH_SIZE];

            try
            {
                byte newFlags = BuildFlags();

                using (FileStream fs = File.OpenRead(_outputFileName))
                {
                    Span<byte> magic = stackalloc byte[4];
                    fs.ReadExactly(magic);

                    byte oldFlags = (byte)fs.ReadByte();

                    if (oldFlags != newFlags)
                        doNotGenerate = false;

                    string? oldComment = null;
                    if ((oldFlags & FLAG_COMMENT) != 0)
                        oldComment = ReadString(fs);

                    if (doNotGenerate && _comment != oldComment)
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

        using MemoryStream resourceBytes = new MemoryStream();
        using MemoryStream tableBytes = new MemoryStream();
        long offset = 0;
        int processedFileCount = 0;

        var files = Directory
            .EnumerateFiles(_source, "*", SearchOption.AllDirectories)
            .Where(file => _extWhitelist.Contains(Path.GetExtension(file)))
            .Select(file =>
            {
                string directory = Path.GetDirectoryName(file)!;
                string relativeDir = Path.GetRelativePath(_source, directory);
                string prefix =
                    relativeDir == "." ? "" : relativeDir.Replace(Path.DirectorySeparatorChar, '/');
                string fileName = Path.GetFileNameWithoutExtension(file);
                return (Path: file, Prefix: prefix, Name: fileName);
            });

        foreach (var (path, prefix, name) in files)
        {
            long originalPosition = resourceBytes.Position;

            using (
                GZipStream gzip = new GZipStream(
                    resourceBytes,
                    CompressionLevel.Optimal,
                    leaveOpen: true
                )
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

        using (FileStream fs = File.Create(_outputFileName))
        {
            byte flags = BuildFlags();

            fs.Write(PreambleMagic);
            fs.WriteByte(flags);

            if (_comment != null)
                WriteString(fs, _comment);

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
