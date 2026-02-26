using Orpheus.Core.Media;
using Orpheus.Core.Metadata;
using Orpheus.Core.Playlist;

namespace Orpheus.Core.Tests.Playlist;

public class PlaylistFileTests : IDisposable
{
    private readonly string _tempDir;

    public PlaylistFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"orpheus_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string CreateTempFile(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string CreateDummyAudioFile(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, new byte[] { 0xFF, 0xFB, 0x90, 0x00 }); // Minimal MP3-like header.
        return path;
    }

    [Fact]
    public void ReadM3U_ParsesSimplePlaylist()
    {
        var audio1 = CreateDummyAudioFile("song1.mp3");
        var audio2 = CreateDummyAudioFile("song2.mp3");

        var m3u = CreateTempFile("test.m3u",
            $"#EXTM3U\n#EXTINF:180,Artist - Song 1\n{audio1}\n#EXTINF:240,Artist - Song 2\n{audio2}\n");

        var items = PlaylistFileReader.ReadFile(m3u);

        Assert.Equal(2, items.Count);
    }

    [Fact]
    public void ReadM3U_ParsesUrlEntries()
    {
        var m3u = CreateTempFile("radio.m3u",
            "#EXTM3U\n#EXTINF:-1,Cool Radio\nhttp://stream.example.com:8000/radio\n");

        var items = PlaylistFileReader.ReadFile(m3u);

        Assert.Single(items);
        Assert.Equal("http://stream.example.com:8000/radio", items[0].Source.Uri.ToString());
    }

    [Fact]
    public void ReadM3U_SkipsMissingFiles()
    {
        var m3u = CreateTempFile("test.m3u",
            "#EXTM3U\n/nonexistent/path/song.mp3\n");

        var items = PlaylistFileReader.ReadFile(m3u);
        Assert.Empty(items);
    }

    [Fact]
    public void ReadPLS_ParsesPlaylist()
    {
        var audio1 = CreateDummyAudioFile("song1.mp3");

        var pls = CreateTempFile("test.pls",
            $"[playlist]\nFile1={audio1}\nTitle1=My Song\nLength1=180\nNumberOfEntries=1\nVersion=2\n");

        var items = PlaylistFileReader.ReadFile(pls);

        Assert.Single(items);
    }

    [Fact]
    public void ReadFile_ThrowsOnUnsupportedFormat()
    {
        var file = CreateTempFile("test.xyz", "some content");
        Assert.Throws<NotSupportedException>(() => PlaylistFileReader.ReadFile(file));
    }

    [Fact]
    public void ReadFile_ThrowsOnMissingFile()
    {
        Assert.Throws<FileNotFoundException>(
            () => PlaylistFileReader.ReadFile("/nonexistent/playlist.m3u"));
    }

    [Fact]
    public void WriteM3U_RoundTripsUrls()
    {
        var playlist = new Core.Playlist.Playlist { Name = "Test Playlist" };
        var source = MediaSource.FromUri("http://stream.example.com/radio");
        source.DisplayName = "Cool Radio";
        playlist.Add(new PlaylistItem { Source = source });

        var outputPath = Path.Combine(_tempDir, "output.m3u");
        PlaylistFileWriter.WriteFile(playlist, outputPath);

        Assert.True(File.Exists(outputPath));
        var content = File.ReadAllText(outputPath);
        Assert.Contains("#EXTM3U", content);
        Assert.Contains("#PLAYLIST:Test Playlist", content);
        Assert.Contains("http://stream.example.com/radio", content);
    }

    [Fact]
    public void WritePLS_WritesCorrectFormat()
    {
        var playlist = new Core.Playlist.Playlist();
        var source = MediaSource.FromUri("http://stream.example.com/radio");
        source.DisplayName = "Radio Stream";
        playlist.Add(new PlaylistItem
        {
            Source = source,
            Metadata = new TrackMetadata
            {
                Title = "Radio Stream",
                Duration = TimeSpan.FromSeconds(0)
            }
        });

        var outputPath = Path.Combine(_tempDir, "output.pls");
        PlaylistFileWriter.WriteFile(playlist, outputPath);

        var content = File.ReadAllText(outputPath);
        Assert.Contains("[playlist]", content);
        Assert.Contains("File1=", content);
        Assert.Contains("Title1=", content);
        Assert.Contains("NumberOfEntries=1", content);
        Assert.Contains("Version=2", content);
    }

    [Fact]
    public void WriteM3U_WritesRelativePathsForLocalFiles()
    {
        var audio = CreateDummyAudioFile("mysong.mp3");

        var playlist = new Core.Playlist.Playlist();
        playlist.Add(new PlaylistItem { Source = MediaSource.FromFile(audio) });

        var outputPath = Path.Combine(_tempDir, "output.m3u");
        PlaylistFileWriter.WriteFile(playlist, outputPath);

        var content = File.ReadAllText(outputPath);
        // Should contain relative path (just the filename since same directory).
        Assert.Contains("mysong.mp3", content);
    }

    [Fact]
    public void WriteFile_ThrowsOnUnsupportedExtension()
    {
        var playlist = new Core.Playlist.Playlist();
        var outputPath = Path.Combine(_tempDir, "output.xyz");

        Assert.Throws<NotSupportedException>(
            () => PlaylistFileWriter.WriteFile(playlist, outputPath));
    }

    [Fact]
    public void WriteFile_AcceptsExplicitFormat()
    {
        var playlist = new Core.Playlist.Playlist();
        var source = MediaSource.FromUri("http://example.com/stream");
        playlist.Add(new PlaylistItem { Source = source });

        var outputPath = Path.Combine(_tempDir, "output.txt");
        PlaylistFileWriter.WriteFile(playlist, outputPath, PlaylistFileWriter.Format.M3U);

        Assert.True(File.Exists(outputPath));
        var content = File.ReadAllText(outputPath);
        Assert.Contains("#EXTM3U", content);
    }
}
