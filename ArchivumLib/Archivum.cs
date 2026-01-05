using System.Buffers.Binary;
using System.IO.Compression;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using static ArchivumLib.Constants;

namespace ArchivumLib;

public static class Archivum
{
    private static bool _isSetup;
    private static long _dataOffset;
    private static string _fullFileName = "";
    private static Dictionary<string, ArchivumDescriptor> _table = new Dictionary<string, ArchivumDescriptor>();
    private static Dictionary<Type, Delegate> _resolverDelegateTable = new Dictionary<Type, Delegate>();

    private static void CheckSetup()
    {
        if (!_isSetup)
        {
            throw new ArchivumSetUpException();
        }
    }

    private static int ReadInt32(Stream s)
    {
        Span<byte> buf = stackalloc byte[INT_SIZE];
        s.ReadExactly(buf);

        return BinaryPrimitives.ReadInt32LittleEndian(buf);
    }

    private static long ReadInt64(Stream s)
    {
        Span<byte> buf = stackalloc byte[LONG_SIZE];
        s.ReadExactly(buf);

        return BinaryPrimitives.ReadInt64LittleEndian(buf);
    }

    private static byte[] ReadBytes(Stream s)
    {
        int totalLen = ReadInt32(s);

        byte[] bytes = new byte[totalLen];
        s.ReadExactly(bytes);

        return bytes;
    }

    private static string ReadString(Stream s)
    {
        byte[] bytes = ReadBytes(s);

        return Encoding.UTF8.GetString(bytes);
    }

    private static ArchivumDescriptor ReadArchivumDescriptor(Stream s)
    {
        return new ArchivumDescriptor
        {
            Size = ReadInt64(s),
            Offset = ReadInt64(s),
            Name = ReadString(s),
            Extension = ReadString(s)
        };
    }

    private static void LoadResourceInformation()
    {
        using (FileStream fs = File.OpenRead(_fullFileName))
        {
            // We discard the hash since there's not much use when loading
            fs.ReadExactly(stackalloc byte[HASH_SIZE]);

            int totalFileCount = ReadInt32(fs);
            _ = ReadInt32(fs); // Table size in bytes

            for (int i = 0; i < totalFileCount; i++)
            {
                ArchivumDescriptor desc = ReadArchivumDescriptor(fs);
                _table.Add(desc.Name, desc);
            }

            _ = ReadInt32(fs); // Resource size

            _dataOffset = fs.Position;
        }
    }

    private static void LoadResourceInformationIfNotLoaded()
    {
        if (_table.Keys.Count == 0)
        {
            LoadResourceInformation();
        }
    }

    private static void CreateResolverDictionary(IArchivumResolver resolver)
    {
        var methods = resolver.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m =>
                m.DeclaringType != typeof(object) &&
                m.GetParameters().Length == 2 &&
                m.GetParameters()[0].ParameterType == typeof(ArchivumDescriptor) &&
                m.GetParameters()[1].ParameterType == typeof(byte[])
            );

        foreach (var method in methods)
        {
            var returnType = method.ReturnType;

            if (_resolverDelegateTable.ContainsKey(returnType))
            {
                throw new InvalidOperationException($"Duplicate resolver for type {returnType.Name}");
            }

            ParameterExpression descParam = Expression.Parameter(typeof(ArchivumDescriptor), "descriptor");
            ParameterExpression bytesParam = Expression.Parameter(typeof(byte[]), "bytes");

            MethodCallExpression call = Expression.Call(
                Expression.Constant(resolver), // instance
                method,
                descParam,
                bytesParam
            );

            Type funcType = typeof(Func<,,>).MakeGenericType(typeof(ArchivumDescriptor), typeof(byte[]), returnType);
            Delegate typedDelegate = Expression.Lambda(funcType, call, descParam, bytesParam).Compile();

            _resolverDelegateTable[returnType] = typedDelegate;
        }

        {
            ParameterExpression descParam = Expression.Parameter(typeof(ArchivumDescriptor), "descriptor");
            ParameterExpression bytesParam = Expression.Parameter(typeof(byte[]), "bytes");

            MethodInfo undefinedMethod = resolver.GetType().GetMethod(
                "ResolveUndefined",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            ) ?? throw new InvalidOperationException("Resolver must have a ResolveUndefined method");

            MethodCallExpression call = Expression.Call(
                Expression.Constant(resolver),
                undefinedMethod,
                descParam,
                bytesParam
            );

            Delegate fallbackDelegate = Expression.Lambda<Func<ArchivumDescriptor, byte[], object>>(
                call,
                descParam,
                bytesParam
            ).Compile();

            _resolverDelegateTable[typeof(object)] = fallbackDelegate;
        }
    }

    private static byte[] UnsafeGet(ArchivumDescriptor descriptor)
    {
        using (FileStream fs = File.OpenRead(_fullFileName))
        using (MemoryStream decompressed = new MemoryStream())
        {
            byte[] bytes = new byte[descriptor.Size];

            fs.Position = _dataOffset;
            fs.ReadExactly(bytes, 0, (int)descriptor.Size);

            using (MemoryStream compressed = new MemoryStream(bytes))
            using (GZipStream gzip = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: true))
            {
                gzip.CopyTo(decompressed);
            }

            return decompressed.ToArray();
        }
    }

    private static bool UnsafeTryGet(string name, out byte[]? bytes)
    {
        if (!_table.ContainsKey(name))
        {
            bytes = null;
            return false;
        }

        ArchivumDescriptor descriptor = _table[name];

        bytes = UnsafeGet(descriptor);
        return true;
    }

    public static void Setup(string fileName, string fileExtension, IArchivumResolver resolver, bool readFileOnSetup = true)
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

        if (fileName.IndexOfAny(invalidOSPathChars) > 0 || fileExtension.IndexOfAny(invalidOSPathChars) > 0)
        {
            throw new ArgumentException("The file name or its extension must not contain invalid characters!");
        }

        string treatedFileName = fileName.Trim();
        string treatedFileExt = !string.IsNullOrWhiteSpace(fileExtension) ? $".{fileExtension.Trim().Replace(".", "")}" : "";

        _fullFileName = $"{treatedFileName}{treatedFileExt}";

        CreateResolverDictionary(resolver);

        if (readFileOnSetup)
        {
            LoadResourceInformation();
        }

        _isSetup = true;
    }

    public static byte[] Get(string name)
    {
        CheckSetup();
        LoadResourceInformationIfNotLoaded();

        if (!_table.ContainsKey(name))
        {
            throw new ArgumentException($"The key \"{name}\" does not exist in the resource file!");
        }

        ArchivumDescriptor descriptor = _table[name];

        return UnsafeGet(descriptor);
    }

    public static bool TryGet(string name, out byte[]? bytes)
    {
        CheckSetup();
        LoadResourceInformationIfNotLoaded();

        return UnsafeTryGet(name, out bytes);
    }

    public static T Get<T>(string name)
    {
        CheckSetup();
        LoadResourceInformationIfNotLoaded();

        if (!_table.ContainsKey(name))
        {
            throw new ArgumentException($"The key \"{name}\" does not exist in the resource file!");
        }

        ArchivumDescriptor descriptor = _table[name];

        byte[] bytes = UnsafeGet(descriptor);

        if (_resolverDelegateTable.TryGetValue(typeof(T), out var typeDelegate))
        {
            return ((Func<ArchivumDescriptor, byte[], T>)typeDelegate)(descriptor, bytes);
        }

        var fallback = (Func<ArchivumDescriptor, byte[], object>)_resolverDelegateTable[typeof(object)];
        return (T)fallback(descriptor, bytes);
    }

    public static bool TryGet<T>(string name, out T? value)
    {
        CheckSetup();
        LoadResourceInformationIfNotLoaded();

        if (!_table.ContainsKey(name))
        {
            value = default;
            return false;
        }

        value = Get<T>(name);
        return true;
    }

    public static void Generate(string sourceFolder, string[] extensionWhitelist)
    {
        CheckSetup();

        ArchivumGenerator generator = new ArchivumGenerator(sourceFolder, _fullFileName, extensionWhitelist);
        generator.Generate();
    }

    public static void Reset()
    {
        _table.Clear();
        _resolverDelegateTable.Clear();
        _fullFileName = "";
        _dataOffset = 0;
        _isSetup = false;
    }
}