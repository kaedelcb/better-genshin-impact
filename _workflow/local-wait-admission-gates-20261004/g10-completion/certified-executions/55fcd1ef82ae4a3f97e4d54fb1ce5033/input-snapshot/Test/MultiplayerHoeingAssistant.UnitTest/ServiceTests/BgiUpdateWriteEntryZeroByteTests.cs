using MultiplayerHoeingAssistant.Services;
using SharpCompress.Archives;
using SharpCompress.Common;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>
/// WriteEntry 的 0 字节条目分支。真实茶包版 7z 里 0 字节占位文件是 HasStream=false 的无流条目，
/// OpenEntryStream 会抛 InvalidOperationException "File does not have a stream."（fix15 实包复现）；
/// py7zr 生成的夹具给空文件写了空流，复现不了该形态，故用 stub 模拟无流条目直接测 WriteEntry。
/// </summary>
public class BgiUpdateWriteEntryZeroByteTests
{
    /// <summary>模拟 7z 的 HasStream=false 空文件条目：Size=0，开流即抛（与 SharpCompress
    /// SevenZipFilePart.GetCompressedStream 对无流条目的行为一致）。</summary>
    private sealed class NoStreamEntry : IArchiveEntry
    {
        public Stream OpenEntryStream() => throw new InvalidOperationException("File does not have a stream.");
        public bool IsComplete => true;
        public IArchive Archive => null!;
        public CompressionType CompressionType => CompressionType.None;
        public DateTime? ArchivedTime => null;
        public long CompressedSize => 0;
        public long Crc => 0;
        public DateTime? CreatedTime => null;
        public string? Key => "EmptyDir/placeholder.txt";
        public string? LinkTarget => null;
        public bool IsDirectory => false;
        public bool IsEncrypted => false;
        public bool IsSplitAfter => false;
        public bool IsSolid => false;
        public int VolumeIndexFirst => 0;
        public int VolumeIndexLast => 0;
        public DateTime? LastAccessedTime => null;
        public DateTime? LastModifiedTime => null;
        public long Size => 0;
        public int? Attrib => null;
    }

    [Fact]
    public void ZeroByteEntry_CreatesEmptyFile_WithoutOpeningStream()
    {
        var path = Path.Combine(Path.GetTempPath(), "BgiWriteEntry-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            BgiPackageUpdateService.WriteEntry(new NoStreamEntry(), path);
            Assert.True(File.Exists(path));
            Assert.Equal(0, new FileInfo(path).Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>有流条目走回退路径（solid=null 时 OpenEntryStream）应完整落盘。</summary>
    [Fact]
    public void StreamedEntry_FallbackPath_WritesFullContent()
    {
        var path = Path.Combine(Path.GetTempPath(), "BgiWriteEntry-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            var entry = new StreamedEntry("hello-solid-fallback");
            BgiPackageUpdateService.WriteEntry(entry, path, solid: null);
            Assert.Equal("hello-solid-fallback", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class StreamedEntry : IArchiveEntry
    {
        private readonly string _content;
        public StreamedEntry(string content) => _content = content;
        public Stream OpenEntryStream() => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(_content));
        public bool IsComplete => true;
        public IArchive Archive => null!;
        public CompressionType CompressionType => CompressionType.None;
        public DateTime? ArchivedTime => null;
        public long CompressedSize => 0;
        public long Crc => 0;
        public DateTime? CreatedTime => null;
        public string? Key => "Some/File.bin";
        public string? LinkTarget => null;
        public bool IsDirectory => false;
        public bool IsEncrypted => false;
        public bool IsSplitAfter => false;
        public bool IsSolid => false;
        public int VolumeIndexFirst => 0;
        public int VolumeIndexLast => 0;
        public DateTime? LastAccessedTime => null;
        public DateTime? LastModifiedTime => null;
        public long Size => _content.Length;
        public int? Attrib => null;
    }
}
