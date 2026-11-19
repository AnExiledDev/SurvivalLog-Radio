Name: Radio
Summary: Adds a Radio tab to the record player. Play internet radio from around the world, Chinese stations included, paste any stream link, or play the songs in your own music folder. The radio plays instead of the game's music, follows the music volume, needs power like a record, gives the same morale as a record, and adds nothing to your save.

Description (BBCode):
[center][img]https://sldb.core-directive.com/media/save/1-save-safe.png[/img][/center]

Radio adds a Radio tab to the record player's music screen. Pick from thousands of internet radio stations from around the world, Chinese stations included, paste a stream link, or play the songs in your own music folder. The radio plays through the record player instead of the game's music.

[size=5]Main features[/size]
[list]
[*][b]Thousands of stations[/b]: the lists come from Radio Browser, a free list of about 50,000 internet radio stations. [b]Popular[/b] has the stations people play most, [b]China[/b] has stations in China, and [b]Chinese[/b] has stations in Mandarin and Cantonese from anywhere.[/*]
[*][b]Search[/b]: type a station name and press Search to look for it on Radio Browser.[/*]
[*][b]Favorites[/b]: press the star next to a station to keep it on the Favorites tab. Every save shares your favorites.[/*]
[*][b]Paste a link[/b]: paste a stream link and press Play. It plays direct stream links and .pls, .m3u and .m3u8 playlists, including the HLS streams many Chinese stations use, and links to song files.[/*]
[*][b]My music[/b]: plays the MP3, M4A, AAC, WMA, WAV and FLAC files in a folder on your PC, subfolders included. Click a song and the ones after it play in order. Next skips a song.[/*]
[*][b]Plays instead of the game's music[/b]: while the radio plays, the game's music stays off. Stop it and the game's music comes back.[/*]
[*][b]Works like a record[/b]: the record player you picked it on is switched on and uses power, the same as for a record. If the power goes out, the radio stops. Playing a record stops it too.[/*]
[*][b]Morale[/b]: a song from your music folder gives morale when it plays to the end, like a record. A station gives the same morale after you listen for a while. You set how long, 3 minutes by default. The game gives music morale once a day, and the radio counts toward that same once a day.[/*]
[*][b]Keeps playing when you go out[/b]: by default the radio keeps playing while you explore. A setting pauses it while you're away instead.[/*]
[*][b]Its own volume[/b]: the volume slider on the Radio tab sets the radio's volume, and the game's music volume turns it down on top of that.[/*]
[*][b]Lost signal[/b]: if a station drops, the radio tries again by itself and says so on the status line.[/*]
[/list]
Radio does not edit game files and puts nothing in your save. You can remove it at any time.

Works in English and Chinese. The tab, the buttons and the messages follow the game's language.

[size=5]Known issues[/size]
[list]
[*]The station lists and search need an internet connection that can reach radio-browser.info. If it can't be reached, pasted links and My music still work.[/*]
[*]Stations far away can be slow. From China, many stations outside China may be slow or not play. Try another station.[/*]
[*]Stations that send Ogg or Opus are left off the lists, because most Windows PCs can't play them. Stations that send AC-3 can't be played.[/*]
[*]Quitting to the main menu stops the radio, and it is off when you load a game.[/*]
[*]On an N edition of Windows, install Microsoft's Media Feature Pack first. Radio uses the decoders that come with Windows.[/*]
[*]Playing a station from the lists tells Radio Browser it was played. That is how the Popular list is counted.[/*]
[/list]

[center][img]https://sldb.core-directive.com/img/mods-banner.jpg[/img]
[size=4][url=https://www.nexusmods.com/survivallog/mods/73]Get Mod Manager[/url] to set up all your mods in game  |  [url=https://sldb.core-directive.com/en/]Open the Survival Log wiki[/url] for items, recipes and maps[/size][/center]

[size=5]Requirements[/size]
[list]
[*]Survival Log 1.1.21271. A game update can break it until the mod is updated.[/*]
[*]Windows 10 or 11.[/*]
[*][url=https://www.nexusmods.com/survivallog/mods/12]BepInEx for Survival Log[/url]. Install it first.[/*]
[*][url=https://www.nexusmods.com/survivallog/mods/61]Exiled's Toolkit[/url] 0.19.0 or newer. Radio does not load without it. It also lets settings you change on the Mod Manager's Mods screen apply without a restart.[/*]
[*]Optional: [url=https://www.nexusmods.com/survivallog/mods/73]Mod Manager[/url]. With it, you can change every setting in game on its Mods screen and see what each one does, without opening the config file.[/*]
[/list]

[size=5]Installation[/size]
[list=1]
[*]Install [url=https://www.nexusmods.com/survivallog/mods/12]BepInEx[/url] [i](if no other mods were installed before, start the game once so BepInEx finishes setup, then quit)[/i].[/*]
[*]Install [url=https://www.nexusmods.com/survivallog/mods/61]Exiled's Toolkit[/url]. Download its zip and extract it into the game folder.[/*]
[*]Download the zip from the Files tab.[/*]
[*]Extract it into the game folder (the folder with [font=Courier New]SurvivalLog.exe[/font]). The zip holds a BepInEx folder, so the DLL lands in the right place. When asked, merge the folders. The result should be:
[code]C:\Program Files (x86)\Steam\steamapps\common\Survival Log\BepInEx\plugins\Radio.dll[/code][/*]
[*]Start the game, open a record player, and press Radio.[/*]
[/list]

[size=5]Uninstall[/size]
Delete [font=Courier New]Radio.dll[/font] from the [font=Courier New]BepInEx\plugins[/font] folder. Your songs in [font=Courier New]BepInEx\Radio Music[/font] are never deleted by the mod.

[size=5]Configuration[/size]
To change settings, edit [font=Courier New]BepInEx\config\com.dev.survivallog.radio.cfg[/font] while the game is closed, or do it in game on the Mod Manager's Mods screen, where changes take effect at once (see [url=https://github.com/AnExiledDev/SurvivalLog-Radio/blob/main/CONFIG.md]CONFIG.md[/url] for every setting). You can set the volume, whether the radio plays while you explore, your music folder, the morale, and how long a station takes to give it.

[size=5]Feedback[/size]
Found a bug or have an idea? Come say hi in the [url=https://discord.gg/pDrQtqbZgb]unofficial modding Discord for Survival Log[/url], or post in this page's Posts tab. For bugs, please include [font=Courier New]BepInEx\LogOutput.log[/font], and [font=Courier New]BepInEx\radio.log[/font] if you turned on [font=Courier New]LogToFile[/font]. If a station won't play, include its name or link.
To reach me directly, message [b]exiled.dev[/b] on Discord. You can find me in the [url=https://discord.gg/pDrQtqbZgb]unofficial modding Discord[/url] and the [url=https://discord.gg/MZGg4gQAUe]official Survival Log Discord[/url]. I'm also on X as [url=https://x.com/AnExiledDev]@AnExiledDev[/url].

[size=5]Source and license[/size]
Open source under MIT: [url=https://github.com/AnExiledDev/SurvivalLog-Radio]github.com/AnExiledDev/SurvivalLog-Radio[/url]

[line]
[size=5]简体中文[/size]

Radio 在唱片机的音乐界面加入「收音机」标签页。可以从全世界成千上万个网络电台里挑选，中国电台也有；也可以粘贴电台链接，或播放你自己音乐文件夹里的歌。收音机通过唱片机播放，代替游戏音乐。

[size=5]主要功能[/size]
[list]
[*][b]成千上万个电台[/b]：电台列表来自 Radio Browser，一个收录了约 50,000 个网络电台的免费目录。「热门」是大家最常听的电台，「中国」是中国的电台，「中文」是世界各地的普通话和粤语电台。[/*]
[*][b]搜索[/b]：输入电台名称，点击「搜索」，就能在 Radio Browser 上找。[/*]
[*][b]收藏[/b]：点击电台旁的星标，它就会出现在「收藏」标签页。所有存档共用收藏。[/*]
[*][b]粘贴链接[/b]：粘贴电台链接后点击「播放」。支持直接的电台链接，.pls、.m3u 和 .m3u8 播放列表（包括很多中国电台用的 HLS 直播流），以及歌曲文件的链接。[/*]
[*][b]我的音乐[/b]：播放电脑上某个文件夹里的 MP3、M4A、AAC、WMA、WAV 和 FLAC 文件，包括子文件夹。点一首歌，后面的歌会按顺序接着播放。「下一首」可以跳过当前这首。[/*]
[*][b]代替游戏音乐[/b]：收音机播放时，游戏音乐不会响。停止收音机后，游戏音乐会回来。[/*]
[*][b]和唱片一样[/b]：你选电台的那台唱片机会打开并耗电，和放唱片时一样。停电时收音机会停止。放唱片也会让收音机停止。[/*]
[*][b]心态[/b]：音乐文件夹里的歌完整播放后会提升心态，和唱片一样。电台听一段时间后也会提升同样的心态，时长可以自己设置，默认 3 分钟。游戏每天只给一次音乐带来的心态，收音机和唱片共用这一次。[/*]
[*][b]出门也能听[/b]：默认出门探索时收音机继续播放。也可以在设置里改成离家时暂停。[/*]
[*][b]单独的音量[/b]：「收音机」标签页上的音量滑块调收音机的音量，游戏的音乐音量会在此基础上再调低。[/*]
[*][b]信号中断[/b]：电台断线时，收音机会自动重连，并在状态栏里显示。[/*]
[/list]
Radio 不会修改游戏文件，也不会往存档里添加任何内容，随时可以移除。

支持英文和中文。标签页、按钮和提示会跟随游戏语言。

[size=5]已知问题[/size]
[list]
[*]电台列表和搜索需要能访问 radio-browser.info 的网络。访问不了时，粘贴的链接和「我的音乐」仍然可以用。[/*]
[*]距离远的电台可能会很慢。在中国，很多国外电台可能会卡顿或无法播放，换一个电台试试。[/*]
[*]使用 Ogg 或 Opus 格式的电台不会出现在列表里，因为大多数 Windows 电脑播放不了。使用 AC-3 格式的电台无法播放。[/*]
[*]退回主菜单时收音机会停止，载入游戏后是关着的。[/*]
[*]如果你用的是 Windows N 版，请先安装微软的 Media Feature Pack（媒体功能包）。Radio 使用 Windows 自带的解码器。[/*]
[*]从列表播放电台时，会告诉 Radio Browser 这个电台被播放了。「热门」列表就是这样统计出来的。[/*]
[/list]

[url=https://www.nexusmods.com/survivallog/mods/73]获取 Mod Manager[/url]，在游戏里设置所有模组。[url=https://sldb.core-directive.com/zh/]打开 Survival Log 维基[/url]，物品、配方、地图一查就有。

[size=5]前置要求[/size]
[list]
[*]Survival Log 1.1.21271。游戏更新后模组可能暂时失效，直到模组更新为止。[/*]
[*]Windows 10 或 11。[/*]
[*][url=https://www.nexusmods.com/survivallog/mods/12]BepInEx for Survival Log[/url]，请先安装。[/*]
[*][url=https://www.nexusmods.com/survivallog/mods/61]Exiled's Toolkit[/url] 0.19.0 或更新版本。没有它本模组不会加载。有了它，在 Mod Manager 的「模组」界面改动的设置无需重启游戏即可生效。[/*]
[*]可选：[url=https://www.nexusmods.com/survivallog/mods/73]Mod Manager[/url]。装了它，可以在游戏里它的「模组」界面修改每项设置，并看到每项的说明，不用打开配置文件。[/*]
[/list]

[size=5]安装[/size]
[list=1]
[*]安装 [url=https://www.nexusmods.com/survivallog/mods/12]BepInEx[/url] [i]（如果之前没有装过其他模组，先启动一次游戏，让 BepInEx 完成初始化，然后退出）[/i]。[/*]
[*]安装 [url=https://www.nexusmods.com/survivallog/mods/61]Exiled's Toolkit[/url]：下载它的压缩包并解压到游戏目录。[/*]
[*]在「文件」页下载压缩包。[/*]
[*]把压缩包解压到游戏目录（[font=Courier New]SurvivalLog.exe[/font] 所在的文件夹）。压缩包里带有 BepInEx 文件夹，DLL 会自动放到正确位置。如果提示合并文件夹，选择合并。解压后应为：
[code]C:\Program Files (x86)\Steam\steamapps\common\Survival Log\BepInEx\plugins\Radio.dll[/code][/*]
[*]启动游戏，打开唱片机，点击「收音机」。[/*]
[/list]

[size=5]卸载[/size]
从 [font=Courier New]BepInEx\plugins[/font] 文件夹中删除 [font=Courier New]Radio.dll[/font]。模组不会删除 [font=Courier New]BepInEx\Radio Music[/font] 里你的歌。

[size=5]配置[/size]
可以在游戏内 Mod Manager 的「模组」界面修改，改动立即生效。也可以在游戏关闭时编辑 [font=Courier New]BepInEx\config\com.dev.survivallog.radio.cfg[/font]。配置文件中每项设置都有中文说明，完整说明见 [url=https://github.com/AnExiledDev/SurvivalLog-Radio/blob/main/CONFIG.md]CONFIG.md[/url]（英文）。可以设置音量、出门探索时是否继续播放、音乐文件夹、是否提升心态，以及听电台多久后提升心态。

[size=5]反馈[/size]
发现 bug 或有好点子？欢迎来 [url=https://discord.gg/pDrQtqbZgb]Survival Log 非官方模组 Discord[/url] 打个招呼，或在本页的「Posts」标签里发帖。报告 bug 时，请附上 [font=Courier New]BepInEx\LogOutput.log[/font]。如果打开了 [font=Courier New]LogToFile[/font]，也请附上 [font=Courier New]BepInEx\radio.log[/font]。如果某个电台播放不了，请附上它的名称或链接。
想直接联系我，可以在 Discord 上给 [b]exiled.dev[/b] 发消息，我在 [url=https://discord.gg/pDrQtqbZgb]Survival Log 非官方模组 Discord[/url] 和 [url=https://discord.gg/MZGg4gQAUe]Survival Log 官方 Discord[/url] 里。也可以在 X 上找我：[url=https://x.com/AnExiledDev]@AnExiledDev[/url]。

[size=5]源代码与许可[/size]
基于 MIT 许可开源：[url=https://github.com/AnExiledDev/SurvivalLog-Radio]github.com/AnExiledDev/SurvivalLog-Radio[/url]
