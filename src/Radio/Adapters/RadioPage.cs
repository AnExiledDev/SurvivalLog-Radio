using System;
using ExiledToolkit;
using ExiledToolkit.Game;
using ExiledToolkit.Web;
using Radio.Core;

namespace Radio.Adapters;

/// <summary>
/// The mod's half in the game's web UI: page.js, run in the root document the game's pages sit in. It adds the Radio
/// tab to the music screen. The web view can reload at any time and lose the script, so Exiled's Toolkit checks for it
/// every couple of seconds, and the session puts it back when the page says it is missing.
/// </summary>
internal sealed class RadioPage : IRadioPage
{
    private const string ScriptResource = "Radio.page.js";
    private const string Global = "window.__slradio";

    private readonly WebPage _page;
    private readonly WebKeyboard _keyboard = new();
    private readonly Action<string> _log;

    public RadioPage(Action<string> log)
    {
        _log = log;

        // The page's words go with the first call after a load and again when the game's language changes.
        _page = new WebPage(typeof(RadioPage).Assembly, ScriptResource, Global, PageCommand.Prefix + "noscript", log)
        {
            Words = PageWords.Script,
        };
    }

    public bool IsChinese => GameLanguage.IsChinese();

    public void KeepInstalled(double now) => _page.KeepInstalled(now);

    public void Install() => _page.Install();

    public void ForgetWords() => _page.ForgetWords();

    public void SetTyping(bool isTyping)
    {
        if (isTyping) _keyboard.Claim();
        else _keyboard.Release();
    }

    public bool Now(string json) => _page.Call($"now({json})");

    public bool List(string json) => _page.Call($"list({json})");

    public bool Favorites(string json) => _page.Call($"favorites({json})");

    public void Pasted(string text) => _page.Call($"pasted({JsText.Literal(text)})");

    public void Toast(string text, bool isProblem)
    {
        if (string.IsNullOrEmpty(text)) return;

        var request = new ToastRequest(Texts.ToastSource.Pick(IsChinese), text) { Kind = isProblem ? ToastKind.Warning : ToastKind.Info };

        if (Toasts.Show(request).IsNone) _log("a toast was not shown: " + text);
    }
}
