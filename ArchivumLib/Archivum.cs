using System.Buffers.Binary;
using System.IO.Compression;
using System.Linq.Expressions;
using System.Reflection;
using static ArchivumLib.BinaryIO;
using static ArchivumLib.Constants;

namespace ArchivumLib;

public static class Archivum
{
    private static readonly object _lock = new();
    private static bool _isSetup;
    private static long _dataOffset;
    private static string _fullFileName = "";
    private static string _fullFilePath = "";
    private static byte _flags;
    private static byte[]? _xorKey;
    private static bool _verifyCrc = true;
    private static readonly Dictionary<string, ArchivumDescriptor> _table =
        new Dictionary<string, ArchivumDescriptor>();
    private static readonly Dictionary<Type, Delegate> _resolverDelegateTable =
        new Dictionary<Type, Delegate>();

    private static void CheckSetup()
    {
        if (!_isSetup)
        {
            throw new ArchivumSetUpException();
        }
    }

    private static ArchivumDescriptor ReadArchivumDescriptor(Stream s)
    {
        return new ArchivumDescriptor
        {
            Size = ReadInt64(s),
            Offset = ReadInt64(s),
            Name = ReadString(s),
            Extension = ReadString(s),
        };
    }

    private static byte ReadPreamble(Stream fs)
    {
        Span<byte> magic = stackalloc byte[4];
        fs.ReadExactly(magic);

        if (!magic.SequenceEqual(PreambleMagic))
            throw new InvalidDataException("Invalid archive file — missing ARCH preamble magic.");

        byte flags = (byte)fs.ReadByte();

        if ((flags & FLAG_COMMENT) != 0)
        {
            int commentLen = ReadInt32(fs);
            fs.Seek(commentLen, SeekOrigin.Current);
        }

        return flags;
    }

    private static void LoadResourceInformation()
    {
        using (var fs = File.OpenRead(_fullFilePath))
        {
            _flags = ReadPreamble(fs);

            long crcPosition = fs.Position;

            uint storedCrc = 0;
            if (_verifyCrc)
            {
                Span<byte> crcBuf = stackalloc byte[CRC_SIZE];
                fs.ReadExactly(crcBuf);
                storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(crcBuf);
            }
            else
            {
                fs.Seek(CRC_SIZE, SeekOrigin.Current);
            }

            Span<byte> hashBuf = stackalloc byte[HASH_SIZE];
            fs.ReadExactly(hashBuf);

            int totalFileCount = ReadInt32(fs);
            int tableSize = ReadInt32(fs);

            byte[] tableBytes = new byte[tableSize];
            fs.ReadExactly(tableBytes);

            if ((_flags & FLAG_OBFUSCATED) != 0)
            {
                if (_xorKey == null)
                    throw new ArchivumSetUpException(
                        "Archive is obfuscated but no key was provided."
                    );

                XorBlock(tableBytes, _xorKey!, 8);
            }

            using (var tableStream = new MemoryStream(tableBytes))
            {
                for (int i = 0; i < totalFileCount; i++)
                {
                    var desc = ReadArchivumDescriptor(tableStream);
                    _table.Add(desc.Name, desc);
                }
            }

            int resourceSize = ReadInt32(fs);
            _dataOffset = fs.Position;

            if (_verifyCrc)
            {
                fs.Position = crcPosition + CRC_SIZE;
                long dataEnd = _dataOffset + resourceSize;
                uint computedCrc = Crc32.Compute(fs, dataEnd - (crcPosition + CRC_SIZE));

                if (computedCrc != storedCrc)
                    throw new InvalidDataException(
                        "Archive CRC check failed. The file may be corrupted."
                    );
            }
        }
    }

    private static void CreateResolverDictionary(IArchivumResolver resolver)
    {
        var methods = resolver
            .GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m =>
                m.DeclaringType != typeof(object)
                && m.GetParameters().Length == 2
                && m.GetParameters()[0].ParameterType == typeof(ArchivumDescriptor)
                && m.GetParameters()[1].ParameterType == typeof(byte[])
            );

        foreach (var method in methods)
        {
            var returnType = method.ReturnType;

            if (_resolverDelegateTable.ContainsKey(returnType))
            {
                throw new InvalidOperationException(
                    $"Duplicate resolver for type {returnType.Name}"
                );
            }

            var descParam = Expression.Parameter(typeof(ArchivumDescriptor), "descriptor");
            var bytesParam = Expression.Parameter(typeof(byte[]), "bytes");

            var call = Expression.Call(
                Expression.Constant(resolver), // instance
                method,
                descParam,
                bytesParam
            );

            var funcType = typeof(Func<,,>).MakeGenericType(
                typeof(ArchivumDescriptor),
                typeof(byte[]),
                returnType
            );
            var typedDelegate = Expression.Lambda(funcType, call, descParam, bytesParam).Compile();

            _resolverDelegateTable[returnType] = typedDelegate;
        }

        {
            var descParam = Expression.Parameter(typeof(ArchivumDescriptor), "descriptor");
            var bytesParam = Expression.Parameter(typeof(byte[]), "bytes");

            var undefinedMethod =
                resolver
                    .GetType()
                    .GetMethod(
                        "ResolveUndefined",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                    )
                ?? throw new InvalidOperationException(
                    "Resolver must have a ResolveUndefined method"
                );

            var call = Expression.Call(
                Expression.Constant(resolver),
                undefinedMethod,
                descParam,
                bytesParam
            );

            Delegate fallbackDelegate = Expression
                .Lambda<Func<ArchivumDescriptor, byte[], object>>(call, descParam, bytesParam)
                .Compile();

            _resolverDelegateTable[typeof(object)] = fallbackDelegate;
        }
    }

    private static byte[] UnsafeGet(ArchivumDescriptor descriptor)
    {
        using var fs = File.OpenRead(_fullFilePath);
        using var decompressed = new MemoryStream();
        byte[] bytes = new byte[descriptor.Size];

        fs.Position = _dataOffset + descriptor.Offset;
        fs.ReadExactly(bytes, 0, (int)descriptor.Size);

        if ((_flags & FLAG_OBFUSCATED) != 0)
        {
            XorBlock(bytes, _xorKey!, (int)descriptor.Offset);
        }

        using (var compressed = new MemoryStream(bytes))
        using (var gzip = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: true))
        {
            gzip.CopyTo(decompressed);
        }

        return decompressed.TryGetBuffer(out var buffer)
            ? buffer.ToArray()
            : decompressed.ToArray();
    }

    private static bool UnsafeTryGet(string name, out byte[]? bytes)
    {
        if (!_table.TryGetValue(name, out var descriptor))
        {
            bytes = null;
            return false;
        }

        bytes = UnsafeGet(descriptor);
        return true;
    }

    public static void Setup(
        string fileName,
        string fileExtension,
        IArchivumResolver resolver,
        bool readFileOnSetup = true,
        string? key = null,
        bool verifyCrc = true
    )
    {
        lock (_lock)
        {
            if (_isSetup)
            {
                throw new ArchivumSetUpException();
            }

            char[] invalidOSPathChars = Path.GetInvalidFileNameChars();

            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new ArgumentException("The file name must not be empty!", nameof(fileName));
            }

            if (
                fileName.IndexOfAny(invalidOSPathChars) >= 0
                || fileExtension.IndexOfAny(invalidOSPathChars) >= 0
            )
            {
                throw new ArgumentException(
                    "The file name or its extension must not contain invalid characters!"
                );
            }

            string treatedFileName = fileName.Trim();
            string treatedFileExt = !string.IsNullOrWhiteSpace(fileExtension)
                ? $".{fileExtension.Trim().Replace(".", "")}"
                : "";

            _fullFileName = $"{treatedFileName}{treatedFileExt}";
            _fullFilePath = Path.Combine(AppContext.BaseDirectory, _fullFileName);

            _xorKey = DeriveKey(key);
            _verifyCrc = verifyCrc;

            CreateResolverDictionary(resolver);

            if (readFileOnSetup)
            {
                LoadResourceInformation();
            }

            _isSetup = true;
        }
    }

    public static byte[] Get(string name)
    {
        lock (_lock)
        {
            CheckSetup();
            if (_table.Count == 0)
                LoadResourceInformation();

            return !_table.TryGetValue(name, out var descriptor)
                ? throw new ArgumentException(
                    $"The key \"{name}\" does not exist in the resource file!"
                )
                : UnsafeGet(descriptor);
        }
    }

    public static bool TryGet(string name, out byte[]? bytes)
    {
        lock (_lock)
        {
            CheckSetup();
            if (_table.Count == 0)
                LoadResourceInformation();

            return UnsafeTryGet(name, out bytes);
        }
    }

    public static T Get<T>(string name)
    {
        lock (_lock)
        {
            CheckSetup();
            if (_table.Count == 0)
                LoadResourceInformation();

            if (!_table.TryGetValue(name, out var descriptor))
            {
                throw new ArgumentException(
                    $"The key \"{name}\" does not exist in the resource file!"
                );
            }

            byte[] bytes = UnsafeGet(descriptor);

            if (_resolverDelegateTable.TryGetValue(typeof(T), out var typeDelegate))
            {
                return ((Func<ArchivumDescriptor, byte[], T>)typeDelegate)(descriptor, bytes);
            }

            var fallback =
                (Func<ArchivumDescriptor, byte[], object>)_resolverDelegateTable[typeof(object)];
            return (T)fallback(descriptor, bytes);
        }
    }

    public static bool TryGet<T>(string name, out T? value)
    {
        lock (_lock)
        {
            CheckSetup();
            if (_table.Count == 0)
                LoadResourceInformation();

            if (!_table.ContainsKey(name))
            {
                value = default;
                return false;
            }

            value = Get<T>(name);
            return true;
        }
    }

    public static void Generate(
        string sourceFolder,
        string[] extensionWhitelist,
        string? comment = null,
        string? key = null
    )
    {
        lock (_lock)
        {
            CheckSetup();

            var generator = new ArchivumGenerator(
                sourceFolder,
                _fullFileName,
                extensionWhitelist,
                key,
                comment
            );
            generator.Generate();
        }
    }

    public static void Reset()
    {
        lock (_lock)
        {
            _table.Clear();
            _resolverDelegateTable.Clear();
            _fullFileName = "";
            _fullFilePath = "";
            _dataOffset = 0;
            _flags = 0;
            _xorKey = null;
            _isSetup = false;
        }
    }
}
