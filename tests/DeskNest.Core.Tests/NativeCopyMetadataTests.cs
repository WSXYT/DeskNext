using System.Buffers.Binary;
using System.Text;
using DeskNest.Core.Storage;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class NativeCopyMetadataTests : IDisposable
{
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
        "DeskNext-copy-streams-" + Guid.NewGuid().ToString("N"))).FullName;

    [WindowsHandleTheory]
    [InlineData("file")]
    [InlineData("root-directory")]
    [InlineData("child-directory")]
    [InlineData("child-file")]
    public async Task NamedStreamRefusesCopyBeforeCreatingDestinationParent(string kind)
    {
        bool directory = kind != "file";
        string source = Path.Combine(root, directory ? "source-tree" : "source.txt");
        string content = directory ? Path.Combine(source, "item.txt") : source;
        if (directory) Directory.CreateDirectory(Path.Combine(source, "child"));
        File.WriteAllText(content, "main content");
        string streamOwner = kind switch
        {
            "root-directory" => source,
            "child-directory" => Path.Combine(source, "child"),
            _ => content
        };
        string stream = streamOwner + ":metadata";
        File.WriteAllText(stream, "must not be lost");
        Assert.Equal("must not be lost", File.ReadAllText(stream)); // Real NTFS positive control.
        string parent = Path.Combine(root, "not-created");
        string destination = Path.Combine(parent, directory ? "tree" : "copy.txt");
        if (directory)
            await Assert.ThrowsAsync<NotSupportedException>(() => WindowsDirectoryCopyLease.CreateAsync(source, destination));
        else
            await Assert.ThrowsAsync<NotSupportedException>(() => WindowsFileCopyLease.CreateAsync(source, destination));
        Assert.False(Directory.Exists(parent));
        Assert.Equal("main content", File.ReadAllText(content));
        Assert.Equal("must not be lost", File.ReadAllText(stream));
    }

    [WindowsHandleFact]
    public void DirectoryLeaseRecheckDetectsANewNamedStream()
    {
        string folder = Directory.CreateDirectory(Path.Combine(root, "tree")).FullName;
        using var parent = WindowsDirectoryLease.Open(root);
        using var handle = WindowsFileHandles.OpenDirectory(parent.Handle, "tree");
        using var tree = WindowsTreeLease.Capture(handle);
        File.WriteAllText(folder + ":late-stream", "late metadata");
        Assert.Equal("late metadata", File.ReadAllText(folder + ":late-stream"));
        Assert.Throws<NotSupportedException>(() => tree.VerifyUnchanged());
    }

    [WindowsHandleFact]
    public async Task PublishedFileLeaseRecheckDetectsANewNamedStream()
    {
        string source = Path.Combine(root, "source.txt");
        string destination = Path.Combine(root, "copy.txt");
        File.WriteAllText(source, "ordinary");
        using var copy = await WindowsFileCopyLease.CreateAsync(source, destination);
        // Unlike File.WriteAllText, this writer does not itself deny the copy handle's DELETE access.
        using (var writer = new FileStream(destination + ":late-stream", FileMode.Create, FileAccess.Write,
                   FileShare.ReadWrite | FileShare.Delete))
            writer.Write("late metadata"u8);
        using (var reader = new StreamReader(new FileStream(destination + ":late-stream", FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete)))
            Assert.Equal("late metadata", reader.ReadToEnd());
        Assert.Throws<NotSupportedException>(() => copy.VerifyForEnrollment());
    }

    [Fact]
    public void NativeStreamParserAcceptsOnlyCompleteDefaultData()
    {
        WindowsFileHandles.RequireDefaultDataStream(StreamBuffer("::$DATA"), directory: false);
        WindowsFileHandles.RequireDefaultDataStream([], directory: true);
        Assert.Throws<IOException>(() => WindowsFileHandles.RequireDefaultDataStream([], directory: false));
        Assert.Throws<NotSupportedException>(() => WindowsFileHandles.RequireDefaultDataStream(StreamBuffer(":named:$DATA"), false));
        byte[] multiple = StreamBuffer("::$DATA");
        BinaryPrimitives.WriteInt32LittleEndian(multiple, 40);
        Assert.Throws<NotSupportedException>(() => WindowsFileHandles.RequireDefaultDataStream(multiple, false));
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(1)]
    [InlineData(100000)]
    public void NativeStreamParserRejectsInvalidNameLengths(int length)
    {
        byte[] buffer = StreamBuffer("::$DATA");
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), length);
        Assert.Throws<IOException>(() => WindowsFileHandles.RequireDefaultDataStream(buffer, false));
    }

    [WindowsHandleFact]
    public async Task StreamQueryOverflowRefusesWithoutCopyingPartialContent()
    {
        string source = Path.Combine(root, "source.txt");
        File.WriteAllText(source, "ordinary");
        for (int i = 0; i < 100; i++) File.WriteAllText(source + $":metadata-{i:D3}", "extra");
        Assert.Equal("extra", File.ReadAllText(source + ":metadata-099"));
        string parent = Path.Combine(root, "not-created");
        await Assert.ThrowsAsync<NotSupportedException>(() => WindowsFileCopyLease.CreateAsync(source, Path.Combine(parent, "copy.txt")));
        Assert.False(Directory.Exists(parent));
        Assert.Equal("ordinary", File.ReadAllText(source));
    }

    private static byte[] StreamBuffer(string name)
    {
        byte[] text = Encoding.Unicode.GetBytes(name);
        byte[] buffer = new byte[24 + text.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), text.Length);
        text.CopyTo(buffer, 24);
        return buffer;
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
