using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ExiledToolkit.Kit;

namespace Radio.Core;

/// <summary>
/// The radio: what plays, on which record player, and what the Radio tab shows. Everything here runs on the game's
/// main thread. Station lists, link lookups and folder scans run as tasks, and the readers run on threads of their
/// own; the session picks up what they finished each time it checks.
/// </summary>
public sealed class RadioSession
{
    public const long NoPlayer = long.MinValue;
    public const int MaxPasteLength = 2048;

    /// <summary>
    /// How often it checks the readers and tasks while any are running. A reader thread can't call into the game, so
    /// the game thread has to look; four times a second is plenty for a status line and a song title, and a check
    /// that finds nothing costs almost nothing.
    /// </summary>
    private const float CheckSeconds = 0.25f;

    /// <summary>Radio Browser gets this long to give a station's current link before the saved one is used.</summary>
    private static readonly TimeSpan ResolveTime = TimeSpan.FromSeconds(4);

    private static readonly StatusLine Connecting = new("connecting", "", Tone.Waiting);
    private static readonly StatusLine Playing = new("playing", "", Tone.Good);
    private static readonly StatusLine Buffering = new("buffering", "", Tone.Waiting);
    private static readonly StatusLine Away = new("away", "", Tone.Waiting);
    private static readonly StatusLine Ended = new("ended", "", Tone.None);

    private readonly IRadioGame _game;
    private readonly IRadioPage _page;
    private readonly IAudioOut _audio;
    private readonly IPlaybackMaker _maker;
    private readonly IRadioFiles _files;
    private readonly RadioBrowser _browser;
    private readonly MusicHold _hold;
    private readonly Action<int> _keepVolume;
    private readonly Action<string> _log;
    private readonly Action<string> _detail;
    private readonly MoraleClock _morale = new();
    private readonly Queue<PlayerEvent.SongStarts> _songStarts = new();
    private readonly Favorites _favorites;
    private readonly bool _canSaveFavorites;
    private readonly ResumeList _resumes;

    private RadioSettings _settings;
    private float _nextCheckAt;

    private Source? _source;
    private IPlayback? _playback;
    private long _playerId = NoPlayer;
    private bool _isSuspended;
    private bool _isSwitchedOn;
    private bool _isAtHome = true;
    private Task<Uri?>? _resolving;
    private Station? _resolvingStation;
    private CancellationTokenSource? _resolveCancel;
    private bool _hasSound;
    private bool _isEnding;
    private bool _hasSongStarted;
    private int _song = -1;
    private string _title = "";
    private StatusLine _status = StatusLine.None;

    /// <summary>The save being played, known from its first load on. Null at the main menu.</summary>
    private string? _saveKey;

    /// <summary>The new ids of the load going on, from just before the game resets the record players.</summary>
    private Func<long, long?>? _loadIds;

    /// <summary>A song to pick up once the music folder is scanned, with the record player's id in this load.</summary>
    private ResumePoint? _waitingSong;

    private bool _isScreenOpen;
    private long _screenPlayerId = NoPlayer;
    private bool _hasList;
    private ListView _list = new(Tab.Popular, false, "", "", "", "", ListView.NoRows);
    private IReadOnlyList<Station> _rows = Array.Empty<Station>();
    private Task<LoadedRows>? _listTask;
    private CancellationTokenSource? _listCancel;
    private MusicList? _music;
    private Task<MusicList>? _scanTask;
    private CancellationTokenSource? _scanCancel;
    private int _favoritesVersion;
    private bool _hasWarnedFavorites;

    private NowView? _sentNow;
    private ListView? _sentList;
    private int _sentFavoritesVersion = -1;

    /// <param name="hold">The same hold the music hooks ask, so the game music stays off while the radio plays.</param>
    /// <param name="keepVolume">Saves the volume the player set on the page.</param>
    /// <param name="log">A line for the BepInEx log.</param>
    /// <param name="detail">A line for the log only when the player turned on the extra logging.</param>
    public RadioSession(
        IRadioGame game,
        IRadioPage page,
        IAudioOut audio,
        IPlaybackMaker maker,
        IRadioFiles files,
        RadioBrowser browser,
        MusicHold hold,
        RadioSettings settings,
        Action<int> keepVolume,
        Action<string> log,
        Action<string> detail)
    {
        _game = game;
        _page = page;
        _audio = audio;
        _maker = maker;
        _files = files;
        _browser = browser;
        _hold = hold;
        _settings = settings;
        _keepVolume = keepVolume;
        _log = log;
        _detail = detail;
        _favorites = Favorites.Parse(files.LoadFavorites(), out _canSaveFavorites);
        _resumes = ResumeList.Parse(files.LoadResume());

        if (!_canSaveFavorites) log("The favorites file couldn't be read. It is left as it is, and stars won't be saved until it is fixed or deleted.");

        audio.SetVolume(VolumeOf(settings.Volume));
    }

    public bool IsOn => _source != null;

    private bool IsBusy => _playback != null || _resolving != null || _listTask != null || _scanTask != null;

    /// <summary>Morale comes from a record player that is on, and one is only ever on at home.</summary>
    private bool IsCountingMorale => _settings.GiveMorale && _isSwitchedOn;

    /// <summary>A setting changed. Every one of them applies at once.</summary>
    public void UseSettings(RadioSettings settings)
    {
        var old = _settings;
        _settings = settings;

        if (old.Volume != settings.Volume) _audio.SetVolume(VolumeOf(settings.Volume));

        if (old.PlayWhileExploring != settings.PlayWhileExploring) FollowExploringSetting();

        if (old.MusicFolder != settings.MusicFolder) ForgetMusic();

        Send();
    }

    public void Handle(PageCommand command)
    {
        switch (command)
        {
            case PageCommand.Ready:
                _page.ForgetWords();
                ForgetSent();
                break;
            case PageCommand.NoScript:
                _page.Install();
                break;
            case PageCommand.Note note:
                _detail("page: " + note.Text);
                break;
            case PageCommand.Focus focus:
                _page.SetTyping(focus.IsTyping);
                break;
            case PageCommand.Show:
                ForgetSent();

                if (!_hasList) Browse(_page.IsChinese ? Tab.Chinese : Tab.Popular, "");

                break;
            case PageCommand.Search search:
                Browse(Tab.Search, search.Query);
                break;
            case PageCommand.Browse browse:
                Browse(browse.Tab, "");
                break;
            case PageCommand.Play play:
                PlayStation(play.StationId);
                break;
            case PageCommand.Link link:
                PlayLink(link.Text);
                break;
            case PageCommand.Star star:
                SetFavorite(star.StationId, star.IsFavorite);
                break;
            case PageCommand.Song song:
                PlaySong(song.Index);
                break;
            case PageCommand.Next:
                PlayNextSong();
                break;
            case PageCommand.Stop:
                StopRadio(switchOff: true, restoreMusic: true);
                _status = StatusLine.None;
                break;
            case PageCommand.Volume volume:
                SetVolume(volume.Percent);
                break;
            case PageCommand.OpenFolder:
                OpenFolder();
                break;
            case PageCommand.Scan:
                CancelScan();
                _music = null;
                Browse(Tab.Music, "");
                break;
            case PageCommand.Paste:
                Paste();
                break;
        }

        Send();
    }

    /// <param name="now">Seconds on any clock that only goes forward.</param>
    public void Update(float now)
    {
        if (!IsBusy || now < _nextCheckAt) return;

        _nextCheckAt = now + CheckSeconds;
        Check();
    }

    /// <summary>The music screen opened on this record player.</summary>
    public void OnScreenOpened(long playerId)
    {
        _isScreenOpen = true;
        _screenPlayerId = playerId;
        ForgetSent();
        Send();
    }

    public void OnScreenClosed()
    {
        _isScreenOpen = false;
        _screenPlayerId = NoPlayer;

        // A closed screen takes its text boxes with it, and may not say that typing ended.
        _page.SetTyping(false);
    }

    /// <summary>A record is about to play, on any record player. The record wins, and its music must not be held.</summary>
    public void OnRecordStarting()
    {
        if (_source == null) return;

        StopRadio(switchOff: false, restoreMusic: false);
        _status = StatusLine.None;
        Send();
    }

    /// <summary>The game is turning a record player off: a power cut, or its stop button.</summary>
    /// <param name="wasOn">It was on, so the game plays the home music itself once it is off.</param>
    public void OnPlayerStopping(long playerId, bool wasOn)
    {
        if (_source == null || playerId != _playerId) return;

        StopRadio(switchOff: false, restoreMusic: !wasOn);
        _status = StatusLine.None;

        if (_game.IsPowerOff()) Toast(Texts.PowerCut, isProblem: true);

        Send();
    }

    /// <summary>The game gave every agent a new id, as it does on every load and trip, just before it resets the record players.</summary>
    /// <param name="newIdOf">The new id for an old one, or null when that agent isn't in the map being loaded.</param>
    public void OnIdsChanged(Func<long, long?> newIdOf)
    {
        _loadIds = newIdOf;

        if (_playerId != NoPlayer && newIdOf(_playerId) is { } playerId) _playerId = playerId;

        if (_screenPlayerId != NoPlayer && newIdOf(_screenPlayerId) is { } screenPlayerId) _screenPlayerId = screenPlayerId;
    }

    /// <summary>A save loaded or a trip started or ended. The game turns every record player off when that happens.</summary>
    public void OnPlayersReset(bool isAtHome)
    {
        var loadIds = _loadIds;
        _loadIds = null;
        _isAtHome = isAtHome;
        _isSwitchedOn = false;

        if (PickUpSave(loadIds))
        {
            Send();
            return;
        }

        if (_source == null) return;

        if (!isAtHome)
        {
            if (!_settings.PlayWhileExploring && !_isSuspended) Suspend();
        }
        else if (_isSuspended)
        {
            Resume();
        }
        else if (_game.IsPowerOff())
        {
            StopRadio(switchOff: false, restoreMusic: true);
            _status = StatusLine.None;
            Toast(Texts.PowerCut, isProblem: true);
        }
        else
        {
            SwitchOn();
        }

        Send();
    }

    /// <summary>Back at the main menu. The home music stays off there; anything else the game asked for plays.</summary>
    public void OnLeftGame()
    {
        KeepForNextLoad();
        _saveKey = null;
        _loadIds = null;
        _waitingSong = null;

        if (_source != null) StopRadio(switchOff: false, restoreMusic: _hold.Held is not HeldMusic.Home);

        _status = StatusLine.None;
        _isAtHome = true;
        OnScreenClosed();
    }

    /// <summary>The game's music wouldn't take the radio's sound. The radio stops, so the player isn't left listening to nothing.</summary>
    public void OnSoundFailed()
    {
        if (_source != null) StopRadio(switchOff: true, restoreMusic: true);

        _status = StatusLine.None;
        Toast(Texts.Failed, isProblem: true);
        Send();
    }

    /// <summary>The game is closing.</summary>
    public void Shutdown()
    {
        KeepForNextLoad();
        StopRadio(switchOff: false, restoreMusic: false);
        CancelList();
        CancelScan();
    }

    private void Check()
    {
        FinishResolve();
        FinishList();
        FinishScan();
        TakePlayerEvents();
        FollowSongs();
        CountStreamMorale();
        FinishEnding();
        FollowSignal();
        Send();
    }

    private void Browse(Tab tab, string query)
    {
        _hasList = true;

        switch (tab)
        {
            case Tab.Favorites:
                ShowFavorites();
                break;
            case Tab.Music:
                ShowMusic();
                break;
            default:
                LoadStations(tab, query);
                break;
        }
    }

    private void LoadStations(Tab tab, string query)
    {
        CancelList();
        _rows = Array.Empty<Station>();

        if (tab == Tab.Search && query.Trim().Length == 0)
        {
            _list = new ListView(tab, false, query, "typeToSearch", "", "", ListView.NoRows);
            return;
        }

        _list = new ListView(tab, true, query, "loading", "", "", ListView.NoRows);
        _listCancel = new CancellationTokenSource();
        _listTask = LoadRows(ListOf(tab), query, _listCancel.Token);
    }

    /// <summary>The rows' JSON is built where the list arrives, on a worker, so a long list costs the game thread nothing.</summary>
    private async Task<LoadedRows> LoadRows(StationList list, string query, CancellationToken cancel)
    {
        var stations = await _browser.List(list, query, cancel).ConfigureAwait(false);

        return new LoadedRows(stations, RadioView.StationRows(stations));
    }

    private void FinishList()
    {
        var task = _listTask;

        if (task == null || !task.IsCompleted) return;

        _listTask = null;
        _listCancel = null;

        if (task.IsCompletedSuccessfully)
        {
            var loaded = task.Result;
            _rows = loaded.Stations;
            _list = _list with { IsLoading = false, Note = loaded.Stations.Count == 0 ? "noStations" : "", RowsJson = loaded.Json };
            return;
        }

        _log("Couldn't load the station list: " + ProblemOf(task));
        _list = _list with { IsLoading = false, Note = "listFailed" };
    }

    /// <summary>A snapshot: a station unstarred here stays on the list until the tab opens again, so a misclick is easy to undo.</summary>
    private void ShowFavorites()
    {
        CancelList();
        _rows = new List<Station>(_favorites.All);
        _list = new ListView(Tab.Favorites, false, "", _rows.Count == 0 ? "noFavorites" : "", "", "", RadioView.StationRows(_rows));
    }

    private void ShowMusic()
    {
        CancelList();
        _rows = Array.Empty<Station>();

        if (_music != null)
        {
            _list = MusicView(_music);
            return;
        }

        _list = new ListView(Tab.Music, true, "", "scanning", "", _settings.MusicFolder, ListView.NoRows);

        if (_scanTask != null) return;

        _scanCancel = new CancellationTokenSource();
        _scanTask = _files.ScanMusic(_settings.MusicFolder, _scanCancel.Token);
    }

    private void FinishScan()
    {
        var task = _scanTask;

        if (task == null || !task.IsCompleted) return;

        _scanTask = null;
        _scanCancel = null;

        if (task.IsCompletedSuccessfully)
        {
            _music = task.Result;
        }
        else
        {
            _log("Couldn't read the music folder " + _settings.MusicFolder + ": " + ProblemOf(task));
            _music = new MusicList(_settings.MusicFolder, Array.Empty<string>(), Array.Empty<string>(), ListView.NoRows);
        }

        if (_list.Tab == Tab.Music) _list = MusicView(_music);

        PlayWaitingSong();
    }

    /// <summary>The music folder setting changed, so the songs found before are from the wrong folder.</summary>
    private void ForgetMusic()
    {
        CancelScan();
        _music = null;
        _waitingSong = null;

        if (_hasList && _list.Tab == Tab.Music) ShowMusic();
    }

    private void PlayStation(string id)
    {
        var station = FindStation(id);

        if (station == null)
        {
            _log("The page asked for a station that isn't listed: " + id);
            return;
        }

        Start(new Source.OfStation(station), _screenPlayerId);
    }

    private void PlayLink(string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var link) || !IsWebLink(link))
        {
            Toast(Texts.NotALink, isProblem: true);
            return;
        }

        Start(new Source.OfStation(Station.FromLink(link)), _screenPlayerId);
    }

    private void PlaySong(int index)
    {
        var music = _music;

        if (music == null || index < 0 || index >= music.Paths.Count) return;

        Start(new Source.OfFolder(music, index), _screenPlayerId);
    }

    private void PlayNextSong()
    {
        if (_source is not Source.OfFolder folder || _isSuspended) return;

        Start(folder with { First = (Math.Max(_song, 0) + 1) % folder.Music.Paths.Count }, _playerId);
    }

    private void Start(Source source, long playerId)
    {
        if (!_game.IsInGame || playerId == NoPlayer)
        {
            Toast(Texts.NoGame, isProblem: true);
            return;
        }

        if (_isAtHome && _game.IsPowerOff())
        {
            Toast(Texts.NoPower, isProblem: true);
            return;
        }

        StopReading();
        _waitingSong = null;

        if (_isSwitchedOn && _playerId != playerId)
        {
            _game.SwitchOff(_playerId);
            _isSwitchedOn = false;
        }

        if (!_hold.IsHolding)
        {
            // Stopped while nothing is held, so the hold starts out remembering the home music, not silence.
            _game.StopGameMusic();
            _hold.Hold();
        }

        _source = source;
        _playerId = playerId;
        _isSuspended = false;

        if (_isAtHome && !SwitchOn()) return;

        Begin(source);
    }

    private bool SwitchOn()
    {
        if (_game.SwitchOn(_playerId))
        {
            _isSwitchedOn = true;
            return true;
        }

        StopRadio(switchOff: false, restoreMusic: true);
        _status = StatusLine.None;
        Toast(Texts.PlayerGone, isProblem: true);

        return false;
    }

    private void Begin(Source source)
    {
        _title = "";
        _hasSound = false;
        _hasSongStarted = false;

        switch (source)
        {
            case Source.OfStation { Station: var station } when !station.IsLink:
                _status = Connecting;
                Resolve(station);
                break;
            case Source.OfStation { Station: var station }:
                _status = Connecting;
                PlayStream(station);
                break;
            case Source.OfFolder folder:
                _song = folder.First;
                _status = Playing;
                Play(_maker.Songs(folder.Music.Paths, folder.First));
                break;
        }
    }

    /// <summary>Asks Radio Browser for the station's current link. Asking also counts the listen, which is how its popular list works.</summary>
    private void Resolve(Station station)
    {
        _resolveCancel = new CancellationTokenSource(ResolveTime);
        _resolvingStation = station;
        _resolving = _browser.Resolve(station.Id, _resolveCancel.Token);
        _nextCheckAt = 0;
    }

    /// <summary>When Radio Browser can't be reached, as can happen in some countries, the link it gave with the list is used.</summary>
    private void FinishResolve()
    {
        var task = _resolving;
        var station = _resolvingStation;

        if (task == null || station == null || !task.IsCompleted) return;

        _resolving = null;
        _resolvingStation = null;
        _resolveCancel = null;

        if (task.IsCompletedSuccessfully && task.Result is { } link)
        {
            Play(_maker.Stream(link));
            return;
        }

        _detail("Radio Browser gave no link for " + station.Name + ", so the one from the list is used: " + (task.IsCompletedSuccessfully ? "the station is gone" : ProblemOf(task)));
        PlayStream(station);
    }

    private void PlayStream(Station station)
    {
        if (!Uri.TryCreate(station.Url, UriKind.Absolute, out var link) || !IsWebLink(link))
        {
            Fail(Trouble.NotAStream, "the saved link isn't a web link: " + station.Url);
            return;
        }

        Play(_maker.Stream(link));
    }

    private void Play(IPlayback playback)
    {
        _playback = playback;
        _audio.Play(playback.Ring);
        _morale.Restart(0);
        _songStarts.Clear();
        _nextCheckAt = 0;
    }

    /// <summary>Stops the sound and anything still on its way, and leaves the record player and the game music as they are.</summary>
    private void StopReading()
    {
        _resolveCancel?.Cancel();
        _resolveCancel = null;
        _resolving = null;
        _resolvingStation = null;
        _hasSound = false;
        _isEnding = false;
        _songStarts.Clear();

        if (_playback == null) return;

        _playback.Stop();
        _playback = null;
        _audio.Stop();
    }

    private void StopRadio(bool switchOff, bool restoreMusic)
    {
        StopReading();

        if (switchOff && _isSwitchedOn) _game.SwitchOff(_playerId);

        _source = null;
        _playerId = NoPlayer;
        _isSwitchedOn = false;
        _isSuspended = false;
        _song = -1;
        _title = "";
        ReleaseMusic(restoreMusic);
    }

    private void ReleaseMusic(bool restoreMusic)
    {
        if (!_hold.IsHolding) return;

        var held = _hold.Release();

        if (restoreMusic) _game.PlayGameMusic(held);
    }

    /// <summary>Away from home with the radio set to stay there: the game music plays until the player is back.</summary>
    private void Suspend()
    {
        StopReading();
        _isSuspended = true;
        _title = "";
        _status = Away;
        ReleaseMusic(restoreMusic: true);
    }

    private void Resume()
    {
        var source = _source;

        if (source == null) return;

        if (_isAtHome && _game.IsPowerOff())
        {
            StopRadio(switchOff: false, restoreMusic: false);
            _status = StatusLine.None;
            Toast(Texts.PowerCut, isProblem: true);
            return;
        }

        Start(source is Source.OfFolder folder ? folder with { First = Math.Max(_song, 0) } : source, _playerId);
    }

    /// <summary>
    /// The first load of a save picks up the station or song that played when the player last left it. At home the
    /// record player has a new id from this load. On a trip it keeps the id it had, and the way home gives it a new one.
    /// </summary>
    /// <returns>True when the radio is back on, or waiting to play when the player gets home.</returns>
    private bool PickUpSave(Func<long, long?>? loadIds)
    {
        var key = _game.SaveKey;

        if (key == null || key == _saveKey) return false;

        _saveKey = key;
        var point = _resumes.Get(key);

        if (point == null || _source != null || !_settings.ResumeOnLoad) return false;

        if ((_isAtHome ? loadIds?.Invoke(point.PlayerId) : point.PlayerId) is not { } playerId)
        {
            _detail("The record player the radio last played on in this save is gone, so the radio stays off.");
            return false;
        }

        if (_isAtHome && _game.IsPowerOff())
        {
            _detail("The base has no power, so the radio stays off after the load.");
            return false;
        }

        if (point.Station == null)
        {
            WaitForSong(point with { PlayerId = playerId });
            return false;
        }

        PickUp(new Source.OfStation(point.Station), playerId);

        return _source != null;
    }

    private void PickUp(Source source, long playerId)
    {
        _detail("picking up " + NameOf(source) + " from the last time in this save");

        if (_isAtHome || _settings.PlayWhileExploring)
        {
            Start(source, playerId);
            return;
        }

        _source = source;
        _playerId = playerId;
        _song = source is Source.OfFolder folder ? folder.First : -1;
        Suspend();
    }

    private void WaitForSong(ResumePoint point)
    {
        _waitingSong = point;

        if (_music != null)
        {
            PlayWaitingSong();
            return;
        }

        if (_scanTask != null) return;

        _scanCancel = new CancellationTokenSource();
        _scanTask = _files.ScanMusic(_settings.MusicFolder, _scanCancel.Token);
        _nextCheckAt = 0;
    }

    private void PlayWaitingSong()
    {
        var point = _waitingSong;
        var music = _music;

        if (point == null || music == null) return;

        _waitingSong = null;
        var index = IndexOfSong(music, point.Song);

        if (index < 0)
        {
            _detail("The song that last played in this save isn't in the music folder any more, so the radio stays off: " + point.Song);
            return;
        }

        PickUp(new Source.OfFolder(music, index), point.PlayerId);
    }

    private static int IndexOfSong(MusicList music, string path)
    {
        for (var i = 0; i < music.Paths.Count; i++)
        {
            if (string.Equals(music.Paths[i], path, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return -1;
    }

    /// <summary>Leaving the save: what plays now is what its next load picks up. A song still waiting on the scan stays as it was.</summary>
    private void KeepForNextLoad()
    {
        var key = _saveKey;

        if (key == null) return;

        var point = PointNow();
        var isChanged = point != null ? _resumes.Set(key, point) : _waitingSong == null && _resumes.Remove(key);

        if (isChanged) _files.SaveResume(_resumes.ToJson());
    }

    private ResumePoint? PointNow() => _source switch
    {
        Source.OfStation { Station: var station } => new ResumePoint(_playerId, station, ""),
        Source.OfFolder folder => new ResumePoint(_playerId, null, folder.Music.Paths[_song >= 0 && _song < folder.Music.Paths.Count ? _song : folder.First]),
        _ => null,
    };

    private void FollowExploringSetting()
    {
        if (_source == null || _isAtHome) return;

        if (_settings.PlayWhileExploring && _isSuspended) Resume();
        else if (!_settings.PlayWhileExploring && !_isSuspended) Suspend();
    }

    private void TakePlayerEvents()
    {
        var playback = _playback;

        while (playback != null && playback == _playback && playback.TryTakeEvent(out var playerEvent))
        {
            if (playerEvent != null) OnPlayerEvent(playerEvent);
        }
    }

    private void OnPlayerEvent(PlayerEvent playerEvent)
    {
        switch (playerEvent)
        {
            case PlayerEvent.Connecting:
                _hasSound = false;
                _status = Connecting;
                break;
            case PlayerEvent.Playing playing:
                _hasSound = true;
                _detail(NameOf(_source) + ": playing " + playing.Format);
                break;
            case PlayerEvent.Title title:
                _title = title.Text;
                break;
            case PlayerEvent.Retrying retrying:
                _hasSound = false;
                _status = new StatusLine("retrying", retrying.Seconds.ToString(CultureInfo.InvariantCulture), Tone.Waiting);
                _detail($"{NameOf(_source)}: lost the signal ({retrying.Why}, {retrying.Detail}), trying again in {retrying.Seconds} s");
                break;
            case PlayerEvent.Failed failed:
                Fail(failed.Why, failed.Detail);
                break;
            case PlayerEvent.Ended:
                _isEnding = true;
                break;
            case PlayerEvent.SongStarts songStarts:
                _songStarts.Enqueue(songStarts);
                break;
            case PlayerEvent.SongSkipped skipped:
                _log("Skipped a song that couldn't be played: " + SongPath(skipped.Index) + " (" + skipped.Detail + ")");
                break;
            case PlayerEvent.Note note:
                _detail(note.Text);
                break;
        }
    }

    /// <summary>The status line says why, and a toast says it too, since the screen is usually closed by then.</summary>
    private void Fail(Trouble why, string detail)
    {
        var name = NameOf(_source);
        var arg = why is Trouble.Unsupported or Trouble.Refused ? detail : "";

        _log($"{name} stopped: {why}, {detail}");
        StopRadio(switchOff: true, restoreMusic: true);
        _status = new StatusLine(StatusKey(why), arg, Tone.Bad);
        Toast(Texts.StoppedBecause, isProblem: true, name, Texts.For(why).Say(_page.IsChinese, arg));
    }

    /// <summary>The reader works ahead, so a song shows, and the one before it counts as finished, once the sound reaches it.</summary>
    private void FollowSongs()
    {
        var playback = _playback;

        if (playback == null || _songStarts.Count == 0) return;

        var played = playback.Ring.PlayedFrames;

        while (_songStarts.Count > 0 && played >= _songStarts.Peek().AtFrame)
        {
            var songStarts = _songStarts.Dequeue();

            // A song that played to the end gives morale the way a finished record does.
            if (_hasSongStarted && IsCountingMorale) _game.GrantMorale();

            _hasSongStarted = true;
            _song = songStarts.Index;
        }
    }

    /// <summary>A station never ends, so it gives a record's morale after a set time of listening instead.</summary>
    private void CountStreamMorale()
    {
        if (_playback == null || _source is not Source.OfStation) return;

        if (_morale.Update(_playback.Ring.PlayedFrames, IsCountingMorale, _settings.StreamMoraleMinutes)) _game.GrantMorale();
    }

    /// <summary>A link to a file or a finished playlist ended. The reader works ahead, so the radio stops once the last of it has played.</summary>
    private void FinishEnding()
    {
        if (!_isEnding || _playback == null || _playback.Ring.WaitingFrames > 0) return;

        StopRadio(switchOff: true, restoreMusic: true);
        _status = Ended;
    }

    private void FollowSignal()
    {
        if (!_hasSound || _playback == null || _source is not Source.OfStation) return;

        var ring = _playback.Ring;

        // Until the first sound plays, the wait is still part of connecting.
        _status = !ring.IsBuffering ? Playing : ring.PlayedFrames == 0 ? Connecting : Buffering;
    }

    private void SetFavorite(string id, bool isFavorite)
    {
        var station = FindStation(id);

        if (station == null || !_favorites.Set(station, isFavorite)) return;

        _favoritesVersion++;

        if (_canSaveFavorites)
        {
            _files.SaveFavorites(_favorites.ToJson());
            return;
        }

        if (_hasWarnedFavorites) return;

        _hasWarnedFavorites = true;
        Toast(Texts.FavoritesUnreadable, isProblem: true);
    }

    private void SetVolume(int percent)
    {
        _settings = _settings with { Volume = percent };
        _keepVolume(percent);
        _audio.SetVolume(VolumeOf(percent));
    }

    private void OpenFolder()
    {
        var problem = _files.OpenMusicFolder(_settings.MusicFolder);

        if (problem != null) Toast(Texts.FolderFailed, isProblem: true, problem);
    }

    private void Paste()
    {
        var text = _files.ReadClipboard().Trim();

        if (text.Length == 0)
        {
            Toast(Texts.ClipboardEmpty, isProblem: true);
            return;
        }

        _page.Pasted(text.Length > MaxPasteLength ? text.Substring(0, MaxPasteLength) : text);
    }

    /// <summary>
    /// Sends the page whatever changed since it last took it. Building the now playing bar to compare costs one small
    /// object per check, and only while the music screen is open, which pauses the game.
    /// </summary>
    private void Send()
    {
        if (!_isScreenOpen) return;

        var now = NowOf();

        if (!now.Equals(_sentNow) && _page.Now(RadioView.Now(now))) _sentNow = now;

        // The stars go first, so a new list draws with them.
        if (_favoritesVersion != _sentFavoritesVersion && _page.Favorites(RadioView.Favorites(_favorites))) _sentFavoritesVersion = _favoritesVersion;

        if (_hasList && !_list.Equals(_sentList) && _page.List(RadioView.List(_list))) _sentList = _list;
    }

    private void ForgetSent()
    {
        _sentNow = null;
        _sentList = null;
        _sentFavoritesVersion = -1;
    }

    private NowView NowOf()
    {
        var isHere = _source != null && _playerId == _screenPlayerId;

        return _source switch
        {
            Source.OfStation { Station: var station } => new NowView(true, isHere, NowView.StationKind, station.Id, -1, station.Name, _title, _status, _settings.Volume, false),
            Source.OfFolder { Music: var music } => new NowView(true, isHere, NowView.FolderKind, "", _song, SongName(music), "", _status, _settings.Volume, music.Paths.Count > 1),
            _ => NowView.Off(_settings.Volume, _status),
        };
    }

    private Station? FindStation(string id)
    {
        foreach (var station in _rows)
        {
            if (station.Id == id) return station;
        }

        foreach (var station in _favorites.All)
        {
            if (station.Id == id) return station;
        }

        return _source is Source.OfStation { Station: var playing } && playing.Id == id ? playing : null;
    }

    private void CancelList()
    {
        // Not disposed: a request still running may touch its token after this.
        _listCancel?.Cancel();
        _listCancel = null;
        _listTask = null;
    }

    private void CancelScan()
    {
        _scanCancel?.Cancel();
        _scanCancel = null;
        _scanTask = null;
    }

    private void Toast(Phrase phrase, bool isProblem, params object[] args) => _page.Toast(phrase.Say(_page.IsChinese, args), isProblem);

    private string NameOf(Source? source) => source switch
    {
        Source.OfStation { Station: var station } => station.Name,
        Source.OfFolder => Texts.MyMusic.Pick(_page.IsChinese),
        _ => "",
    };

    /// <summary>The note for an empty folder names the folder; the one over the songs says how many and where.</summary>
    private static ListView MusicView(MusicList music)
    {
        var count = music.Paths.Count;

        return count == 0
            ? new ListView(Tab.Music, false, "", "noSongsIn", music.Folder, music.Folder, ListView.NoRows)
            : new ListView(Tab.Music, false, "", "songsIn", count.ToString(CultureInfo.InvariantCulture), music.Folder, music.RowsJson);
    }

    private string SongName(MusicList music) => _song >= 0 && _song < music.Names.Count ? music.Names[_song] : "";

    private string SongPath(int index) => _source is Source.OfFolder { Music: var music } && index >= 0 && index < music.Paths.Count ? music.Paths[index] : "song " + index.ToString(CultureInfo.InvariantCulture);

    private static StationList ListOf(Tab tab) => tab switch
    {
        Tab.China => StationList.China,
        Tab.Chinese => StationList.Chinese,
        Tab.Search => StationList.Search,
        _ => StationList.Popular,
    };

    private static string StatusKey(Trouble why) => why switch
    {
        Trouble.NotAStream => "notAStream",
        Trouble.Unsupported => "unsupported",
        Trouble.Refused => "refused",
        Trouble.NoSongs => "noSongs",
        _ => "cannotConnect",
    };

    private static string ProblemOf(Task task) => task.Exception?.GetBaseException().Message ?? "it took too long";

    private static bool IsWebLink(Uri link) => link.Scheme == Uri.UriSchemeHttp || link.Scheme == Uri.UriSchemeHttps;

    private static float VolumeOf(int percent) => Math.Clamp(percent, 0, 100) / 100f;

    /// <summary>What the radio plays.</summary>
    private abstract record Source
    {
        private Source()
        {
        }

        public sealed record OfStation(Station Station) : Source;

        /// <summary>The music folder as it was scanned, starting at one song and going round.</summary>
        public sealed record OfFolder(MusicList Music, int First) : Source;
    }

    private sealed record LoadedRows(IReadOnlyList<Station> Stations, string Json);
}
