using Radio.Core;
using Xunit;

namespace Radio.Tests;

public sealed class RadioSessionTests
{
    private const int Second = SampleRing.Rate;
    private const string Trip = "event:/Music/Trip";

    private readonly MusicHold _hold = new();
    private readonly FakeGame _game;
    private readonly FakePage _page = new();
    private readonly FakeAudio _audio = new();
    private readonly FakeMaker _maker = new();
    private readonly FakeFiles _files = new();
    private readonly FakeDirectory _directory = new();
    private readonly List<string> _log = new();
    private RadioSettings _settings = new(Volume: 80, PlayWhileExploring: true, MusicFolder: "/music", GiveMorale: true, StreamMoraleMinutes: 3);
    private RadioSession? _session;
    private int? _keptVolume;
    private float _clock;

    public RadioSessionTests()
    {
        _game = new FakeGame(_hold);
        _directory.Answer = Answer;
        _files.Folders["/music"] = new() { "/music/a.mp3", "/music/b.mp3" };
    }

    private RadioSession Session => _session ??= NewSession();

    private string Status => _page.NowBar.Text("status");

    private string Arg => _page.NowBar.Text("arg");

    private RadioSession NewSession() => new(_game, _page, _audio, _maker, _files, new RadioBrowser(_directory.Fetch), _hold, _settings, v => _keptVolume = v, _log.Add, _ => { });

    /// <summary>Every list has the same two stations, and Radio Browser gives each a current link of its own.</summary>
    private static string Answer(Uri uri) => uri.AbsolutePath.StartsWith("/json/url/", StringComparison.Ordinal)
        ? $"{{\"ok\":true,\"url\":\"http://{uri.Segments[^1]}.example/now\"}}"
        : StationJson.Json(StationJson.Item("jazz", "Jazz FM", "http://jazz.example/live"), StationJson.Item("news", "News 24", "http://news.example/live"));

    private void Tick()
    {
        _clock += 1;
        Session.Update(_clock);
    }

    private void OpenScreen(long player = 1)
    {
        Session.OnScreenOpened(player);
        Session.Handle(new PageCommand.Show());
        Tick();
    }

    private FakePlayback PlayJazz(long player = 1)
    {
        OpenScreen(player);
        Session.Handle(new PageCommand.Play("jazz"));
        Tick();

        return _maker.Last;
    }

    private FakePlayback PlayFolder(int song = 0)
    {
        OpenScreen();
        Session.Handle(new PageCommand.Browse(Tab.Music));
        Tick();
        Session.Handle(new PageCommand.Song(song));

        return _maker.Last;
    }

    /// <summary>Sound comes in and the speakers play it, half a second at a time, then the session checks.</summary>
    private void Hear(FakePlayback playback, double seconds)
    {
        for (var left = (int)(seconds * Second); left > 0; left -= Second / 2)
        {
            var frames = Math.Min(left, Second / 2);
            playback.Arrives(frames);
            _audio.Speakers(frames);
        }

        Tick();
    }

    /// <summary>What the game's StopPlayback does: the radio hears first, then the record player goes off, and the home music plays when it was on.</summary>
    private void GameStopsPlayer(long playerId)
    {
        var wasOn = _game.PlayerOn == playerId;
        Session.OnPlayerStopping(playerId, wasOn);
        _game.SwitchOff(playerId);

        if (wasOn) _game.AskForMusic(FakeGame.HomeMusic);
    }

    private void StartTrip()
    {
        _game.TurnAllOff();
        Session.OnPlayersReset(isAtHome: false);
        _game.AskForMusic(Trip);
    }

    private void EndTrip()
    {
        _game.TurnAllOff();
        Session.OnPlayersReset(isAtHome: true);
        _game.AskForMusic(FakeGame.HomeMusic);
    }

    [Fact]
    public void AStationTakesOverTheGameMusicAndTheRecordPlayer()
    {
        var playback = PlayJazz();

        Assert.Equal("http://jazz.example/now", playback.Link?.AbsoluteUri);
        Assert.Same(playback.Ring, _audio.Ring);
        Assert.Equal("", _game.Music);
        Assert.Equal(1, _game.PlayerOn);
        Assert.True(_page.NowBar.Flag("on"));
        Assert.True(_page.NowBar.Flag("here"));
        Assert.Equal("Jazz FM", _page.NowBar.Text("name"));
        Assert.Equal("connecting", Status);
    }

    [Fact]
    public void GameMusicAskedForWhileTheRadioPlaysStartsWhenItStops()
    {
        var playback = PlayJazz();
        _game.AskForMusic(Trip);

        Assert.Equal("", _game.Music);

        Session.Handle(new PageCommand.Stop());

        Assert.Equal(Trip, _game.Music);
        Assert.Null(_game.PlayerOn);
        Assert.Null(_audio.Ring);
        Assert.True(playback.IsStopped);
        Assert.False(_page.NowBar.Flag("on"));
    }

    [Fact]
    public void StoppingWithNoOtherMusicAskedForBringsBackTheHomeMusic()
    {
        PlayJazz();

        Session.Handle(new PageCommand.Stop());

        Assert.Equal(FakeGame.HomeMusic, _game.Music);
    }

    [Fact]
    public void SwitchingStationsKeepsTheGameMusicItWasHolding()
    {
        PlayJazz();
        _game.AskForMusic(Trip);

        Session.Handle(new PageCommand.Play("news"));
        Tick();

        Assert.True(_maker.Made[0].IsStopped);
        Assert.Equal("http://news.example/now", _maker.Last.Link?.AbsoluteUri);

        Session.Handle(new PageCommand.Stop());

        Assert.Equal(Trip, _game.Music);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AStationRadioBrowserGivesNoLinkForPlaysTheLinkFromTheList(bool isRadioBrowserReachable)
    {
        OpenScreen();

        _directory.Answer = uri => !uri.AbsolutePath.StartsWith("/json/url/", StringComparison.Ordinal) ? Answer(uri)
            : isRadioBrowserReachable ? "{\"ok\":false,\"message\":\"station not found\"}"
            : throw new HttpRequestException("blocked");
        Session.Handle(new PageCommand.Play("jazz"));
        Tick();

        Assert.Equal("http://jazz.example/live", _maker.Last.Link?.AbsoluteUri);
    }

    [Fact]
    public void APastedLinkPlaysAsItIs()
    {
        OpenScreen();

        Session.Handle(new PageCommand.Link("  http://radio.example:8000/live  "));

        Assert.Equal("http://radio.example:8000/live", _maker.Last.Link?.AbsoluteUri);
        Assert.Equal("radio.example", _page.NowBar.Text("name"));
        Assert.Equal(1, _game.PlayerOn);
    }

    [Theory]
    [InlineData("radio.example/live")]
    [InlineData("ftp://radio.example/live")]
    [InlineData("hello")]
    public void TextThatIsNotAWebLinkIsTurnedAway(string text)
    {
        OpenScreen();

        Session.Handle(new PageCommand.Link(text));

        Assert.Empty(_maker.Made);
        Assert.Equal(FakeGame.HomeMusic, _game.Music);
        Assert.Equal((Texts.NotALink.En, true), Assert.Single(_page.Toasts));
    }

    [Fact]
    public void WithThePowerOutTheRadioWillNotStart()
    {
        _game.HasPower = false;
        OpenScreen();

        Session.Handle(new PageCommand.Play("jazz"));
        Tick();

        Assert.Empty(_maker.Made);
        Assert.Null(_game.PlayerOn);
        Assert.Equal(FakeGame.HomeMusic, _game.Music);
        Assert.Equal((Texts.NoPower.En, true), Assert.Single(_page.Toasts));
    }

    [Fact]
    public void WithNoSaveLoadedTheRadioWillNotStart()
    {
        _game.IsInGame = false;
        OpenScreen();

        Session.Handle(new PageCommand.Link("http://radio.example/live"));

        Assert.Empty(_maker.Made);
        Assert.Equal((Texts.NoGame.En, true), Assert.Single(_page.Toasts));
    }

    [Fact]
    public void ARecordStartingOnAnyRecordPlayerTakesOver()
    {
        const string Record = "event:/Music/Record1";
        var playback = PlayJazz();

        // What PlayTrack does: the radio hears first, then the game switches the record player on and plays the record.
        Session.OnRecordStarting();
        _game.SwitchOn(2);
        _game.AskForMusic(Record);

        Assert.False(Session.IsOn);
        Assert.True(playback.IsStopped);
        Assert.Null(_audio.Ring);
        Assert.Equal(Record, _game.Music);
        Assert.Equal(2, _game.PlayerOn);
    }

    [Fact]
    public void APowerCutStopsTheRadioAndSaysWhy()
    {
        var playback = PlayJazz();
        _game.HasPower = false;

        GameStopsPlayer(1);

        Assert.False(Session.IsOn);
        Assert.True(playback.IsStopped);
        Assert.Equal(FakeGame.HomeMusic, _game.Music);
        Assert.Equal((Texts.PowerCut.En, true), Assert.Single(_page.Toasts));
    }

    [Fact]
    public void WhenTheGameWontPlayTheSoundTheRadioStopsAndSaysWhy()
    {
        var playback = PlayJazz();

        Session.OnSoundFailed();

        Assert.False(Session.IsOn);
        Assert.True(playback.IsStopped);
        Assert.Null(_game.PlayerOn);
        Assert.Equal(FakeGame.HomeMusic, _game.Music);
        Assert.Equal((Texts.Failed.En, true), Assert.Single(_page.Toasts));
    }

    [Fact]
    public void TheRecordPlayersStopButtonStopsTheRadioWithoutAToast()
    {
        PlayJazz();

        GameStopsPlayer(1);

        Assert.False(Session.IsOn);
        Assert.Equal(FakeGame.HomeMusic, _game.Music);
        Assert.Empty(_page.Toasts);
    }

    [Fact]
    public void AnotherRecordPlayerStoppingLeavesTheRadioPlaying()
    {
        var playback = PlayJazz();

        GameStopsPlayer(2);

        Assert.True(Session.IsOn);
        Assert.False(playback.IsStopped);
        Assert.Equal(1, _game.PlayerOn);
    }

    [Fact]
    public void TheRadioKeepsPlayingOnATripWhenSetTo()
    {
        var playback = PlayJazz();

        StartTrip();

        Assert.False(playback.IsStopped);
        Assert.Same(playback.Ring, _audio.Ring);
        Assert.Equal("", _game.Music);

        EndTrip();

        Assert.Same(playback.Ring, _audio.Ring);
        Assert.Equal(1, _game.PlayerOn);
        Assert.Equal("", _game.Music);
    }

    [Fact]
    public void SetToPauseOnTripsTheGameMusicPlaysAwayAndTheRadioComesBackAtHome()
    {
        _settings = _settings with { PlayWhileExploring = false };
        var playback = PlayJazz();

        StartTrip();

        Assert.True(Session.IsOn);
        Assert.True(playback.IsStopped);
        Assert.Null(_audio.Ring);
        Assert.Equal(Trip, _game.Music);
        Assert.Equal("away", Status);

        EndTrip();
        Tick();

        Assert.Equal("http://jazz.example/now", _maker.Last.Link?.AbsoluteUri);
        Assert.Same(_maker.Last.Ring, _audio.Ring);
        Assert.Equal(1, _game.PlayerOn);
        Assert.Equal("", _game.Music);
    }

    [Fact]
    public void ChangingPlayWhileExploringOnATripAppliesThere()
    {
        var playback = PlayJazz();
        StartTrip();

        Session.UseSettings(_settings with { PlayWhileExploring = false });

        Assert.True(playback.IsStopped);
        Assert.Equal(Trip, _game.Music);

        Session.UseSettings(_settings with { PlayWhileExploring = true });
        Tick();

        Assert.Same(_maker.Last.Ring, _audio.Ring);
        Assert.Equal("", _game.Music);

        // Away from home there is no record player to switch on.
        Assert.Null(_game.PlayerOn);
    }

    [Fact]
    public void ComingHomeToNoPowerStopsTheRadio()
    {
        var playback = PlayJazz();
        StartTrip();
        _game.HasPower = false;

        EndTrip();

        Assert.False(Session.IsOn);
        Assert.True(playback.IsStopped);
        Assert.Equal(FakeGame.HomeMusic, _game.Music);
        Assert.Equal((Texts.PowerCut.En, true), Assert.Single(_page.Toasts));
    }

    [Fact]
    public void ARecordPlayerThatIsGoneStopsTheRadio()
    {
        PlayJazz();
        StartTrip();
        _game.Players.Remove(1);

        EndTrip();

        Assert.False(Session.IsOn);
        Assert.Equal(FakeGame.HomeMusic, _game.Music);
        Assert.Equal((Texts.PlayerGone.En, true), Assert.Single(_page.Toasts));
    }

    [Fact]
    public void TheRecordPlayerIsFollowedToTheNewIdTheGameGivesItOnTheWayHome()
    {
        var playback = PlayJazz(player: 1);
        StartTrip();
        _game.Players.Remove(1);
        _game.Players.Add(7);

        // The game hands over its map from old ids to new ones just before it resets the record players.
        Session.OnIdsChanged(id => id == 1 ? 7 : null);
        EndTrip();

        Assert.Same(playback.Ring, _audio.Ring);
        Assert.Equal(7, _game.PlayerOn);
        Assert.Empty(_page.Toasts);
    }

    [Fact]
    public void ListeningToAStationGivesMoraleEveryFewMinutes()
    {
        var playback = PlayJazz();

        Hear(playback, 179);
        Assert.Equal(0, _game.Morale);

        Hear(playback, 1);
        Assert.Equal(1, _game.Morale);

        Hear(playback, 180);
        Assert.Equal(2, _game.Morale);
    }

    [Fact]
    public void NoMoraleWhenTheSettingIsOff()
    {
        _settings = _settings with { GiveMorale = false };

        Hear(PlayJazz(), 240);

        Assert.Equal(0, _game.Morale);
    }

    [Fact]
    public void ListeningAwayFromHomeGivesNoMorale()
    {
        var playback = PlayJazz();
        StartTrip();

        Hear(playback, 240);

        Assert.Equal(0, _game.Morale);
    }

    [Fact]
    public void TheSongShownIsTheOneTheSpeakersReachedAndAFinishedSongGivesMorale()
    {
        var playback = PlayFolder();
        playback.Send(new PlayerEvent.SongStarts(0, 0));
        playback.Send(new PlayerEvent.SongStarts(1, 2 * Second));

        Hear(playback, 1.5);

        Assert.Equal("a", _page.NowBar.Text("name"));
        Assert.Equal(0, _game.Morale);

        Hear(playback, 0.5);

        Assert.Equal("b", _page.NowBar.Text("name"));
        Assert.Equal(1, _page.NowBar.Number("song"));
        Assert.Equal(1, _game.Morale);
    }

    [Fact]
    public void PlayingASongPlaysTheFolderFromThatSong()
    {
        var playback = PlayFolder(song: 1);

        Assert.Equal(new[] { "/music/a.mp3", "/music/b.mp3" }, playback.Paths);
        Assert.Equal(1, playback.First);
        Assert.Equal(1, _game.PlayerOn);
        Assert.Equal("folder", _page.NowBar.Text("kind"));
        Assert.Equal("b", _page.NowBar.Text("name"));
        Assert.True(_page.NowBar.Flag("canNext"));
    }

    [Fact]
    public void NextGoesOnToTheFollowingSongAndRoundToTheTop()
    {
        var first = PlayFolder(song: 1);

        Session.Handle(new PageCommand.Next());

        Assert.True(first.IsStopped);
        Assert.Equal(0, _maker.Last.First);
        Assert.Equal("a", _page.NowBar.Text("name"));
    }

    [Fact]
    public void AFolderWithOneSongHasNoNext()
    {
        _files.Folders["/music"] = new() { "/music/only.mp3" };

        PlayFolder();

        Assert.False(_page.NowBar.Flag("canNext"));
    }

    [Fact]
    public void AStationThatGivesUpStopsAndSaysWhyOnScreenAndInAToast()
    {
        var playback = PlayJazz();

        playback.Send(new PlayerEvent.Failed(Trouble.Refused, "HTTP 403"));
        Tick();

        Assert.False(Session.IsOn);
        Assert.True(playback.IsStopped);
        Assert.Null(_game.PlayerOn);
        Assert.Equal(FakeGame.HomeMusic, _game.Music);
        Assert.Equal(("refused", "HTTP 403", "bad"), (Status, Arg, _page.NowBar.Text("tone")));
        Assert.Equal(("Jazz FM: The station won't play right now (HTTP 403).", true), Assert.Single(_page.Toasts));
    }

    [Fact]
    public void AConnectionProblemKeepsItsDetailForTheLog()
    {
        var playback = PlayJazz();

        playback.Send(new PlayerEvent.Failed(Trouble.CannotConnect, "No such host is known"));
        Tick();

        Assert.Equal(("cannotConnect", ""), (Status, Arg));
        Assert.Equal("Jazz FM: Can't connect to this station.", Assert.Single(_page.Toasts).Text);
        Assert.Contains(_log, line => line.Contains("No such host is known"));
    }

    [Fact]
    public void ProblemsAreToldInTheGamesLanguage()
    {
        _page.IsChinese = true;
        var playback = PlayJazz();

        playback.Send(new PlayerEvent.Failed(Trouble.Unsupported, "AC-3"));
        Tick();

        Assert.Equal("Jazz FM：这个电台的格式是 AC-3，收音机无法播放。", Assert.Single(_page.Toasts).Text);
    }

    [Fact]
    public void TheStatusLineFollowsTheSignal()
    {
        var playback = PlayJazz();

        Assert.Equal("connecting", Status);

        playback.Send(new PlayerEvent.Playing("Mp3 44100 Hz, 2 ch"));
        playback.Arrives(Second / 4);
        Tick();

        Assert.Equal("connecting", Status);

        Hear(playback, 1);

        Assert.Equal(("playing", "good"), (Status, _page.NowBar.Text("tone")));

        // Nothing new came in, so the speakers run dry.
        _audio.Speakers(Second);
        Tick();

        Assert.Equal("buffering", Status);

        playback.Send(new PlayerEvent.Retrying(5, Trouble.CannotConnect, "the stream ended"));
        Tick();

        Assert.Equal(("retrying", "5"), (Status, Arg));

        playback.Send(new PlayerEvent.Connecting());
        Tick();

        Assert.Equal("connecting", Status);
    }

    [Fact]
    public void TheSongTitleFromTheStationShowsUntilAnotherStationStarts()
    {
        var playback = PlayJazz();

        playback.Send(new PlayerEvent.Title("Artist - Song"));
        Tick();

        Assert.Equal("Artist - Song", _page.NowBar.Text("title"));

        Session.Handle(new PageCommand.Play("news"));

        Assert.Equal("", _page.NowBar.Text("title"));
    }

    [Fact]
    public void ARecordingWithAnEndStopsOnceTheLastOfItHasPlayed()
    {
        OpenScreen();
        Session.Handle(new PageCommand.Link("http://radio.example/show.mp3"));
        var playback = _maker.Last;

        playback.Arrives(Second);
        _audio.Speakers(Second * 6 / 10);
        playback.Send(new PlayerEvent.Ended());
        Tick();

        Assert.True(Session.IsOn);

        _audio.Speakers(Second * 4 / 10);
        Tick();

        Assert.False(Session.IsOn);
        Assert.Equal("ended", Status);
        Assert.Null(_game.PlayerOn);
        Assert.Equal(FakeGame.HomeMusic, _game.Music);
    }

    [Fact]
    public void AStarredStationIsSavedAndPlaysFromFavoritesNextTime()
    {
        OpenScreen();

        Session.Handle(new PageCommand.Star("jazz", true));

        Assert.Equal(new[] { "jazz" }, _page.Stars);

        // The next time the game starts.
        _session = NewSession();
        Session.OnScreenOpened(1);
        Session.Handle(new PageCommand.Browse(Tab.Favorites));

        Assert.Equal(new[] { "jazz" }, _page.ListShown.RowIds());

        Session.Handle(new PageCommand.Play("jazz"));
        Tick();

        Assert.Equal("http://jazz.example/now", _maker.Last.Link?.AbsoluteUri);
    }

    [Fact]
    public void AFavoritesFileThatCannotBeReadIsLeftAsItWasAndSaidOnce()
    {
        _files.FavoritesFile = "[{\"id\":";
        OpenScreen();

        Session.Handle(new PageCommand.Star("jazz", true));
        Session.Handle(new PageCommand.Star("news", true));

        Assert.Equal("[{\"id\":", _files.FavoritesFile);
        Assert.Equal(new[] { "jazz", "news" }, _page.Stars);
        Assert.Equal((Texts.FavoritesUnreadable.En, true), Assert.Single(_page.Toasts));
    }

    [Fact]
    public void AStationUnstarredOnTheFavoritesTabStaysUntilTheTabOpensAgain()
    {
        _files.FavoritesFile = new Favorites(new[] { new Station("jazz", "Jazz FM", "http://jazz.example/live", "", "MP3", 128) }).ToJson();
        Session.OnScreenOpened(1);
        Session.Handle(new PageCommand.Browse(Tab.Favorites));

        Session.Handle(new PageCommand.Star("jazz", false));

        Assert.Equal(new[] { "jazz" }, _page.ListShown.RowIds());
        Assert.Empty(_page.Stars);

        Session.Handle(new PageCommand.Browse(Tab.Favorites));

        Assert.Empty(_page.ListShown.RowIds());
        Assert.Equal("noFavorites", _page.ListShown.Text("note"));
    }

    [Fact]
    public void APageThatTurnedAnUpdateAwayGetsItOnTheNextCheck()
    {
        _page.Accepts = false;
        PlayJazz();

        Assert.Null(_page.NowBar);

        _page.Accepts = true;
        Tick();

        Assert.Equal("Jazz FM", _page.NowBar.Text("name"));
        Assert.Equal(new[] { "jazz", "news" }, _page.ListShown.RowIds());
    }

    [Fact]
    public void TheScreenOnAnotherRecordPlayerShowsTheRadioIsElsewhereAndCanMoveIt()
    {
        PlayJazz(player: 1);
        Session.OnScreenClosed();

        Session.OnScreenOpened(2);

        Assert.True(_page.NowBar.Flag("on"));
        Assert.False(_page.NowBar.Flag("here"));

        Session.Handle(new PageCommand.Play("news"));
        Tick();

        Assert.Equal(2, _game.PlayerOn);
        Assert.True(_page.NowBar.Flag("here"));
    }

    [Fact]
    public void ClosingTheScreenGivesTheKeysBackToTheGame()
    {
        OpenScreen();
        Session.Handle(new PageCommand.Focus(true));

        Assert.True(_page.IsTyping);

        Session.OnScreenClosed();

        Assert.False(_page.IsTyping);
    }

    [Fact]
    public void PasteFillsTheLinkBoxWithTheClipboardTrimmed()
    {
        _files.Clipboard = "  http://radio.example/live \r\n";
        OpenScreen();

        Session.Handle(new PageCommand.Paste());

        Assert.Equal("http://radio.example/live", _page.LinkBox);

        // Pasting only fills the box; the player presses Play.
        Assert.Empty(_maker.Made);
    }

    [Fact]
    public void AHugePasteIsCutToFitTheLinkBox()
    {
        _files.Clipboard = new string('a', 5000);
        OpenScreen();

        Session.Handle(new PageCommand.Paste());

        Assert.Equal(RadioSession.MaxPasteLength, _page.LinkBox.Length);
    }

    [Fact]
    public void PastingAnEmptyClipboardSaysSo()
    {
        _files.Clipboard = " \n";
        OpenScreen();

        Session.Handle(new PageCommand.Paste());

        Assert.Equal("", _page.LinkBox);
        Assert.Equal((Texts.ClipboardEmpty.En, true), Assert.Single(_page.Toasts));
    }

    [Fact]
    public void TheVolumeStartsAtTheSettingAndTheSliderChangesAndKeepsIt()
    {
        Session.OnScreenOpened(1);

        Assert.Equal(0.8, _audio.Volume, 3);
        Assert.Equal(80, _page.NowBar.Number("volume"));

        Session.Handle(new PageCommand.Volume(35));

        Assert.Equal(0.35, _audio.Volume, 3);
        Assert.Equal(35, _keptVolume);
        Assert.Equal(35, _page.NowBar.Number("volume"));
    }

    [Fact]
    public void TheMusicTabSaysItIsLookingThenListsTheSongs()
    {
        _files.SlowScan = new TaskCompletionSource<MusicList>();
        Session.OnScreenOpened(1);

        Session.Handle(new PageCommand.Browse(Tab.Music));

        Assert.Equal(("scanning", "/music"), (_page.ListShown.Text("note"), _page.ListShown.Text("folder")));
        Assert.True(_page.ListShown.Flag("loading"));

        _files.SlowScan.SetResult(MusicFolder.Scan(new[] { "/music/b.mp3", "/music/a.mp3", "/music/cover.jpg" }, "/music"));
        Tick();

        Assert.Equal(("songsIn", "2"), (_page.ListShown.Text("note"), _page.ListShown.Text("noteArg")));
        Assert.Equal(new[] { "a", "b" }, _page.ListShown.RowIds());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFolderWithNoSongsSaysWhichFolderToPutThemIn(bool isFolderThere)
    {
        if (isFolderThere) _files.Folders["/music"] = new() { "/music/cover.jpg" };
        else _files.Folders.Remove("/music");

        Session.OnScreenOpened(1);
        Session.Handle(new PageCommand.Browse(Tab.Music));
        Tick();

        Assert.Equal(("noSongsIn", "/music"), (_page.ListShown.Text("note"), _page.ListShown.Text("noteArg")));
        Assert.Empty(_page.ListShown.RowIds());
    }

    [Fact]
    public void ScanAgainFindsSongsAddedSince()
    {
        Session.OnScreenOpened(1);
        Session.Handle(new PageCommand.Browse(Tab.Music));
        Tick();
        _files.Folders["/music"].Add("/music/c.mp3");

        Session.Handle(new PageCommand.Scan());
        Tick();

        Assert.Equal("3", _page.ListShown.Text("noteArg"));
    }

    [Fact]
    public void ChangingTheMusicFolderLooksInTheNewOne()
    {
        _files.Folders["/other"] = new() { "/other/x.mp3" };
        Session.OnScreenOpened(1);
        Session.Handle(new PageCommand.Browse(Tab.Music));
        Tick();

        Session.UseSettings(_settings with { MusicFolder = "/other" });

        Assert.Equal(("scanning", "/other"), (_page.ListShown.Text("note"), _page.ListShown.Text("folder")));

        Tick();

        Assert.Equal(new[] { "x" }, _page.ListShown.RowIds());
    }

    [Fact]
    public void AMusicFolderThatWillNotOpenSaysWhy()
    {
        _files.OpenProblem = "access denied";
        OpenScreen();

        Session.Handle(new PageCommand.OpenFolder());

        Assert.Equal(("Couldn't open the music folder: access denied", true), Assert.Single(_page.Toasts));
    }

    [Theory]
    [InlineData(false, "popular")]
    [InlineData(true, "chinese")]
    public void ChinesePlayersStartOnTheChineseStations(bool isChinese, string tab)
    {
        _page.IsChinese = isChinese;

        OpenScreen();

        Assert.Equal(tab, _page.ListShown.Text("tab"));
        Assert.Equal(new[] { "jazz", "news" }, _page.ListShown.RowIds());
    }

    [Fact]
    public void ABlankSearchAsksForANameFirst()
    {
        OpenScreen();

        Session.Handle(new PageCommand.Search("   "));

        Assert.Equal(("search", "typeToSearch"), (_page.ListShown.Text("tab"), _page.ListShown.Text("note")));
        Assert.Empty(_page.ListShown.RowIds());
    }

    [Fact]
    public void ASearchWithNoMatchesSaysNoneWereFound()
    {
        OpenScreen();
        _directory.Answer = _ => "[]";

        Session.Handle(new PageCommand.Search("zzzz"));
        Tick();

        Assert.Equal("noStations", _page.ListShown.Text("note"));
    }

    [Fact]
    public void AListThatCannotLoadSaysSo()
    {
        _directory.Answer = _ => throw new HttpRequestException("offline");

        OpenScreen();

        Assert.Equal("listFailed", _page.ListShown.Text("note"));
        Assert.False(_page.ListShown.Flag("loading"));
    }

    [Fact]
    public void BackAtTheMenuTheRadioStopsAndTheMenuMusicPlays()
    {
        const string Menu = "event:/Music/Menu";
        PlayJazz();
        _game.AskForMusic(Menu);

        Session.OnLeftGame();

        Assert.False(Session.IsOn);
        Assert.Null(_audio.Ring);
        Assert.Equal(Menu, _game.Music);
    }

    [Fact]
    public void BackAtTheMenuTheHomeMusicStaysOff()
    {
        PlayJazz();

        Session.OnLeftGame();

        Assert.Equal("", _game.Music);
    }
}
