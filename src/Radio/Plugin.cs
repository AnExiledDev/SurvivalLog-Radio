using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using ExiledToolkit;
using ExiledToolkit.Game;
using ExiledToolkit.Kit;
using ExiledToolkit.Web;
using HarmonyLib;
using Radio.Adapters;
using Radio.Core;

namespace Radio;

[BepInPlugin(Guid, "Radio", Version)]
[BepInDependency(ToolkitGuid, ToolkitVersion)]
public sealed class Plugin : BasePlugin
{
    internal const string Version = "0.1.0";

    private const string Guid = "com.dev.survivallog.radio";

    /// <summary>Exiled's Toolkit: its page tools, toasts, live settings and UI size all come from it.</summary>
    private const string ToolkitGuid = "com.dev.survivallog.exiledtoolkit";
    private const string ToolkitVersion = ">=0.19.0";

    private const string LogFileName = "radio.log";
    private const string FavoritesFileName = "radio-favorites.json";
    private const string DefaultMusicFolder = "Radio Music";

    /// <summary>The name the page script hands its tab to the Toolkit's UI size with.</summary>
    private const string UiSizeName = "Radio";

    public override void Load()
    {
        ModUiSize.Bind(Config, UiSizeName);

        var volume = Config.Bind("General", "Volume", 100, new ConfigDescription(
            "How loud the radio plays, from 0 to 100. 100 by default. The game's music volume turns it down on top of this. The slider on the Radio tab changes this setting too.\n" +
            "收音机的音量，可设为 0 到 100，默认 100。游戏的音乐音量会在此基础上再调低。「收音机」标签页上的滑块也会改这项设置。",
            new AcceptableValueRange<int>(0, 100)));
        var isPlayingWhileExploring = Config.Bind("General", "PlayWhileExploring", true,
            "When true, the radio keeps playing when you go out to explore. When false, it stops when you leave home and comes back on when you get home.\n" +
            "设为 true 时，出门探索时收音机继续播放。设为 false 时，离家时收音机停止，回家后自动重新播放。");
        var musicFolder = Config.Bind("General", "MusicFolder", "",
            "The folder the My music tab plays songs from, subfolders included. Leave it empty to use BepInEx\\Radio Music, which is made the first time you open My music. It plays mp3, m4a, aac, wma, wav, flac and mp4 files, up to 5000 songs.\n" +
            "「我的音乐」标签页播放歌曲的文件夹，包括其中的子文件夹。留空则使用 BepInEx\\Radio Music，第一次打开「我的音乐」时会自动创建。支持 mp3、m4a、aac、wma、wav、flac 和 mp4 文件，最多 5000 首。");
        var isGivingMorale = Config.Bind("Morale", "GiveMorale", true,
            "When true, listening to the radio at home gives morale the way a record does. A song from your music folder gives it when it plays to the end, and a station gives it after StreamMoraleMinutes of listening. Records only give morale once a day, and the radio counts toward that same once a day.\n" +
            "设为 true 时，在家听收音机会像听唱片一样提升心态。音乐文件夹里的歌完整播放后提升，电台在收听 StreamMoraleMinutes 分钟后提升。唱片每天只提升一次心态，收音机和唱片共用这一次。");
        var streamMoraleMinutes = Config.Bind("Morale", "StreamMoraleMinutes", 3.0, new ConfigDescription(
            "How many minutes you listen to a station before it gives the morale of one record played to the end. You can set it from 1 to 60. 3 by default.\n" +
            "收听电台多少分钟后，提升相当于完整播放一张唱片的心态。可设为 1 到 60，默认 3。",
            new AcceptableValueRange<double>(1, 60)));
        var isLoggingToFile = Config.Bind("Diagnostics", "LogToFile", false, new ConfigDescription(
            "When true, what Radio does is also written to BepInEx\\radio.log, which is kept between game starts. Turn it on when reporting a problem.\n" +
            "设为 true 时，收音机模组的操作也会写入 BepInEx\\radio.log，该文件在重启游戏后保留。报告问题时再打开。", null, "ModManager.Advanced"));
        var log = new ModLog(Log, LogFileName);

        // Before the first line, since the file copy is on until the setting says otherwise.
        LiveSettings.Watch(isLoggingToFile, isOn => log.IsFileOn = isOn);

        RadioSettings ReadSettings() => new(
            volume.Value,
            isPlayingWhileExploring.Value,
            MusicFolderOf(musicFolder.Value),
            isGivingMorale.Value,
            streamMoraleMinutes.Value);

        var network = new Network();
        var output = new FmodOutput(log.Write);
        var page = new RadioPage(log.Write);
        var files = new RadioFiles(Path.Combine(Paths.ConfigPath, FavoritesFileName));
        var savedVolume = new SavedVolume(volume);
        var hold = new MusicHold();
        var session = new RadioSession(
            new GameRadio(log.Write),
            page,
            output,
            new PlaybackMaker(network),
            files,
            new RadioBrowser(network.FetchText),
            hold,
            ReadSettings(),
            savedVolume.Keep,
            log.Write,
            log.WriteFileOnly);

        log.Write($"===== Radio {Version} started");

        LiveSettings.Watch(() =>
        {
            var settings = ReadSettings();
            session.UseSettings(settings);
            log.WriteFileOnly($"volume {settings.Volume}, play while exploring {settings.PlayWhileExploring}, music folder {settings.MusicFolder}, morale {settings.GiveMorale} every {settings.StreamMoraleMinutes} min");
        }, volume, isPlayingWhileExploring, musicFolder, isGivingMorale, streamMoraleMinutes);

        RadioHooks.Install(new PatchKit(new Harmony(Guid), log.Write), session, hold);

        var loop = new RadioLoop(session, page, output, files, savedVolume, log.Write);
        PageInbox.Listen(PageCommand.Prefix, loop.OnPageMessage);
        RadioTicker.Loop = loop;
        AddComponent<RadioTicker>();
    }

    /// <summary>An empty setting is the folder next to the mods. Quotes pasted with a path are dropped.</summary>
    private static string MusicFolderOf(string setting)
    {
        var folder = setting.Trim().Trim('"');

        return folder.Length == 0 ? Path.Combine(Paths.BepInExRootPath, DefaultMusicFolder) : Environment.ExpandEnvironmentVariables(folder);
    }
}
