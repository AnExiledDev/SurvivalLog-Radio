using System.Text.Json;
using Radio.Core;
using Xunit;

namespace Radio.Tests;

/// <summary>Radio Browser's station JSON, the way its API sends it.</summary>
internal static class StationJson
{
    public static Dictionary<string, object> Item(string id, string name, string? url = null, string codec = "MP3", string country = "Germany", object? bitrate = null) => new()
    {
        ["stationuuid"] = id,
        ["name"] = name,
        ["url"] = "",
        ["url_resolved"] = url ?? $"http://radio.example/{id}",
        ["codec"] = codec,
        ["country"] = country,
        ["bitrate"] = bitrate ?? 128,
    };

    public static string Json(params object[] items) => JsonSerializer.Serialize(items);
}

/// <summary>Stands in for Radio Browser's servers: some can be down, and each path gets its own answer.</summary>
internal sealed class FakeDirectory
{
    private readonly List<Uri> _asked = new();

    public HashSet<string> Down { get; } = new();

    public Func<Uri, string> Answer { get; set; } = _ => "[]";

    public IReadOnlyList<Uri> Asked
    {
        get
        {
            lock (_asked) return _asked.ToList();
        }
    }

    public Task<string> Fetch(Uri uri, CancellationToken cancel)
    {
        lock (_asked) _asked.Add(uri);

        if (Down.Contains(uri.Host)) return Task.FromException<string>(new HttpRequestException("the server is down"));

        return Task.FromResult(Answer(uri));
    }
}

public class StationTests
{
    [Fact]
    public void TheLineUnderTheNameShowsWhatIsKnown()
    {
        Assert.Equal("Germany, MP3, 128 kbps", new Station("a", "A", "http://x", "Germany", "MP3", 128).Details());
        Assert.Equal("China", new Station("a", "A", "http://x", "China", "UNKNOWN", 0).Details());
        Assert.Equal("", new Station("a", "A", "http://x", "", "", 0).Details());
    }

    [Fact]
    public void APastedLinkIsAStationNamedAfterItsServer()
    {
        var station = Station.FromLink(new Uri("http://radio.example:8000/live"));

        Assert.True(station.IsLink);
        Assert.Equal("url:http://radio.example:8000/live", station.Id);
        Assert.Equal("radio.example", station.Name);
        Assert.Equal("http://radio.example:8000/live", station.Url);
        Assert.False(new Station("9617a958", "A", "http://x", "", "", 0).IsLink);
    }
}

public class RadioBrowserParseTests
{
    [Fact]
    public void AStationKeepsItsNameLinkCountryFormatAndQuality()
    {
        var station = Assert.Single(RadioBrowser.Parse(StationJson.Json(StationJson.Item("9617a958", "Jazz FM", "https://jazz.example/live", "mp3", "Germany", 192))));

        Assert.Equal(("9617a958", "Jazz FM", "https://jazz.example/live", "Germany", "MP3", 192), (station.Id, station.Name, station.Url, station.Country, station.Codec, station.Bitrate));
    }

    [Fact]
    public void StationsInFormatsTheRadioCannotPlayAreLeftOut()
    {
        var json = StationJson.Json(
            StationJson.Item("1", "Ogg", codec: "OGG"),
            StationJson.Item("2", "Opus", codec: "OPUS"),
            StationJson.Item("3", "Flac", codec: "FLAC"),
            StationJson.Item("4", "Aac plus", codec: "AAC+"),
            StationJson.Item("5", "Unknown", codec: "UNKNOWN"),
            StationJson.Item("6", "Blank", codec: ""),
            StationJson.Item("7", "Aac", codec: " aac "));

        Assert.Equal(new[] { "4", "5", "6", "7" }, RadioBrowser.Parse(json).Select(s => s.Id));
    }

    [Fact]
    public void TheOriginalLinkIsUsedWhenThereIsNoCheckedOne()
    {
        var item = StationJson.Item("1", "A", url: "");
        item["url"] = "http://radio.example/original";

        Assert.Equal("http://radio.example/original", Assert.Single(RadioBrowser.Parse(StationJson.Json(item))).Url);
    }

    [Fact]
    public void StationsWithoutAWebLinkANameOrAnIdAreLeftOut()
    {
        var json = StationJson.Json(
            StationJson.Item("1", "Rtsp", url: "rtsp://radio.example/live"),
            StationJson.Item("2", "Mms", url: "mms://radio.example/live"),
            StationJson.Item("3", "No link", url: "not a link"),
            StationJson.Item("4", " \t "),
            StationJson.Item("", "No id"),
            StationJson.Item("6", "Good"));

        Assert.Equal(new[] { "6" }, RadioBrowser.Parse(json).Select(s => s.Id));
    }

    [Fact]
    public void TheSameStreamListedTwiceShowsOnce()
    {
        var json = StationJson.Json(StationJson.Item("1", "A", "http://radio.example/live"), StationJson.Item("2", "A again", "HTTP://RADIO.EXAMPLE/live"));

        Assert.Equal(new[] { "1" }, RadioBrowser.Parse(json).Select(s => s.Id));
    }

    [Fact]
    public void NamesLoseTheirTabsLineBreaksAndExtraSpaces()
    {
        Assert.Equal("Radio One", Assert.Single(RadioBrowser.Parse(StationJson.Json(StationJson.Item("1", "  Radio\t\n One  ")))).Name);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData("128")]
    public void AQualityThatIsNotAPositiveNumberIsUnknown(object bitrate)
    {
        Assert.Equal(0, Assert.Single(RadioBrowser.Parse(StationJson.Json(StationJson.Item("1", "A", bitrate: bitrate)))).Bitrate);
    }

    [Fact]
    public void AnAnswerThatIsNotAListGivesNoStations()
    {
        Assert.Empty(RadioBrowser.Parse("{\"error\":\"busy\"}"));
    }
}

public class RadioBrowserMergeTests
{
    private static Station S(string id) => new(id, id, "http://radio.example/" + id, "", "", 0);

    private static string[] Ids(IReadOnlyList<Station> stations) => stations.Select(s => s.Id).ToArray();

    [Fact]
    public void TheTopOfEveryListComesBeforeTheTailOfAny()
    {
        var merged = RadioBrowser.Merge(new[] { new[] { S("a1"), S("a2"), S("a3") }, new[] { S("b1") } }, 10);

        Assert.Equal(new[] { "a1", "b1", "a2", "a3" }, Ids(merged));
    }

    [Fact]
    public void AStationInTwoListsShowsOnceWhereItFirstAppears()
    {
        var merged = RadioBrowser.Merge(new[] { new[] { S("a1"), S("both") }, new[] { S("both"), S("b2") } }, 10);

        Assert.Equal(new[] { "a1", "both", "b2" }, Ids(merged));
    }

    [Fact]
    public void TheMergedListStopsAtItsLimit()
    {
        var merged = RadioBrowser.Merge(new[] { new[] { S("a1"), S("a2") }, new[] { S("b1"), S("b2") }, new[] { S("c1") } }, 2);

        Assert.Equal(new[] { "a1", "b1" }, Ids(merged));
    }
}

public class RadioBrowserTests
{
    private const string FirstServer = "all.api.radio-browser.info";

    private readonly FakeDirectory _directory = new();
    private readonly RadioBrowser _browser;

    public RadioBrowserTests() => _browser = new RadioBrowser(_directory.Fetch);

    private static string QueryOf(Uri uri) => Uri.UnescapeDataString(uri.Query);

    private string[] Ids(StationList list, string query = "") => _browser.List(list, query, CancellationToken.None).GetAwaiter().GetResult().Select(s => s.Id).ToArray();

    [Fact]
    public void TheChinaListIsTheStationsInChina()
    {
        _directory.Answer = uri => uri.AbsolutePath == "/json/stations/bycountrycodeexact/CN" ? StationJson.Json(StationJson.Item("cn1", "CNR")) : StationJson.Json(StationJson.Item("other", "Other"));

        Assert.Equal(new[] { "cn1" }, Ids(StationList.China));
    }

    [Fact]
    public void TheChineseListMergesTheThreeWaysStationsNameTheLanguage()
    {
        _directory.Answer = uri =>
        {
            var query = QueryOf(uri);

            if (query.Contains("language=chinese&")) return StationJson.Json(StationJson.Item("zh1", "A"), StationJson.Item("zh2", "B"));

            if (query.Contains("language=mandarin&")) return StationJson.Json(StationJson.Item("md1", "C"));

            if (query.Contains("language=cantonese&")) return StationJson.Json(StationJson.Item("yue1", "D"), StationJson.Item("zh1", "A"));

            return "[]";
        };

        Assert.Equal(new[] { "zh1", "md1", "yue1", "zh2" }, Ids(StationList.Chinese));
    }

    [Fact]
    public void ASearchLooksForTheNameAsTyped()
    {
        _directory.Answer = uri => QueryOf(uri).Contains("name=中国 & FM&") ? StationJson.Json(StationJson.Item("hit", "Hit")) : "[]";

        Assert.Equal(new[] { "hit" }, Ids(StationList.Search, "  中国 & FM "));
    }

    [Fact]
    public void ABlankSearchAsksNothing()
    {
        Assert.Empty(Ids(StationList.Search, "   "));
        Assert.Empty(_directory.Asked);
    }

    [Fact]
    public void AServerThatIsDownIsSkippedAndTheOneThatAnsweredIsKept()
    {
        _directory.Down.Add(FirstServer);
        _directory.Answer = _ => StationJson.Json(StationJson.Item("1", "A"));

        Assert.Equal(new[] { "1" }, Ids(StationList.Popular));
        Assert.Equal(new[] { "1" }, Ids(StationList.Popular));

        Assert.Equal(new[] { FirstServer, "de1.api.radio-browser.info", "de1.api.radio-browser.info" }, _directory.Asked.Select(u => u.Host));
    }

    [Fact]
    public void WithEveryServerDownTheListFails()
    {
        _directory.Down.UnionWith(new[] { FirstServer, "de1.api.radio-browser.info", "de2.api.radio-browser.info" });

        Assert.Throws<HttpRequestException>(() => Ids(StationList.Popular));
    }

    [Fact]
    public void AStationsCurrentLinkIsAskedForByItsId()
    {
        _directory.Answer = uri => uri.AbsolutePath == "/json/url/9617a958" ? "{\"ok\":true,\"url\":\"https://jazz.example/new\"}" : "{}";

        var link = _browser.Resolve("9617a958", CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal("https://jazz.example/new", link?.AbsoluteUri);
    }

    [Theory]
    [InlineData("{\"ok\":false,\"message\":\"station not found\"}")]
    [InlineData("{\"ok\":true,\"url\":\"not a link\"}")]
    [InlineData("[]")]
    public void AStationThatIsGoneHasNoCurrentLink(string answer)
    {
        _directory.Answer = _ => answer;

        Assert.Null(_browser.Resolve("gone", CancellationToken.None).GetAwaiter().GetResult());
    }
}

public class FavoritesTests
{
    private static Station S(string id, string name = "A") => new(id, name, "http://radio.example/" + id, "China", "AAC", 64);

    [Fact]
    public void AStarredStationIsKeptAndAnUnstarredOneGoes()
    {
        var favorites = new Favorites(Array.Empty<Station>());

        Assert.True(favorites.Set(S("a"), true));
        Assert.True(favorites.Set(S("b"), true));
        Assert.True(favorites.Set(S("a"), false));

        Assert.Equal(new[] { "b" }, favorites.All.Select(s => s.Id));
        Assert.True(favorites.Contains("b"));
        Assert.False(favorites.Contains("a"));
    }

    [Fact]
    public void StarringTwiceOrUnstarringWhatIsNotThereChangesNothing()
    {
        var favorites = new Favorites(new[] { S("a") });

        Assert.False(favorites.Set(S("a"), true));
        Assert.False(favorites.Set(S("z"), false));
        Assert.Single(favorites.All);
    }

    [Fact]
    public void TheListHasALimit()
    {
        var favorites = new Favorites(Enumerable.Range(0, Favorites.Max).Select(i => S("s" + i)));

        Assert.False(favorites.Set(S("one more"), true));
        Assert.Equal(Favorites.Max, favorites.All.Count);
    }

    [Fact]
    public void TheFileGivesBackEveryStationAsItWasSaved()
    {
        var saved = new Favorites(new[] { S("a", "中文电台 \"Live\""), Station.FromLink(new Uri("http://radio.example/live")) });

        var loaded = Favorites.Parse(saved.ToJson(), out var isReadable);

        Assert.True(isReadable);
        Assert.Equal(
            saved.All.Select(s => (s.Id, s.Name, s.Url, s.Country, s.Codec, s.Bitrate)),
            loaded.All.Select(s => (s.Id, s.Name, s.Url, s.Country, s.Codec, s.Bitrate)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n")]
    public void NoFileYetIsAnEmptyList(string? json)
    {
        var favorites = Favorites.Parse(json, out var isReadable);

        Assert.True(isReadable);
        Assert.Empty(favorites.All);
    }

    [Theory]
    [InlineData("[{\"id\":\"a\",")]
    [InlineData("{\"id\":\"a\"}")]
    public void ABrokenFileIsUnreadable(string json)
    {
        Favorites.Parse(json, out var isReadable);

        Assert.False(isReadable);
    }

    [Fact]
    public void EntriesWithoutAnIdOrALinkAreDropped()
    {
        var favorites = Favorites.Parse("[{\"id\":\"a\",\"url\":\"http://x\"},{\"id\":\"b\"},{\"url\":\"http://y\"},5]", out var isReadable);

        Assert.True(isReadable);
        Assert.Equal(new[] { "a" }, favorites.All.Select(s => s.Id));
    }
}

public class MusicFolderTests
{
    private const string Root = "/music";

    [Theory]
    [InlineData("a.mp3", true)]
    [InlineData("a.MP3", true)]
    [InlineData("a.m4a", true)]
    [InlineData("a.flac", true)]
    [InlineData("a.wma", true)]
    [InlineData("a.ogg", false)]
    [InlineData("cover.jpg", false)]
    [InlineData("notes.txt", false)]
    [InlineData("mp3", false)]
    public void OnlyFilesWindowsCanPlayAreSongs(string file, bool isSong)
    {
        Assert.Equal(isSong, MusicFolder.IsSong(file));
    }

    [Fact]
    public void SongsAreSortedTheWayAPersonWould()
    {
        var files = new[] { "/music/10 Ten.mp3", "/music/2 Two.mp3", "/music/b.mp3", "/music/A.mp3", "/music/1 One.mp3", "/music/cover.jpg" };

        Assert.Equal(new[] { "/music/1 One.mp3", "/music/2 Two.mp3", "/music/10 Ten.mp3", "/music/A.mp3", "/music/b.mp3" }, MusicFolder.Songs(files, Root));
    }

    [Fact]
    public void LeadingZerosDoNotChangeTheOrder()
    {
        var files = new[] { "/music/Track 010.mp3", "/music/Track 9.mp3", "/music/Track 02.mp3" };

        Assert.Equal(new[] { "/music/Track 02.mp3", "/music/Track 9.mp3", "/music/Track 010.mp3" }, MusicFolder.Songs(files, Root));
    }

    [Fact]
    public void ASongInASubfolderShowsTheSubfolder()
    {
        Assert.Equal("Album/Disc 1/01 Intro", MusicFolder.DisplayName(Root, "/music/Album/Disc 1/01 Intro.mp3"));
        Assert.Equal("Song", MusicFolder.DisplayName(Root, "/music/Song.m4a"));
    }

    [Fact]
    public void AScanGivesThePathsTheNamesAndTheRowsInTheSameOrder()
    {
        var music = MusicFolder.Scan(new[] { "/music/b/2.mp3", "/music/a.mp3" }, Root);

        Assert.Equal(new[] { "/music/a.mp3", "/music/b/2.mp3" }, music.Paths);
        Assert.Equal(new[] { "a", "b/2" }, music.Names);
        Assert.Equal(new[] { "a", "b/2" }, JsonSerializer.Deserialize<string[]>(music.RowsJson));
        Assert.Equal(Root, music.Folder);
    }

    [Fact]
    public void AHugeFolderIsCutOffAtTheLimit()
    {
        var files = Enumerable.Range(0, MusicFolder.MaxSongs + 10).Select(i => $"/music/{i}.mp3");

        Assert.Equal(MusicFolder.MaxSongs, MusicFolder.Songs(files, Root).Count);
    }
}

public class MusicHoldTests
{
    [Fact]
    public void WhileNotHoldingTheGameMusicPlays()
    {
        var hold = new MusicHold();

        Assert.False(hold.OnPlay("event:/Music/Home"));
        Assert.False(hold.OnFadeIn("event:/Music/Home", 2, 1));
    }

    [Fact]
    public void WhileHoldingTheMusicIsSkippedAndTheLastAskIsKept()
    {
        var hold = new MusicHold();
        hold.Hold();

        Assert.True(hold.OnPlay("event:/Music/Trip"));
        Assert.True(hold.OnFadeIn("event:/Music/Battle", 2.5f, 0.8f));

        Assert.Equal(new HeldMusic.FadeIn("event:/Music/Battle", 2.5f, 0.8f), hold.Release());
    }

    [Fact]
    public void WithNothingAskedTheHomeMusicComesBack()
    {
        var hold = new MusicHold();
        hold.OnPlay("event:/Music/Trip");
        hold.Hold();

        Assert.Equal(new HeldMusic.Home(), hold.Release());
    }

    [Fact]
    public void TheGameStoppingItsMusicMeansSilence()
    {
        var hold = new MusicHold();
        hold.Hold();
        hold.OnPlay("event:/Music/Trip");

        hold.OnStop();

        Assert.Equal(new HeldMusic.Silence(), hold.Release());
    }

    [Fact]
    public void AfterReleaseTheGameMusicPlaysAgain()
    {
        var hold = new MusicHold();
        hold.Hold();
        hold.Release();

        Assert.False(hold.IsHolding);
        Assert.False(hold.OnPlay("event:/Music/Home"));
    }
}

public class PageCommandTests
{
    public static TheoryData<string, PageCommand> Commands() => new()
    {
        { "ready", new PageCommand.Ready() },
        { "noscript", new PageCommand.NoScript() },
        { "diag|a|b", new PageCommand.Note("a|b") },
        { "focus|1", new PageCommand.Focus(true) },
        { "focus|0", new PageCommand.Focus(false) },
        { "show", new PageCommand.Show() },
        { "search|%E4%B8%AD%E6%96%87%7Cfm", new PageCommand.Search("中文|fm") },
        { "search|", new PageCommand.Search("") },
        { "browse|china", new PageCommand.Browse(Tab.China) },
        { "browse|music", new PageCommand.Browse(Tab.Music) },
        { "play|9617a958", new PageCommand.Play("9617a958") },
        { "play|url%3Ahttp%3A%2F%2Fradio.example%2Flive", new PageCommand.Play("url:http://radio.example/live") },
        { "url|http%3A%2F%2Fradio.example%2Flive%3Fa%3D1%7C2", new PageCommand.Link("http://radio.example/live?a=1|2") },
        { "fav|9617a958|1", new PageCommand.Star("9617a958", true) },
        { "fav|9617a958|0", new PageCommand.Star("9617a958", false) },
        { "file|12", new PageCommand.Song(12) },
        { "next", new PageCommand.Next() },
        { "stop", new PageCommand.Stop() },
        { "volume|0", new PageCommand.Volume(0) },
        { "volume|100", new PageCommand.Volume(100) },
        { "folder", new PageCommand.OpenFolder() },
        { "scan", new PageCommand.Scan() },
        { "paste", new PageCommand.Paste() },
    };

    [Theory]
    [MemberData(nameof(Commands))]
    public void TheRadioUnderstandsWhatThePageSends(string body, PageCommand command)
    {
        Assert.Equal(command, PageCommand.Parse(PageCommand.Prefix + body));
    }

    [Theory]
    [InlineData("slmod|packrat|ready")]
    [InlineData("slmod|radio|")]
    [InlineData("slmod|radio|dance")]
    [InlineData("slmod|radio|ready|now")]
    [InlineData("slmod|radio|focus|2")]
    [InlineData("slmod|radio|browse|search")]
    [InlineData("slmod|radio|browse|")]
    [InlineData("slmod|radio|play|")]
    [InlineData("slmod|radio|url|")]
    [InlineData("slmod|radio|fav|a|yes")]
    [InlineData("slmod|radio|file|-1")]
    [InlineData("slmod|radio|file|+3")]
    [InlineData("slmod|radio|volume|101")]
    [InlineData("slmod|radio|volume|50.5")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElseIsIgnored(string? message)
    {
        Assert.Null(PageCommand.Parse(message));
    }

    [Fact]
    public void OnlyRadioMessagesAreOurs()
    {
        Assert.True(PageCommand.IsOurs("slmod|radio|ready"));
        Assert.False(PageCommand.IsOurs("slmod|radiox|ready"));
        Assert.False(PageCommand.IsOurs(null));
    }
}

public class RadioViewTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void TheNowPlayingBarCarriesEverythingThePageShows()
    {
        var now = new NowView(true, false, NowView.StationKind, "9617a958", -1, "中文 \"FM\"", "Artist - Song", new StatusLine("retrying", "5", Tone.Waiting), 80, false);

        var json = Parse(RadioView.Now(now));

        Assert.True(json.GetProperty("on").GetBoolean());
        Assert.False(json.GetProperty("here").GetBoolean());
        Assert.Equal("station", json.GetProperty("kind").GetString());
        Assert.Equal("9617a958", json.GetProperty("id").GetString());
        Assert.Equal(-1, json.GetProperty("song").GetInt32());
        Assert.Equal("中文 \"FM\"", json.GetProperty("name").GetString());
        Assert.Equal("Artist - Song", json.GetProperty("title").GetString());
        Assert.Equal("retrying", json.GetProperty("status").GetString());
        Assert.Equal("5", json.GetProperty("arg").GetString());
        Assert.Equal("wait", json.GetProperty("tone").GetString());
        Assert.Equal(80, json.GetProperty("volume").GetInt32());
        Assert.False(json.GetProperty("canNext").GetBoolean());
    }

    [Fact]
    public void TheListCarriesItsRowsAsTheyWereBuilt()
    {
        var rows = RadioView.StationRows(new[] { new Station("a", "Jazz </script>", "http://x", "Germany", "MP3", 128) });

        var json = Parse(RadioView.List(new ListView(Tab.Chinese, false, "q", "songsIn", "3", "/music", rows)));

        Assert.Equal("chinese", json.GetProperty("tab").GetString());
        Assert.False(json.GetProperty("loading").GetBoolean());
        Assert.Equal("songsIn", json.GetProperty("note").GetString());
        Assert.Equal("3", json.GetProperty("noteArg").GetString());
        Assert.Equal("/music", json.GetProperty("folder").GetString());

        var row = json.GetProperty("rows")[0];

        Assert.Equal("a", row.GetProperty("id").GetString());
        Assert.Equal("Jazz </script>", row.GetProperty("name").GetString());
        Assert.Equal("Germany, MP3, 128 kbps", row.GetProperty("meta").GetString());
    }

    [Fact]
    public void TheStarsAreTheFavoriteIds()
    {
        var favorites = new Favorites(new[] { new Station("a", "A", "http://x", "", "", 0), new Station("b", "B", "http://y", "", "", 0) });

        Assert.Equal(new[] { "a", "b" }, JsonSerializer.Deserialize<string[]>(RadioView.Favorites(favorites)));
    }

    [Theory]
    [InlineData(Tone.Good, "good")]
    [InlineData(Tone.Bad, "bad")]
    [InlineData(Tone.None, "")]
    public void TheToneSaysHowTheStatusLineIsColored(Tone tone, string name)
    {
        var now = NowView.Off(50, new StatusLine("x", "", tone));

        Assert.Equal(name, Parse(RadioView.Now(now)).GetProperty("tone").GetString());
    }
}
