# Archivum

Archivum is meant to be a small, reflective library that allows you to pack an assortment of files in a single one. The name and extension of the file can be configured to your liking.

The basic structure of the file as of December, 2025 is:

```
---------------------------------------------------------
| Header
---------------------------------------------------------
[4 bytes]       > [Number of files that have been packed]
[4 bytes]       > [Size X of the file table in bytes]
[X bytes]       > [File table]
 | [8 bytes]    > [Size of the 1st compressed file]
 | [8 bytes]    > [Offset* of the 1st compressed file]
 | [x0 bytes]   > [Name/Tag of the 1st compressed file]
 .
 .
 .
 | [8 bytes]    > [Size of the Nth compressed file]
 | [8 bytes]    > [Offset* of the Nth compressed file]
 | [xN bytes]   > [Name/Tag of the Nth compressed file]
---------------------------------------------------------
| Body
---------------------------------------------------------
[4 bytes]       > [Size Y of the content array in bytes]
 | [x0 bytes]   > [1st Compressed file]
 .
 .
 .
 | [x0 bytes]   > [Nth Compressed file]
EOF
```

> \* The offset starts counting from 0 right after the end of the header content. Therefore, Offset is not an absolute value.

## Usage

A small example of usage can be seen here, in which a content file can be generated in the Debug build and used in all other targets:

```csharp
using Raylib_cs;

// Resolver
// A resolver is a class containing methods that are going to be
// used by the main class in order to know how to cast the byte
// arrays properly. In case a resolution method is not found,
// ResolveUndefined will be called.
//
// In this example, I'm using Raylib-cs methods to demonstrate how
// This would be used normally.
internal class BasicResolver : IArchivumResolver
{
    public void ThisMethodWillBeIgnored(int a) {}
    public int ThisMethodWillAlsoBeIgnored(int b, string c) => 0;

    public Music ResolveMusic(ArchivumDescriptor _, byte[] bytes) => Raylib.LoadMusicStreamFromMemory(/* Extension here */, bytes);
    public Image ResolveImage(ArchivumDescriptor _, byte[] bytes) => Raylib.LoadImageFromMemory(/* Extension here */, bytes);
    public object ResolveUndefined(ArchivumDescriptor descriptor, byte[] bytes)
    {
        // You can do basically anything you want here! This is a fallback method.
        throw new InvalidCastException($"The resource \"{descriptor.Name}\" could not be cast!");
    }
}

// [...] public static int Main() [...]

// This will generate MyContentFileName.MyCoolExtension at the CWD.
Archivum.Setup("MyContentFileName", "MyCoolExtension", new BasicResolver());

// Calling Archivum.Generate in Release will throw an exception.
// The method body and all its fields are not generated in Release,
// as to not ship with any unnecessary code.
#if DEBUG

        Console.WriteLine("Generate content? (Type in the path to a source folder path or leave blank)");
        string input = Console.ReadLine();

        // If we have the following source folder structure:
        //
        // Content
        //  |_ fileA.png
        //  |_ fileB.txt (will be ignored, .txt is not whitelisted)
        //  |
        //  |_ SubFolder1
        //  |   |_ fileC.ogg
        //  |   |_ fileD.png
        //  |
        //  |_ SubFolder2
        //  |   |_ fileE.pdf (will be ignored, .pdf is not whitelisted)
        //  |
        //  |_ SubFolder3
        //      |_ fileF.png
        //      |
        //      |_ SubFolder4
        //      |   |_ <empty>
        //      |
        //      |_ SubFolder5
        //          |_ fileG.png
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

#endif

// [...]
// The generated file must be in the CWD for loading to work.

Image i = Archivum.Get<Image>("fileA"); // OK.
bool fileAExists = Archivum.TryGet<Image>("fileA", out Image i2); // OK - TRUE.
bool fileKExists = Archivum.TryGet<Image>("fileK", out Image i3); // OK - FALSE.
Image i4 = Archivum.Get<Image>("fileK"); // NO! Will throw an ArgumentException.

// Image objects from raylib in this example still need to be unloaded :)
// They're unmanaged, folks!
```

## License

This library is licensed to you under the BSD 3-clause license.

## Additional notes

This project was hacked together in a single saturday, and it is currently 2:52AM on a sunday as I am writing this README because NuGet whines if you dont add one...
That is to say that this lib is not in a finished state, and I don't really know if it will ever be. I made this for my own enjoyment and published it to see how the process works. Hopefully it is of good use to you, stanger! :D