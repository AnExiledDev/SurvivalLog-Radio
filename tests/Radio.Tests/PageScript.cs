using ExiledToolkit.Web;
using Jint;
using Radio.Core;

namespace Radio.Tests;

/// <summary>
/// page.js exactly as the plugin embeds it, run in Jint with Exiled's Toolkit's page API (api.js, from the Toolkit's
/// source) on top of FakePage.js (Root.html, the game's record player screen and the vuplex bridge). The words go
/// first, as the plugin hands them over before its first call. What the script posts is parsed with the real
/// PageCommand, so a test sees the commands the plugin would act on.
/// </summary>
internal sealed class PageScript
{
    private readonly Engine _engine = new();

    /// <param name="isToolkitFirst">
    /// The game runs the Toolkit's script first, or with false Radio's first; then the Toolkit's runs only at
    /// <see cref="LoadToolkit"/>.
    /// </param>
    public PageScript(bool isToolkitFirst = true, bool isChinese = false)
    {
        _engine.Execute(Resource("Radio.Tests.FakePage.js"));
        _engine.Execute(PageWords.Script(isChinese));

        if (isToolkitFirst) _engine.Execute(Resource("ExiledToolkit.api.js"));

        _engine.Execute(Resource("Radio.page.js"));
        Hear();
    }

    /// <summary>Every command the script sent the plugin, its ready message and diag notes aside.</summary>
    public List<PageCommand> Sent { get; } = new();

    public List<string> Notes { get; } = new();

    public int Readies { get; private set; }

    /// <summary>The Toolkit's page API arriving after Radio's script.</summary>
    public void LoadToolkit() => Run(Resource("ExiledToolkit.api.js"));

    public void Run(string statements)
    {
        _engine.Execute(statements);
        Hear();
    }

    public string Text(string expression) => _engine.Evaluate($"String({expression})").AsString();

    public bool Is(string expression) => _engine.Evaluate($"!!({expression})").AsBoolean();

    public int Count(string expression) => (int)_engine.Evaluate($"({expression}).length").AsNumber();

    /// <summary>One run of the Toolkit's 250 ms page check, which hands the record player screen to the script.</summary>
    public void Poll() => Run("poll()");

    public void OpenMusic(bool hasObserver = false) => Run($"openMusic({(hasObserver ? "true" : "false")})");

    public void CloseMusic() => Run("closeMusic()");

    public void Click(string expression) => Run($"click({expression})");

    /// <summary>The player switches the screen to the Radio tab.</summary>
    public void OpenRadio() => Click("act('mode', 'radio')");

    public void Now(NowView now) => Call($"now({RadioView.Now(now)})");

    public void List(ListView list) => Call($"list({RadioView.List(list)})");

    public void Favorites(params string[] ids) => Call($"favorites({System.Text.Json.JsonSerializer.Serialize(ids)})");

    public void Pasted(string text) => Call($"pasted({JsText.Literal(text)})");

    /// <summary>The plugin swaps the words, as after the player changes the game's language.</summary>
    public void Words(bool isChinese) => Run(PageWords.Script(isChinese));

    private void Call(string expression) => Run("window.__slradio." + expression);

    private void Hear()
    {
        var count = (int)_engine.Evaluate("posted.length").AsNumber();
        var messages = Enumerable.Range(0, count).Select(i => _engine.Evaluate($"posted[{i}]").AsString()).ToList();

        _engine.Execute("posted.length = 0");

        foreach (var message in messages)
        {
            switch (PageCommand.Parse(message) ?? throw new InvalidOperationException("the page sent a message the plugin cannot read: " + message))
            {
                case PageCommand.Ready:
                    Readies++;
                    break;
                case PageCommand.Note note:
                    Notes.Add(note.Text);
                    break;
                case var command:
                    Sent.Add(command);
                    break;
            }
        }
    }

    private static string Resource(string name)
    {
        using var stream = typeof(PageScript).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException(name + " is not embedded in the test assembly");
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}
