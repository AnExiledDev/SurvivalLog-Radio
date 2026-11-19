using Radio.Core;
using Xunit;

namespace Radio.Tests;

/// <summary>The record player screen with its Radio tab, as the player sees and uses it, driven through page.js in Jint.</summary>
public class PageScriptTests
{
    private static readonly StatusLine Playing = new("playing", "", Tone.Good);

    private static NowView Off(int volume = 70) => NowView.Off(volume, StatusLine.None);

    private static NowView OnStation(string id, string name, bool isHere = true, int volume = 70) =>
        new(true, isHere, NowView.StationKind, id, -1, name, "Blue in Green", Playing, volume, false);

    private static NowView OnSong(int song, bool canNext = true) =>
        new(true, true, NowView.FolderKind, "", song, "b.mp3", "", Playing, 70, canNext);

    private static ListView Stations(Tab tab, params string[] ids) =>
        new(tab, false, "", "", "", "", RadioView.StationRows(ids.Select(id => new Station(id, "Station " + id, "http://radio.example/" + id, "CN", "MP3", 128)).ToList()));

    private static ListView Many(Tab tab, int count) => Stations(tab, Enumerable.Range(0, count).Select(i => "s" + i).ToArray());

    private static ListView Songs(params string[] names) =>
        new(Tab.Music, false, "", "songsIn", names.Length.ToString(), "C:/Music", RadioView.SongRows(names));

    private static int Rows(PageScript page) => page.Count("musicPage().querySelectorAll('.slr-row')");

    private static string Word(string key) => Texts.Page.First(w => w.Key == key).Phrase.En;

    /// <summary>The player has the record player screen open, with the radio off.</summary>
    private static PageScript Opened(bool hasObserver = false)
    {
        var page = new PageScript();

        page.Now(Off());
        page.OpenMusic(hasObserver);
        page.Poll();

        return page;
    }

    /// <summary>The player has switched the screen to the Radio tab, and the plugin has answered with the popular list.</summary>
    private static PageScript OnRadio(bool hasObserver = false)
    {
        var page = Opened(hasObserver);

        page.OpenRadio();
        page.List(Stations(Tab.Popular, "a", "b"));
        page.Sent.Clear();

        return page;
    }

    [Fact]
    public void TheScreenOpensOnTheRecordsWithASwitchToTheRadio()
    {
        var page = Opened();

        Assert.Equal(1, page.Readies);
        Assert.True(page.Is("isShown('.list')"));
        Assert.False(page.Is("isShown('.slr-view')"));
        Assert.Equal(Word("tabRadio"), page.Text("act('mode', 'radio').textContent"));
        Assert.Empty(page.Sent);
    }

    [Fact]
    public void TheRadioTabHidesTheRecordsAndAsksThePluginForEverything()
    {
        var page = Opened();

        page.OpenRadio();

        Assert.False(page.Is("isShown('.list')"));
        Assert.True(page.Is("isShown('.slr-view')"));
        Assert.Equal(new PageCommand[] { new PageCommand.Show() }, page.Sent);

        page.Click("act('mode', 'records')");

        Assert.True(page.Is("isShown('.list')"));
        Assert.False(page.Is("isShown('.slr-view')"));
    }

    [Fact]
    public void EachNewScreenAsksAgainAndGetsOneSetOfRadioParts()
    {
        var page = Opened();

        page.OpenRadio();
        page.CloseMusic();
        page.OpenMusic();
        page.Poll();

        Assert.Equal(new PageCommand[] { new PageCommand.Show(), new PageCommand.Show() }, page.Sent);
        Assert.Equal(1, page.Count("musicPage().querySelectorAll('.slr-switch')"));
        Assert.Equal(1, page.Count("musicPage().querySelectorAll('.slr-view')"));
    }

    [Fact]
    public void TheRecordPlayerThatPlaysTheRadioOpensOnTheRadioTab()
    {
        var page = new PageScript();

        page.Now(OnStation("a", "Jazz FM"));
        page.OpenMusic();
        page.Poll();

        Assert.True(page.Is("isShown('.slr-view')"));
        Assert.Contains(new PageCommand.Show(), page.Sent);

        page.Click("act('mode', 'records')");

        Assert.True(page.Is("isShown('.slr-here')"));
        Assert.Equal(Word("radioHere"), page.Text("shown('.slr-here')"));
    }

    [Fact]
    public void AnotherRecordPlayerOpensOnTheRecordsAndSaysTheRadioPlaysElsewhere()
    {
        var page = new PageScript();

        page.Now(OnStation("a", "Jazz FM", isHere: false));
        page.OpenMusic();
        page.Poll();

        Assert.True(page.Is("isShown('.list')"));
        Assert.False(page.Is("isShown('.slr-here')"));

        page.OpenRadio();

        Assert.Contains(Word("elsewhere"), page.Text("shown('.slr-now')"));
        Assert.True(page.Is("act('stop')"));
    }

    [Fact]
    public void EachListTabAsksForItsListAndShowsLoadingUntilItComes()
    {
        var page = OnRadio();

        page.Click("act('tab', 'china')");

        Assert.Equal(new PageCommand[] { new PageCommand.Browse(Tab.China) }, page.Sent);
        Assert.Equal(Word("loading"), page.Text("shown('.slr-note')"));
        Assert.True(page.Is("act('tab', 'china').classList.contains('on')"));

        page.List(Stations(Tab.China, "c1", "c2", "c3"));

        Assert.Equal("", page.Text("shown('.slr-note')"));
        Assert.Equal(3, Rows(page));
        Assert.Contains("Station c2", page.Text("shown('.slr-list')"));
        Assert.Contains("CN, MP3, 128 kbps", page.Text("shown('.slr-list')"));
    }

    [Fact]
    public void SearchWaitsForTheNameAndSendsItAsTyped()
    {
        var page = OnRadio();

        page.Click("act('tab', 'search')");

        Assert.Empty(page.Sent);
        Assert.True(page.Is("isShown('.slr-find')"));
        Assert.Equal(Word("typeToSearch"), page.Text("shown('.slr-note')"));

        page.Run("type(musicPage().querySelector('.slr-query'), '上海 news & jazz|1')");
        page.Run("pressEnter(musicPage().querySelector('.slr-query'))");

        Assert.Equal(new PageCommand[] { new PageCommand.Focus(true), new PageCommand.Search("上海 news & jazz|1") }, page.Sent);
    }

    /// <summary>Enter that picks a word in a Chinese input method must not send a half typed search.</summary>
    [Fact]
    public void EnterThatFinishesAnInputMethodWordSendsNothing()
    {
        var page = OnRadio();

        page.Click("act('tab', 'search')");
        page.Run("type(musicPage().querySelector('.slr-query'), 'shang')");
        page.Run("pressEnter(musicPage().querySelector('.slr-query'), { isComposing: true })");
        page.Run("pressEnter(musicPage().querySelector('.slr-query'), { keyCode: 229 })");

        Assert.DoesNotContain(page.Sent, c => c is PageCommand.Search);
    }

    [Fact]
    public void AListForAnotherTabDoesNotReplaceTheSearch()
    {
        var page = OnRadio();

        page.Click("act('tab', 'search')");
        page.List(Stations(Tab.Favorites, "f1"));

        Assert.True(page.Is("act('tab', 'search').classList.contains('on')"));
        Assert.DoesNotContain("Station f1", page.Text("shown('.slr-list')"));
    }

    [Fact]
    public void ClickingAStationPlaysItByItsId()
    {
        var page = OnRadio();

        page.List(Stations(Tab.Popular, "url:http://radio.example/live?x=1|2"));
        page.Click("act('play', 'url:http://radio.example/live?x=1|2').querySelector('b')");

        Assert.Equal(new PageCommand[] { new PageCommand.Play("url:http://radio.example/live?x=1|2") }, page.Sent);
    }

    [Fact]
    public void TheStarKeepsAStationWithoutPlayingIt()
    {
        var page = OnRadio();

        page.Click("act('fav', 'b').querySelector('path')");

        Assert.Equal(new PageCommand[] { new PageCommand.Star("b", true) }, page.Sent);

        page.Favorites("b");

        Assert.True(page.Is("act('fav', 'b').classList.contains('on')"));
        Assert.Equal(Word("unstar"), page.Text("act('fav', 'b').getAttribute('title')"));
        Assert.False(page.Is("act('fav', 'a').classList.contains('on')"));

        page.Click("act('fav', 'b')");

        Assert.Equal(new PageCommand.Star("b", false), page.Sent[^1]);
    }

    [Fact]
    public void TheStationPlayingIsMarkedInTheList()
    {
        var page = OnRadio();

        page.Now(OnStation("b", "Station b"));

        Assert.True(page.Is("act('play', 'b').classList.contains('is-playing')"));
        Assert.Contains(Word("playingTag"), page.Text("act('play', 'b').textContent"));
        Assert.False(page.Is("act('play', 'a').classList.contains('is-playing')"));
        Assert.Contains("Station b", page.Text("shown('.slr-now')"));
        Assert.Contains("Blue in Green", page.Text("shown('.slr-now')"));
    }

    [Fact]
    public void TheMusicTabListsTheSongsAndPlaysFromTheOnePicked()
    {
        var page = OnRadio();

        page.Click("act('tab', 'music')");
        page.List(Songs("a.mp3", "b.mp3"));

        Assert.True(page.Is("isShown('.slr-folder')"));
        Assert.Equal("2 songs in C:/Music", page.Text("shown('.slr-note')"));

        page.Click("act('file', 1)");
        page.Now(OnSong(1));

        Assert.Equal(new PageCommand.Song(1), page.Sent[^1]);
        Assert.True(page.Is("act('file', 1).classList.contains('is-playing')"));
        Assert.False(page.Is("act('file', 0).classList.contains('is-playing')"));

        page.Click("act('next')");
        page.Click("act('folder')");
        page.Click("act('scan')");

        Assert.Equal(new PageCommand[] { new PageCommand.Next(), new PageCommand.OpenFolder(), new PageCommand.Scan() }, page.Sent.TakeLast(3));
    }

    [Fact]
    public void NextShowsOnlyWhenThereIsANextSong()
    {
        var page = OnRadio();

        page.Now(OnSong(0, canNext: false));

        Assert.False(page.Is("act('next')"));

        page.Now(OnStation("a", "Station a"));

        Assert.False(page.Is("act('next')"));
    }

    [Fact]
    public void AnEmptyFolderSaysWhereToPutTheSongs()
    {
        var page = OnRadio();

        page.Click("act('tab', 'music')");
        page.List(new ListView(Tab.Music, false, "", "noSongsIn", "C:/Games/Radio Music", "C:/Games/Radio Music", ListView.NoRows));

        Assert.StartsWith("No songs in C:/Games/Radio Music.", page.Text("shown('.slr-note')"));
    }

    [Fact]
    public void TheNowBarSaysWhyTheRadioStoppedInPlainWords()
    {
        var page = OnRadio();

        page.Now(NowView.Off(70, new StatusLine("refused", "HTTP 403", Tone.Bad)));

        Assert.Contains(Word("off"), page.Text("shown('.slr-now')"));
        Assert.Contains(Texts.Refused.Say(false, "HTTP 403"), page.Text("shown('.slr-now')"));
        Assert.True(page.Is("musicPage().querySelector('.slr-now .bad')"));
        Assert.False(page.Is("act('stop')"));
    }

    [Fact]
    public void TheLinkBoxPlaysWhatWasPastedOrTyped()
    {
        var page = OnRadio();

        page.Click("act('url')");

        Assert.Empty(page.Sent);

        page.Click("act('paste')");
        page.Pasted("  http://radio.example/live.mp3 ");

        Assert.Equal("  http://radio.example/live.mp3 ", page.Text("musicPage().querySelector('.slr-url').value"));

        page.Click("act('url')");

        Assert.Equal(new PageCommand[] { new PageCommand.Paste(), new PageCommand.Link("http://radio.example/live.mp3") }, page.Sent);

        page.Run("type(musicPage().querySelector('.slr-url'), 'https://radio.example/b.aac')");
        page.Run("pressEnter(musicPage().querySelector('.slr-url'))");

        Assert.Equal(new PageCommand.Link("https://radio.example/b.aac"), page.Sent[^1]);
    }

    [Fact]
    public void TextBoxesTellThePluginWhenTheyTakeAndGiveBackTheKeyboard()
    {
        var page = OnRadio();

        page.Run("type(musicPage().querySelector('.slr-url'), 'h')");
        page.Run("fire(musicPage().querySelector('.slr-url'), 'focusout')");
        page.Run("type(musicPage().querySelector('.slr-url'), 'ht')");
        page.Click("act('mode', 'records')");

        Assert.Equal(new PageCommand[] { new PageCommand.Focus(true), new PageCommand.Focus(false), new PageCommand.Focus(true), new PageCommand.Focus(false) }, page.Sent);
    }

    /// <summary>The plugin lets go of the keyboard when the screen closes, so the next screen's box must claim it again.</summary>
    [Fact]
    public void ABoxOnTheNextScreenClaimsTheKeyboardAgain()
    {
        var page = OnRadio();

        page.Run("type(musicPage().querySelector('.slr-url'), 'h')");
        page.CloseMusic();
        page.OpenMusic();
        page.Poll();
        page.Run("type(musicPage().querySelector('.slr-url'), 'h')");

        Assert.Equal(2, page.Sent.Count(c => c is PageCommand.Focus { IsTyping: true }));
    }

    [Fact]
    public void DraggingTheVolumeSendsOneStepPerTickAndTheLastWhenLetGo()
    {
        var page = OnRadio();

        page.Run("drag([10, 20, 30])");

        Assert.Empty(page.Sent);
        Assert.Equal("30", page.Text("shown('.slr-vn')"));

        page.Run("runTimers()");

        Assert.Equal(new PageCommand[] { new PageCommand.Volume(30) }, page.Sent);

        page.Run("drag([45])");
        page.Run("letGo()");
        page.Run("runTimers()");

        Assert.Equal(new PageCommand[] { new PageCommand.Volume(30), new PageCommand.Volume(45) }, page.Sent);
    }

    [Fact]
    public void ThePluginDoesNotMoveTheSliderWhileThePlayerHoldsIt()
    {
        var page = OnRadio();

        Assert.Equal("70", page.Text("musicPage().querySelector('.slr-slider').value"));

        page.Run("drag([20])");
        page.Now(Off(volume: 70));

        Assert.Equal("20", page.Text("musicPage().querySelector('.slr-slider').value"));

        page.Run("letGo()");
        page.Now(Off(volume: 55));

        Assert.Equal("55", page.Text("musicPage().querySelector('.slr-slider').value"));
        Assert.Equal("55", page.Text("shown('.slr-vn')"));
    }

    [Fact]
    public void ALongListDrawsMoreRowsAsThePlayerScrollsDown()
    {
        var page = OnRadio(hasObserver: true);

        page.List(Many(Tab.Popular, 150));

        Assert.Equal(60, Rows(page));

        page.Run("scrollToEnd()");

        Assert.Equal(120, Rows(page));

        page.Run("scrollToEnd()");

        Assert.Equal(150, Rows(page));
        Assert.False(page.Is("musicPage().querySelector('.slr-more')"));

        page.Click("act('tab', 'china')");
        page.List(Many(Tab.China, 150));

        Assert.Equal(60, Rows(page));
    }

    [Fact]
    public void WithoutAnObserverTheWholeListIsDrawn()
    {
        var page = OnRadio();

        page.List(Many(Tab.Popular, 150));

        Assert.Equal(150, Rows(page));
    }

    [Fact]
    public void TheWordsFollowTheGameLanguage()
    {
        var page = OnRadio();

        page.Words(isChinese: true);
        page.Now(Off());

        Assert.Equal(Texts.Page.First(w => w.Key == "tabRadio").Phrase.Zh, page.Text("act('mode', 'radio').textContent"));
        Assert.Equal(Texts.Page.First(w => w.Key == "linkHint").Phrase.Zh, page.Text("musicPage().querySelector('.slr-url').placeholder"));
        Assert.Contains(Texts.Page.First(w => w.Key == "off").Phrase.Zh, page.Text("shown('.slr-now')"));
    }

    [Fact]
    public void AScreenAlreadyDrawnIsNotSearchedAgain()
    {
        var page = OnRadio();

        page.Run("searches = 0");
        page.Poll();
        page.Now(OnStation("a", "Station a"));
        page.List(Stations(Tab.Popular, "a", "b", "c"));
        page.Poll();

        Assert.Equal(0, (int)double.Parse(page.Text("searches")));
    }

    [Fact]
    public void TheScreenWorksWhenTheToolkitLoadsAfterRadio()
    {
        var page = new PageScript(isToolkitFirst: false);

        page.Now(Off());
        page.OpenMusic();
        page.Poll();

        Assert.Equal(0, page.Readies);
        Assert.False(page.Is("musicPage().querySelector('.slr-switch')"));

        page.LoadToolkit();
        page.Poll();

        Assert.Equal(1, page.Readies);
        Assert.True(page.Is("musicPage().querySelector('.slr-switch')"));

        page.OpenRadio();

        Assert.Equal(new PageCommand[] { new PageCommand.Show() }, page.Sent);
    }
}
