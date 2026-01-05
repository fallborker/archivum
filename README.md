# Archivum

Archivum is meant to be a small, reflective library that allows you to pack an assortment of files in a single one. The name and extension of the file can be configured to your liking.

The basic structure of the file is:

```
-----------------------------------------------------------
| Header
-----------------------------------------------------------
[16 bytes]      > [MD5 to check if generation is needed]
[4 bytes]       > [Number of files that have been packed]
[4 bytes]       > [Size X of the file table in bytes]
[X bytes]       > [File table]
 | [8 bytes]    > [Size of the 1st compressed file]
 | [8 bytes]    > [Offset* of the 1st compressed file]
 | [x0 bytes]   > [Name/Tag of the 1st compressed file]
 | [y0 bytes]   > [Extension (with '.') of the 1st file]
 .
 .
 .
 | [8 bytes]    > [Size of the Nth compressed file]
 | [8 bytes]    > [Offset* of the Nth compressed file]
 | [xN bytes]   > [Name/Tag of the Nth compressed file]
 | [y1 bytes]   > [Extension (with '.') of the Nth file]
-----------------------------------------------------------
| Body
-----------------------------------------------------------
[4 bytes]       > [Size Y of the resource array in bytes]
 | [x0 bytes]   > [1st Compressed file]
 .
 .
 .
 | [x0 bytes]   > [Nth Compressed file]
EOF
```

> \*: The offset starts counting from 0 right after the end of the header data. Therefore, Offset is not an absolute value.

## Setup

These are the minimum necessary steps to load a [generated](#generation) resource file into your application:

```csharp
using ArchivumLib;

// Resolver
// A resolver is a class containing methods that are going to be
// used by the Archivum class in order to know how to cast the byte
// arrays properly. In case a resolution method is not found,
// ResolveUndefined will be called.
//
// In this example, I'm using Raylib-cs methods to demonstrate how
// This would be used normally.

using Raylib_cs;

internal class BasicResolver : IArchivumResolver
{
    private int _thisFieldWillBeIgnored = 143;

    public int ThisMethodWillBeIgnored(int b, string c) => 0;
    private void ThisMethodWillAlsoBeIgnored(int a) {}

    // Only methods with this specific signature will be read.
    // public T AnyName(ArchivumDescriptor, byte[]);

    public Music ResolveMusic(ArchivumDescriptor _, byte[] bytes) => Raylib.LoadMusicStreamFromMemory(/* Extension here */, bytes);
    public Image ResolveImage(ArchivumDescriptor _, byte[] bytes) => Raylib.LoadImageFromMemory(/* Extension here */, bytes);
    public object ResolveUndefined(ArchivumDescriptor descriptor, byte[] bytes)
    {
        // You can do basically anything you want here! This is a fallback method.
        throw new InvalidCastException($"The resource \"{descriptor.Name}\" could not be cast!");
    }
}

// This will load MyResourceFileName.MyCoolExtension from the CWD.
// The file name is mandatory, but the extension can be left blank by either
// passing "" or null.
Archivum.Setup("MyResourceFileName", "MyCoolExtension", new BasicResolver());
```

## Generation

To generate a file for the first time, you can wrap the above [setup](#setup) call with the following conditional guards. The resource file contains a MD5 hash to check if generation is needed, and this step will be skipped if no file changes are detected.

```csharp
#if DEBUG

    // The resource file will be read when any get method is
    // called for the first time.
    Archivum.Setup("MyResourceFileName", "MyCoolExtension", new BasicResolver(), readFileOnSetup: false);

    Console.WriteLine("Generate resource file? (Type in the path to a source folder or leave blank)");
    string input = Console.ReadLine();

    // If we have the following source folder structure:
    //
    // Resources
    //  |__ fileA.png
    //  |__ fileB.txt (will be ignored, .txt is not whitelisted)
    //  |
    //  |__ SubFolder1
    //  |   |__ fileC.ogg
    //  |   |__ fileD.png
    //  |
    //  |__ SubFolder2
    //  |   |__ fileE.pdf (will be ignored, .pdf is not whitelisted)
    //  |
    //  |__ SubFolder3
    //      |__ fileF.png
    //      |
    //      |__ SubFolder4
    //      |   |__ <empty>
    //      |
    //      |__ SubFolder5
    //          |__ fileG.png
    //
    // The following tags will be accepted in Get and TryGet:
    // | fileA
    // | SubFolder1/fileC
    // | SubFolder1/fileD
    // | SubFolder3/fileF
    // | SubFolder3/SubFolder5/fileG
    string[] extWhitelist = [
        ".png",
        ".ogg",
    ];

    if (!string.IsNullOrWhiteSpace(input))
    {
        input = input.Replace("\"", "").Replace("'", "").Trim();
        Archivum.Generate(input, extWhitelist);
    }

#else

    // Do the setup normally!
    Archivum.Setup("MyResourceFileName", "MyCoolExtension", new BasicResolver());

#endif
```

## Loading resources

To access your resources as objects inside C#, simply call one of the Get or TryGet methods. Do keep in mind that Get WILL throw an exception if the name of the resource you are trying to access is not found within the file.

```csharp
Image image;
bool resourceExists;

image = Archivum.Get<Image>("fileA"); // OK.
resourceExists = Archivum.TryGet<Image>("fileA", out Image outImage1); // OK - TRUE.
resourceExists = Archivum.TryGet<Image>("fileK", out Image outImage2); // OK - FALSE.
image = Archivum.Get<Image>("fileK"); // NO! Will throw an ArgumentException.

// You can also get the raw data via Get and TryGet

byte[] raw = Archivum.Get("fileA");
resourceExists = Archivum.TryGet("fileA", out byte[]? bytes);

// Image objects from raylib in this example still need to be unloaded :)
// They're unmanaged, folks!
```

## License

This library is licensed to you under the BSD 3-clause license.

## Additional notes

This project was hacked together in a single saturday, and it is currently 2:52AM on a sunday as I am writing this README because NuGet whines if you dont add one...
That is to say that this lib is not in a finished state, and I don't really know if it will ever be. I made this for my own enjoyment and published it to see how the process works. Hopefully it is of good use to you, stranger! :D