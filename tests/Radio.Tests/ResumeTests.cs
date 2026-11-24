using Radio.Core;
using Xunit;

namespace Radio.Tests;

/// <summary>Loading a save plays what was playing when the player last left it, on the same record player.</summary>
public sealed class ResumeTests
{
    private const string Save = "Save01";

    private readonly MusicHold _hold = new();
    private readonly FakeGame _game;
    private readonly FakePage _page = new();
    private readonly FakeAudio _audio = new();
    private readonly FakeMaker _maker = new();
    private readonly FakeFiles _files = new();
    private readonly FakeDirectory _directory = new();
    private RadioSettings _settings = new(Volume: 80, PlayWhileExploring: true, MusicFolder: "/music", GiveMorale: true, StreamMoraleMinutes: 3);
    private RadioSession _session;
    private float _clock;

    public ResumeTests()
    {
        _game = new FakeGame(_hold);
        _directory.Answer = uri => uri.AbsolutePath.StartsWith("/json/url/", StringComparison.Ordinal)
            ? $"{{\"ok\":true,\"url\":\"http://{uri.Segments[^1]}.example/now\"}}"
            : StationJson.Json(StationJson.Item("jazz", "Jazz FM", "http://jazz.example/live"), StationJson.Item("news", "News 24", "http://news.example/live"));
        _files.Folders["/music"] = new() { "/music/a.mp3", "/music/b.mp3", "/music/c.mp3" };
        _session = NewSession();
    }

    private RadioSession NewSession() => new(_game, _page, _audio, _maker, _files, new RadioBrowser(_directory.Fetch), _hold, _settings, _ => { }, _ => { }, _ => { });

    private void Tick()
    {
        _clock += 1;
        _session.Update(_clock);
    }

    /// <summary>What the game does on a load at home: new ids for every agent, then every record player off.</summary>
    private void LoadAtHome(string save, Func<long, long?> newIdOf)
    {
        _game.SaveKey = save;
        _game.TurnAllOff();
        _session.OnIdsChanged(newIdOf);
        _session.OnPlayersReset(isAtHome: true);
        _game.AskForMusic(FakeGame.HomeMusic);
        Tick();
    }

    /// <summary>A save made on a trip loads on the trip. The game gives no new ids for the home there.</summary>
    private void LoadOnTrip(string save)
    {
        _game.SaveKey = save;
        _game.TurnAllOff();
        _session.OnPlayersReset(isAtHome: false);
        Tick();
    }

    private static long? Same(long id) => id;

    private void PlayJazz(long player)
    {
        _session.OnScreenOpened(player);
        _session.Handle(new PageCommand.Show());
        Tick();
        _session.Handle(new PageCommand.Play("jazz"));
        Tick();
    }

    private void PlaySong(long player, int song)
    {
        _session.OnScreenOpened(player);
        _session.Handle(new PageCommand.Browse(Tab.Music));
        Tick();
        _session.Handle(new PageCommand.Song(song));
    }

    private void QuitToMenu()
    {
        _session.OnScreenClosed();
        _session.OnLeftGame();
    }

    [Fact]
    public void QuittingToTheMenuAndLoadingTheSameSavePlaysTheStationAgainOnTheRecordPlayer()
    {
        LoadAtHome(Save, Same);
        PlayJazz(player: 2);
        QuitToMenu();
        Assert.False(_session.IsOn);
        _game.Players.Add(12);

        LoadAtHome(Save, id => id == 2 ? 12 : null);

        Assert.True(_session.IsOn);
        Assert.Equal("http://jazz.example/now", _maker.Last.Link?.AbsoluteUri);
        Assert.Same(_maker.Last.Ring, _audio.Ring);
        Assert.Equal(12, _game.PlayerOn);
        Assert.Equal("", _game.Music);
        Assert.Empty(_page.Toasts);
    }

    [Fact]
    public void AfterTheGameRestartsTheSaveStillPicksUpItsStation()
    {
        LoadAtHome(Save, Same);
        PlayJazz(player: 1);
        _session.Shutdown();
        _session = NewSession();

        LoadAtHome(Save, Same);

        Assert.True(_session.IsOn);
        Assert.Equal("http://jazz.example/now", _maker.Last.Link?.AbsoluteUri);
        Assert.Equal(1, _game.PlayerOn);
    }

    [Fact]
    public void ARadioStoppedBeforeLeavingStaysOffAfterTheLoad()
    {
        LoadAtHome(Save, Same);
        PlayJazz(player: 1);
        QuitToMenu();
        LoadAtHome(Save, Same);
        _session.Handle(new PageCommand.Stop());
        QuitToMenu();
        var made = _maker.Made.Count;

        LoadAtHome(Save, Same);

        Assert.False(_session.IsOn);
        Assert.Equal(made, _maker.Made.Count);
        Assert.Null(_game.PlayerOn);
        Assert.Equal(FakeGame.HomeMusic, _game.Music);
    }

    [Fact]
    public void AnotherSaveDoesNotGetTheStationOfThisOne()
    {
        LoadAtHome(Save, Same);
        PlayJazz(player: 1);
        QuitToMenu();

        LoadAtHome("Save02", Same);

        Assert.False(_session.IsOn);
        Assert.Null(_game.PlayerOn);
    }

    [Fact]
    public void WithResumeOnLoadOffTheRadioIsOffAfterTheLoad()
    {
        _settings = _settings with { ResumeOnLoad = false };
        _session = NewSession();
        LoadAtHome(Save, Same);
        PlayJazz(player: 1);
        QuitToMenu();

        LoadAtHome(Save, Same);

        Assert.False(_session.IsOn);
        Assert.Null(_game.PlayerOn);
    }

    [Fact]
    public void ARecordPlayerThatIsGoneLeavesTheRadioOffWithoutAFuss()
    {
        LoadAtHome(Save, Same);
        PlayJazz(player: 1);
        QuitToMenu();

        LoadAtHome(Save, _ => null);

        Assert.False(_session.IsOn);
        Assert.Null(_game.PlayerOn);
        Assert.Empty(_page.Toasts);
    }

    [Fact]
    public void WithNoPowerTheRadioStaysOff()
    {
        LoadAtHome(Save, Same);
        PlayJazz(player: 1);
        QuitToMenu();
        _game.HasPower = false;

        LoadAtHome(Save, Same);

        Assert.False(_session.IsOn);
        Assert.Null(_game.PlayerOn);
    }

    [Fact]
    public void ASongFromTheMusicFolderPicksUpAtThatSongAfterARestart()
    {
        LoadAtHome(Save, Same);
        PlaySong(player: 1, song: 1);
        _session.Shutdown();
        _session = NewSession();

        LoadAtHome(Save, Same);

        Assert.True(_session.IsOn);
        Assert.Equal(new[] { "/music/a.mp3", "/music/b.mp3", "/music/c.mp3" }, _maker.Last.Paths);
        Assert.Equal(1, _maker.Last.First);
        Assert.Equal(1, _game.PlayerOn);
    }

    [Fact]
    public void ASongThatLeftTheFolderLeavesTheRadioOff()
    {
        LoadAtHome(Save, Same);
        PlaySong(player: 1, song: 1);
        _session.Shutdown();
        _files.Folders["/music"].Remove("/music/b.mp3");
        _session = NewSession();

        LoadAtHome(Save, Same);

        Assert.False(_session.IsOn);
        Assert.Null(_game.PlayerOn);
    }

    [Fact]
    public void LeavingBeforeTheFolderScanFinishesKeepsTheSongForNextTime()
    {
        LoadAtHome(Save, Same);
        PlaySong(player: 1, song: 2);
        _session.Shutdown();
        _files.SlowScan = new TaskCompletionSource<MusicList>();
        _session = NewSession();
        LoadAtHome(Save, Same);
        _session.Shutdown();
        _files.SlowScan = null;
        _session = NewSession();

        LoadAtHome(Save, Same);

        Assert.True(_session.IsOn);
        Assert.Equal(2, _maker.Last.First);
    }

    [Fact]
    public void ASaveLoadedOnATripWaitsAndPlaysOnceHomeWhenSetToPauseOnTrips()
    {
        _settings = _settings with { PlayWhileExploring = false };
        _session = NewSession();
        LoadAtHome(Save, Same);
        PlayJazz(player: 1);
        QuitToMenu();
        var made = _maker.Made.Count;

        LoadOnTrip(Save);

        Assert.True(_session.IsOn);
        Assert.Equal(made, _maker.Made.Count);
        Assert.Null(_audio.Ring);

        _game.Players.Add(9);
        LoadAtHome(Save, id => id == 1 ? 9 : null);

        Assert.Equal("http://jazz.example/now", _maker.Last.Link?.AbsoluteUri);
        Assert.Equal(9, _game.PlayerOn);
    }

    [Fact]
    public void ASaveLoadedOnATripPlaysThereWhenSetToKeepPlaying()
    {
        LoadAtHome(Save, Same);
        PlayJazz(player: 1);
        QuitToMenu();

        LoadOnTrip(Save);

        Assert.True(_session.IsOn);
        Assert.Equal("http://jazz.example/now", _maker.Last.Link?.AbsoluteUri);
        Assert.Null(_game.PlayerOn);
    }

    [Fact]
    public void ComingHomeFromATripDoesNotBringBackAStationStoppedThere()
    {
        LoadAtHome(Save, Same);
        PlayJazz(player: 1);
        QuitToMenu();
        LoadAtHome(Save, Same);
        _session.Handle(new PageCommand.Stop());

        LoadOnTrip(Save);
        LoadAtHome(Save, Same);

        Assert.False(_session.IsOn);
        Assert.Null(_game.PlayerOn);
    }

    [Fact]
    public void PickingSomethingElseWhileTheScanRunsWins()
    {
        LoadAtHome(Save, Same);
        PlaySong(player: 1, song: 1);
        _session.Shutdown();
        _files.SlowScan = new TaskCompletionSource<MusicList>();
        _session = NewSession();
        LoadAtHome(Save, Same);
        var made = _maker.Made.Count;
        PlayJazz(player: 1);
        _files.SlowScan.SetResult(MusicFolder.Scan(_files.Folders["/music"], "/music"));
        Tick();

        Assert.Equal("http://jazz.example/now", Assert.Single(_maker.Made.Skip(made)).Link?.AbsoluteUri);
        Assert.Same(_maker.Last.Ring, _audio.Ring);
    }
}

public sealed class ResumeListTests
{
    private static readonly Station Jazz = new("jazz", "Jazz FM", "http://jazz.example/live", "Germany", "MP3", 128);

    [Fact]
    public void PointsComeBackFromTheFileAsTheyWent()
    {
        var list = ResumeList.Empty();
        list.Set("Save01", new ResumePoint(4294967301, Jazz, ""));
        list.Set("Save02", new ResumePoint(7, null, "C:\\Music\\a.mp3"));

        var back = ResumeList.Parse(list.ToJson());

        var station = back.Get("Save01");
        Assert.Equal(4294967301, station?.PlayerId);
        Assert.Equal(("jazz", "Jazz FM", "http://jazz.example/live", 128), (station?.Station?.Id, station?.Station?.Name, station?.Station?.Url, station?.Station?.Bitrate));
        var song = back.Get("Save02");
        Assert.Equal((7L, (Station?)null, "C:\\Music\\a.mp3"), (song?.PlayerId ?? 0, song?.Station, song?.Song));
        Assert.Null(back.Get("Save03"));
    }

    [Fact]
    public void AFileThatCannotBeReadGivesNoPoints()
    {
        Assert.Null(ResumeList.Parse("{not json").Get("Save01"));
        Assert.Null(ResumeList.Parse("{\"save\":\"Save01\"}").Get("Save01"));
        Assert.Null(ResumeList.Parse("[{\"save\":\"Save01\",\"player\":1}]").Get("Save01"));
        Assert.Null(ResumeList.Parse(null).Get("Save01"));
    }

    [Fact]
    public void TheOldestSaveIsDroppedPastTheLimit()
    {
        var list = ResumeList.Empty();

        for (var i = 0; i <= ResumeList.Max; i++) list.Set("Save" + i, new ResumePoint(i, Jazz, ""));

        Assert.Null(list.Get("Save0"));
        Assert.NotNull(list.Get("Save1"));
        Assert.NotNull(list.Get("Save" + ResumeList.Max));
    }

    [Fact]
    public void SettingASaveAgainMakesItTheNewest()
    {
        var list = ResumeList.Empty();
        list.Set("Save0", new ResumePoint(0, Jazz, ""));

        for (var i = 1; i < ResumeList.Max; i++) list.Set("Save" + i, new ResumePoint(i, Jazz, ""));

        list.Set("Save0", new ResumePoint(5, Jazz, ""));
        list.Set("SaveNew", new ResumePoint(0, Jazz, ""));

        Assert.Equal(5, list.Get("Save0")?.PlayerId);
        Assert.Null(list.Get("Save1"));
    }
}
