using Orpheus.Core.Media;

namespace Orpheus.Core.Tests.Media;

public class MediaSourceTests : IDisposable
{
    private readonly string _tempDir;

    public MediaSourceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"orpheus_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void FromFile_CreatesLocalFileSource()
    {
        var filePath = Path.Combine(_tempDir, "test.mp3");
        File.WriteAllBytes(filePath, [0xFF, 0xFB]);

        var source = MediaSource.FromFile(filePath);

        Assert.Equal(MediaSourceType.LocalFile, source.Type);
        Assert.True(source.Uri.IsFile);
        Assert.Equal("test", source.DisplayName);
    }

    [Fact]
    public void FromFile_ThrowsOnMissingFile()
    {
        Assert.Throws<FileNotFoundException>(
            () => MediaSource.FromFile("/nonexistent/file.mp3"));
    }

    [Fact]
    public void FromFile_ThrowsOnNullOrEmpty()
    {
        Assert.Throws<ArgumentException>(() => MediaSource.FromFile(""));
        Assert.Throws<ArgumentException>(() => MediaSource.FromFile("   "));
    }

    [Fact]
    public void FromUri_CreatesNetworkStreamSource()
    {
        var source = MediaSource.FromUri("http://stream.example.com:8000/radio");

        Assert.Equal(MediaSourceType.NetworkStream, source.Type);
        Assert.Equal("http://stream.example.com:8000/radio", source.Uri.ToString());
    }

    [Fact]
    public void FromUri_HandlesHttps()
    {
        var source = MediaSource.FromUri("https://stream.example.com/radio");

        Assert.Equal(MediaSourceType.NetworkStream, source.Type);
    }

    [Fact]
    public void FromUri_HandlesRtsp()
    {
        var source = MediaSource.FromUri("rtsp://stream.example.com/live");

        Assert.Equal(MediaSourceType.NetworkStream, source.Type);
    }

    [Fact]
    public void FromUri_FileSchemeIsLocalFile()
    {
        var source = MediaSource.FromUri("file:///tmp/test.mp3");

        Assert.Equal(MediaSourceType.LocalFile, source.Type);
    }

    [Fact]
    public void FromUri_ThrowsOnRelativeUri()
    {
        Assert.Throws<UriFormatException>(() => MediaSource.FromUri("relative/path.mp3"));
    }

    [Fact]
    public void FromUri_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => MediaSource.FromUri((Uri)null!));
    }

    [Fact]
    public void DisplayName_CanBeChanged()
    {
        var source = MediaSource.FromUri("http://example.com/stream");
        source.DisplayName = "My Stream";

        Assert.Equal("My Stream", source.DisplayName);
    }

    [Fact]
    public void ToString_ReturnsDisplayNameWhenSet()
    {
        var source = MediaSource.FromUri("http://example.com/stream");
        source.DisplayName = "Cool Stream";

        Assert.Equal("Cool Stream", source.ToString());
    }

    [Fact]
    public void ToString_FallsBackToUri()
    {
        var source = MediaSource.FromUri("http://example.com/stream");

        Assert.Equal("http://example.com/stream", source.ToString());
    }
}
