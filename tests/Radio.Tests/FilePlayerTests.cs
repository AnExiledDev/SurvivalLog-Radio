using Radio.Core;
using Xunit;

namespace Radio.Tests;

public sealed class FilePlayerTests : IDisposable
{
    private const int Block = 4800;

    private readonly Dictionary<string, Func<IFileDecoder>> _songs = new();
    private FilePlayer? _player;

    public void Dispose() => _player?.Stop();

    private FilePlayer Play(int first, params string[] files)
    {
        _player = new FilePlayer(files, first, path => _songs[path]());
        _player.Start();

        return _player;
    }

    private void Song(string name, int blocks) => _songs[name] = () => new FakeFileDecoder(blocks, Block);

    private static List<PlayerEvent> UntilStarts(IPlayback player, int count)
    {
        var seen = 0;

        return Events.Until(player, e => e is PlayerEvent.SongStarts && ++seen == count);
    }

    [Fact]
    public void EachSongSaysWhereItsSoundStartsAndTheFolderGoesRound()
    {
        Song("a.mp3", 2);
        Song("b.mp3", 1);

        var events = UntilStarts(Play(0, "a.mp3", "b.mp3"), 3);

        Assert.Equal(new PlayerEvent[] { new PlayerEvent.SongStarts(0, 0), new PlayerEvent.SongStarts(1, 2 * Block), new PlayerEvent.SongStarts(0, 3 * Block) }, events);
    }

    [Fact]
    public void PlayStartsAtTheChosenSong()
    {
        Song("a.mp3", 1);
        Song("b.mp3", 1);

        var events = UntilStarts(Play(1, "a.mp3", "b.mp3"), 2);

        Assert.Equal(new PlayerEvent[] { new PlayerEvent.SongStarts(1, 0), new PlayerEvent.SongStarts(0, Block) }, events);
    }

    [Fact]
    public void AChosenSongPastTheEndOfTheFolderStartsAtTheTop()
    {
        Song("a.mp3", 1);

        var events = UntilStarts(Play(5, "a.mp3"), 1);

        Assert.Equal(new PlayerEvent.SongStarts(0, 0), events[^1]);
    }

    [Fact]
    public void ASongInAFormatThatCannotBeReadIsSkipped()
    {
        _songs["a.ogg"] = () => throw new UnsupportedAudio("Ogg");
        Song("b.mp3", 1);

        var events = UntilStarts(Play(0, "a.ogg", "b.mp3"), 1);

        Assert.Equal(new PlayerEvent[] { new PlayerEvent.SongSkipped(0, "Ogg"), new PlayerEvent.SongStarts(1, 0) }, events);
    }

    [Fact]
    public void ASongWithNoSoundIsSkipped()
    {
        Song("empty.mp3", 0);
        Song("b.mp3", 1);

        var events = UntilStarts(Play(0, "empty.mp3", "b.mp3"), 2);

        Assert.Contains(new PlayerEvent.SongSkipped(0, "InvalidDataException: the song has no sound"), events);
        Assert.Equal(new PlayerEvent.SongStarts(1, 0), events[^1]);
    }

    [Fact]
    public void AFolderWhereNoSongPlaysGivesUp()
    {
        _songs["a.ogg"] = () => throw new UnsupportedAudio("Ogg");
        _songs["b.wma"] = () => throw new IOException("the file is in use");

        var events = Events.Until(Play(0, "a.ogg", "b.wma"), e => e is PlayerEvent.Failed);

        Assert.Equal(new PlayerEvent[]
        {
            new PlayerEvent.SongSkipped(0, "Ogg"),
            new PlayerEvent.SongSkipped(1, "IOException: the file is in use"),
            new PlayerEvent.Failed(Trouble.NoSongs, "none of the songs could be read"),
        }, events);
    }

    [Fact]
    public void OneBadSongInAFolderOfGoodOnesNeverStopsPlay()
    {
        _songs["a.ogg"] = () => throw new UnsupportedAudio("Ogg");
        Song("b.mp3", 1);

        var events = UntilStarts(Play(0, "a.ogg", "b.mp3"), 4);

        Assert.DoesNotContain(events, e => e is PlayerEvent.Failed);
    }

    [Fact]
    public void AnEmptyFolderSaysSo()
    {
        var events = Events.Until(Play(0), e => e is PlayerEvent.Failed);

        Assert.Equal(new PlayerEvent.Failed(Trouble.NoSongs, "the folder is empty"), events[^1]);
    }
}
