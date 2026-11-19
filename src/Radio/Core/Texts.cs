using System.Collections.Generic;
using ExiledToolkit.Kit;

namespace Radio.Core;

/// <summary>The words the mod puts on screen: toasts, the status line, and the page's own labels.</summary>
public static class Texts
{
    private const string Log = "BepInEx/LogOutput.log";

    public static readonly Phrase ToastSource = new("Radio", "收音机");

    public static readonly Phrase NoGame = new("Load a game first.", "请先载入游戏。");
    public static readonly Phrase NoPower = new("The radio needs power, like a record.", "收音机和唱片一样需要电。");
    public static readonly Phrase PowerCut = new("The power went out, so the radio stopped.", "停电了，收音机已停止。");
    public static readonly Phrase PlayerGone = new("The record player is gone, so the radio stopped.", "唱片机不见了，收音机已停止。");
    public static readonly Phrase NotALink = new("That isn't a web link. Stream links start with http:// or https://.", "这不是网址。电台链接以 http:// 或 https:// 开头。");
    public static readonly Phrase ClipboardEmpty = new("There is no text to paste.", "剪贴板里没有文字。");
    public static readonly Phrase FolderFailed = new("Couldn't open the music folder: {0}", "无法打开音乐文件夹：{0}");
    public static readonly Phrase FavoritesUnreadable = new("Your favorites file couldn't be read, so stars won't be saved. See " + Log + ".", "无法读取收藏文件，收藏不会被保存。详见 " + Log + "。");
    public static readonly Phrase Failed = new("Radio failed, see " + Log + ".", "收音机出错，详见 " + Log + "。");
    public static readonly Phrase StoppedBecause = new("{0}: {1}", "{0}：{1}");

    public static readonly Phrase Connecting = new("Connecting...", "正在连接...");
    public static readonly Phrase Playing = new("Playing", "正在播放");
    public static readonly Phrase Buffering = new("Waiting for the signal...", "等待信号...");
    public static readonly Phrase Retrying = new("Lost the signal. Trying again in {0} seconds.", "信号中断，{0} 秒后重试。");
    public static readonly Phrase CannotConnect = new("Can't connect to this station.", "无法连接这个电台。");
    public static readonly Phrase NotAStream = new("This link isn't a radio stream.", "这个链接不是电台。");
    public static readonly Phrase Unsupported = new("This station sends {0}, which the radio can't play.", "这个电台的格式是 {0}，收音机无法播放。");
    public static readonly Phrase Refused = new("The station won't play right now ({0}).", "这个电台现在无法播放（{0}）。");
    public static readonly Phrase NoSongs = new("None of the songs in the music folder could be played.", "音乐文件夹里的歌都无法播放。");
    public static readonly Phrase Ended = new("The recording ended.", "录音已播完。");
    public static readonly Phrase Away = new("Paused while you're away from home.", "离家时暂停。");

    public static readonly Phrase MyMusic = new("My music", "我的音乐");

    /// <summary>The status line for a reader that gave up, by why.</summary>
    public static Phrase For(Trouble why) => why switch
    {
        Trouble.NotAStream => NotAStream,
        Trouble.Unsupported => Unsupported,
        Trouble.Refused => Refused,
        Trouble.NoSongs => NoSongs,
        _ => CannotConnect,
    };

    /// <summary>The words page.js draws the Radio tab with, by the key it looks them up by.</summary>
    public static readonly IReadOnlyList<(string Key, Phrase Phrase)> Page = new (string, Phrase)[]
    {
        ("tabRecords", new("Records", "唱片")),
        ("tabRadio", new("Radio", "收音机")),
        ("popular", new("Popular", "热门")),
        ("china", new("China", "中国")),
        ("chinese", new("Chinese", "中文")),
        ("favorites", new("Favorites", "收藏")),
        ("music", MyMusic),
        ("search", new("Search", "搜索")),
        ("searchHint", new("Station name", "电台名称")),
        ("linkHint", new("Stream link", "电台链接")),
        ("paste", new("Paste", "粘贴")),
        ("play", new("Play", "播放")),
        ("stop", new("Stop", "停止")),
        ("next", new("Next", "下一首")),
        ("volume", new("Volume", "音量")),
        ("off", new("The radio is off.", "收音机已关闭。")),
        ("star", new("Add to favorites", "加入收藏")),
        ("unstar", new("Remove from favorites", "取消收藏")),
        ("playingTag", new("Playing", "正在播放")),
        ("radioHere", new("The radio is playing on this record player.", "这台唱片机正在播放收音机。")),
        ("elsewhere", new("Playing on another record player. Pick a station to play it here.", "正在另一台唱片机上播放。选一个电台就能在这里播放。")),
        ("loading", new("Loading...", "加载中...")),
        ("listFailed", new("Couldn't load the station list. Check your internet connection and try again.", "无法加载电台列表。请检查网络后重试。")),
        ("noStations", new("No stations found.", "没有找到电台。")),
        ("typeToSearch", new("Type a station name and press Search.", "输入电台名称后点击「搜索」。")),
        ("noFavorites", new("Press the star next to a station to keep it here.", "点击电台旁的星标，就能在这里找到它。")),
        ("scanning", new("Looking for songs...", "正在查找歌曲...")),
        ("noSongsIn", new("No songs in {0}. Put MP3, M4A, AAC, WMA, WAV or FLAC files there, then press Scan again.", "{0} 里没有歌曲。放入 MP3、M4A、AAC、WMA、WAV 或 FLAC 文件后点击「重新查找」。")),
        ("songsIn", new("{0} songs in {1}", "{1} 里有 {0} 首歌")),
        ("openFolder", new("Open folder", "打开文件夹")),
        ("scanAgain", new("Scan again", "重新查找")),
        ("connecting", Connecting),
        ("playing", Playing),
        ("buffering", Buffering),
        ("retrying", Retrying),
        ("cannotConnect", CannotConnect),
        ("notAStream", NotAStream),
        ("unsupported", Unsupported),
        ("refused", Refused),
        ("noSongs", NoSongs),
        ("ended", Ended),
        ("away", Away),
    };
}
