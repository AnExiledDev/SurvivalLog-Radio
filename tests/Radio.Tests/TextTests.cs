using System.Reflection;
using System.Text.RegularExpressions;
using ExiledToolkit.Kit;
using Jint;
using Radio.Core;
using Xunit;

namespace Radio.Tests;

public class TextTests
{
    private static IEnumerable<(string Name, Phrase Phrase)> AllPhrases() =>
        typeof(Texts).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(Phrase))
            .Select(f => (f.Name, (Phrase)f.GetValue(null)!))
            .Concat(Texts.Page.Select(w => ("Page." + w.Key, w.Phrase)));

    private static string[] Holes(string text) => Regex.Matches(text, @"\{\d\}").Select(m => m.Value).OrderBy(h => h).ToArray();

    [Fact]
    public void EveryLineHasItsOwnChinese()
    {
        Assert.All(AllPhrases(), p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Phrase.Zh), p.Name);
            Assert.NotEqual(p.Phrase.En, p.Phrase.Zh);
        });
    }

    [Fact]
    public void TheChineseShowsEverythingTheEnglishFillsIn()
    {
        Assert.All(AllPhrases(), p => Assert.True(Holes(p.Phrase.En).SequenceEqual(Holes(p.Phrase.Zh)), p.Name));
    }

    [Fact]
    public void EnglishIsPlainTextAndChineseQuotesLabelsTheGameWay()
    {
        Assert.All(AllPhrases(), p =>
        {
            Assert.True(p.Phrase.En.All(c => c >= ' ' && c <= '~'), p.Name);
            Assert.DoesNotContain("\"", p.Phrase.Zh);
            Assert.DoesNotContain("\u201C", p.Phrase.Zh);
        });
    }

    [Fact]
    public void EveryReasonARadioStopsReadsDifferently()
    {
        var troubles = Enum.GetValues<Trouble>();

        Assert.Equal(troubles.Length, troubles.Select(t => Texts.For(t).En).Distinct().Count());
    }

    [Fact]
    public void EveryPageWordHasOneKey()
    {
        Assert.Equal(Texts.Page.Count, Texts.Page.Select(w => w.Key).Distinct().Count());
    }

    [Fact]
    public void ThePageGetsItsWordsInTheGamesLanguage()
    {
        var engine = new Engine();
        engine.Execute("var window = {};");
        engine.Execute(PageWords.Script(isChinese: true));

        Assert.Equal("正在播放", engine.Evaluate("window.__slradioWords.playing").AsString());
        Assert.Equal(Texts.Page.Count, (int)engine.Evaluate("Object.keys(window.__slradioWords).length").AsNumber());

        engine.Execute(PageWords.Script(isChinese: false));

        Assert.Equal("Lost the signal. Trying again in {0} seconds.", engine.Evaluate("window.__slradioWords.retrying").AsString());
    }

    [Fact]
    public void FilledInLinesReadRightInBothLanguages()
    {
        Assert.Equal("Jazz FM: The station won't play right now (HTTP 403).", Texts.StoppedBecause.Say(false, "Jazz FM", Texts.Refused.Say(false, "HTTP 403")));
        Assert.Equal("音乐台：这个电台现在无法播放（HTTP 403）。", Texts.StoppedBecause.Say(true, "音乐台", Texts.Refused.Say(true, "HTTP 403")));
    }
}
