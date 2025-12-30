using System.Buffers.Binary;
using System.Text;
using System.IO.Compression;

namespace ArchivumLib;

internal class ArchivumGenerator
{
    private readonly string _source;
    private readonly string _outputFileName;
    private readonly HashSet<string> _extWhitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private int WriteInt32(Stream s, int value)
    {
        const int int32Size = 4;

        Span<byte> buf = stackalloc byte[int32Size];
        BinaryPrimitives.WriteInt32LittleEndian(buf, value);
        s.Write(buf);

        return int32Size;
    }

    private int WriteInt64(Stream s, long value)
    {
        const int int64Size = 8;

        Span<byte> buf = stackalloc byte[int64Size];
        BinaryPrimitives.WriteInt64LittleEndian(buf, value);
        s.Write(buf);

        return int64Size;
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
        int totalLen = WriteInt64(s, descriptor.Size)
            + WriteInt64(s, descriptor.Offset)
            + WriteString(s, descriptor.Name);

        return totalLen;
    }

    public ArchivumGenerator(string sourceFolder, string outputFileName, string[] extensionWhitelist)
    {
        _source = sourceFolder;
        _outputFileName = outputFileName;

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

        using (MemoryStream contentBytes = new MemoryStream())
        using (MemoryStream tableBytes = new MemoryStream())
        {
            long offset = 0;
            int processedFileCount = 0;

            foreach (string file in Directory.EnumerateFiles(_source, "*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file);
                if (!_extWhitelist.Contains(ext))
                {
                    continue;
                }

                string directory = Path.GetDirectoryName(file)!;
                string relativeDir = Path.GetRelativePath(_source, directory);

                string prefix = relativeDir == "." ? "" : relativeDir.Replace(Path.DirectorySeparatorChar, '/');
                string fileName = Path.GetFileNameWithoutExtension(file);

                using (MemoryStream compressed = new MemoryStream())
                using (GZipStream gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                {
                    gzip.Write(File.ReadAllBytes(file));

                    compressed.Position = 0;
                    compressed.CopyTo(contentBytes);
                
                    long size = compressed.Length;

                    ArchivumDescriptor desc = new ArchivumDescriptor()
                    {
                        Name = $"{prefix}{(string.IsNullOrWhiteSpace(prefix) ? "" : "/")}{fileName}",
                        Size = size,
                        Offset = offset
                    };

                    WriteArchivumDescriptor(tableBytes, desc);

                    offset += size;
                    processedFileCount++;
                }
            }

            if (processedFileCount == 0)
            {
                return;
            }

            using (FileStream fs = File.Create(_outputFileName))
            {
                WriteInt32(fs, processedFileCount);
                WriteBytes(fs, tableBytes.ToArray());
                WriteBytes(fs, contentBytes.ToArray());
            }
        }
    }
}