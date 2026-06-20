using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using static ArchivumLib.Constants;

namespace ArchivumLib;

internal class ArchivumGenerator
{
    private readonly string _source;
    private readonly string _outputFileName;
    private readonly string? _comment;
    private readonly HashSet<string> _extWhitelist = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase
    );

    private int WriteInt32(Stream s, int value)
    {
        Span<byte> buf = stackalloc byte[INT_SIZE];
        BinaryPrimitives.WriteInt32LittleEndian(buf, value);
        s.Write(buf);

        return INT_SIZE;
    }

    private int WriteInt64(Stream s, long value)
    {
        Span<byte> buf = stackalloc byte[LONG_SIZE];
        BinaryPrimitives.WriteInt64LittleEndian(buf, value);
        s.Write(buf);

        return LONG_SIZE;
    }

    private int WriteBytes(Stream s, byte[] bytes)
    {
        int totalLen = WriteInt32(s, bytes.Length) + bytes.Length;
        s.Write(bytes);

        return totalLen;
    }

    private int WriteString(Stream s, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);

        return WriteBytes(s, bytes);
    }

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
        string? comment = null
    )
    {
        _source = sourceFolder;
        _outputFileName = outputFileName;
        _comment = comment;

        foreach (string ext in extensionWhitelist)
        {
            string treatedExt = $".{ext.Trim().Replace(".", "")}";
            _extWhitelist.Add(treatedExt);
        }
    }

    public void Generate()
    {
        if (!Path.Exists(_source))
        {
            throw new DirectoryNotFoundException();
        }

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

            using (MD5 md5 = MD5.Create())
            {
                computeHash = md5.ComputeHash(Encoding.UTF8.GetBytes(hashSource.ToString()));
            }
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
                using (FileStream fs = File.OpenRead(_outputFileName))
                {
                    Archivum.SkipPreamble(fs);
                    fs.ReadExactly(buffer);
                }

                for (int i = 0; i < HASH_SIZE; i++)
                {
                    if (computeHash[i] != buffer[i])
                    {
                        doNotGenerate = false;
                        break;
                    }
                }
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

        using (MemoryStream resourceBytes = new MemoryStream())
        using (MemoryStream tableBytes = new MemoryStream())
        {
            long offset = 0;
            int processedFileCount = 0;

            foreach (
                string file in Directory.EnumerateFiles(_source, "*", SearchOption.AllDirectories)
            )
            {
                string ext = Path.GetExtension(file);
                if (!_extWhitelist.Contains(ext))
                {
                    continue;
                }

                string directory = Path.GetDirectoryName(file)!;
                string relativeDir = Path.GetRelativePath(_source, directory);

                string prefix =
                    relativeDir == "." ? "" : relativeDir.Replace(Path.DirectorySeparatorChar, '/');
                string fileName = Path.GetFileNameWithoutExtension(file);

                long originalPosition = resourceBytes.Position;

                using (
                    GZipStream gzip = new GZipStream(
                        resourceBytes,
                        CompressionLevel.Optimal,
                        leaveOpen: true
                    )
                )
                {
                    byte[] fileData = File.ReadAllBytes(file);

                    gzip.Write(fileData);
                }

                long size = resourceBytes.Position - originalPosition;

                ArchivumDescriptor desc = new ArchivumDescriptor()
                {
                    Name = $"{prefix}{(string.IsNullOrWhiteSpace(prefix) ? "" : "/")}{fileName}",
                    Size = size,
                    Offset = offset,
                    Extension = ext,
                };

                WriteArchivumDescriptor(tableBytes, desc);

                offset += size;
                processedFileCount++;
            }

            if (processedFileCount == 0)
            {
                return;
            }

            using (FileStream fs = File.Create(_outputFileName))
            {
                if (_comment != null)
                {
                    fs.Write(Constants.PreambleMagic); // magic "ARCH" (raw bytes)
                    fs.WriteByte(1); // flags: bit 0 = has comment
                    WriteString(fs, _comment);
                }

                fs.Write(computeHash, 0, computeHash.Length);

                WriteInt32(fs, processedFileCount);
                WriteBytes(fs, tableBytes.ToArray());
                WriteBytes(fs, resourceBytes.ToArray());

                long dataEnd = fs.Position;
                fs.Position = 0;
                uint crc = Crc32.Compute(fs, dataEnd);
                fs.Position = dataEnd;
                Span<byte> crcBuf = stackalloc byte[CRC_SIZE];
                BinaryPrimitives.WriteUInt32LittleEndian(crcBuf, crc);
                fs.Write(crcBuf);
                fs.Write(Constants.CrcFooterMagic);
            }

            Console.WriteLine("Generated the resource file successfuly!");
        }
    }
}
