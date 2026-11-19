# Radio

Adds a Radio tab to the record player. Listen to internet radio stations from around the world, Chinese stations included, paste a stream link, or play the songs in your own music folder. The radio plays instead of the game's music.

Open a record player the way you normally do. The music screen now has two tabs at the top. The Records tab is the game's list, and the Radio tab is the new one. Pick a station and it plays through the record player, like a record. It needs power like a record does, it follows the game's music volume, and listening to it gives the same morale a record gives.

## Features

- **Thousands of stations**: the station lists come from [Radio Browser](https://www.radio-browser.info), a free list of about 50,000 internet radio stations kept by its listeners. The Popular list has the stations people play most, the China list has stations in China, and the Chinese list has stations in Mandarin and Cantonese from anywhere.
- **Search**: type a station name and press Search to look for it on Radio Browser.
- **Favorites**: press the star next to a station to keep it on the Favorites tab. Favorites are kept in a file of their own, so every save shares them.
- **Paste a link**: paste a stream link into the Stream link box and press Play. It plays direct stream links and .pls, .m3u and .m3u8 playlists, including the HLS streams many Chinese stations use, and links to song files.
- **My music**: plays the songs in a folder on your PC, subfolders included. By default that is `BepInEx\Radio Music`, made the first time you open the My music tab. Click a song to play it. The songs after it play in order and go round to the first one at the end. The Next button skips to the next song. It plays MP3, M4A, AAC, WMA, WAV and FLAC files, and the sound of MP4 videos.
- **Now playing**: the bar at the bottom shows what plays, the song title when the station sends one, and a volume slider for the radio alone. The game's music volume turns it down on top of that.
- **Plays instead of the game's music**: while the radio plays, the game's music stays off. When you stop it, the game's music comes back.
- **Like a record**: the radio plays on the record player you picked it on, and that record player is switched on and uses power the way it does for a record. If the power goes out, the radio stops. Playing a record stops the radio, and so does stopping the record player.
- **Morale**: a song from your music folder gives morale when it plays to the end, the same as a record. A station never ends, so it gives that morale after you listen for a while. You set how long with `StreamMoraleMinutes`, 3 minutes by default. The game gives music morale only once a day, and the radio counts toward that same once a day.
- **Keeps playing when you go out**: by default the radio keeps playing while you explore. Turn off `PlayWhileExploring` and it pauses while you're away and comes back on when you get home.
- **Lost signal**: if a station drops, the radio tries again by itself and says so on the status line. A station on a slow connection that keeps running out waits a little longer each time, so it stops less often.
- **UI size**: Exiled's Toolkit's UI size setting draws the Radio tab bigger or smaller. Radio also has its own `UiScale` setting under Interface. At 0, the default, it follows the Toolkit's.

There are no keys. The new buttons are the Records and Radio tabs, the list tabs, Search, the star on each station, Paste, Play, Stop, Next, the volume slider, and Open folder and Scan again on the My music tab.

Works in English and Chinese. The tab, the buttons and the messages follow the game's language, and the settings file describes every setting in English and Simplified Chinese. Station names are shown the way the station wrote them. The log is in English.

Radio does not edit game files and puts nothing in your save. You can remove it at any time.

## Known issues and limits

- The station lists, search and Radio Browser stations need an internet connection that can reach radio-browser.info. If it can't be reached, the lists stay empty, but pasted links and the My music tab still work.
- Some stations are far away or slow to reach. From China, many stations outside China may be slow or not play, and some Chinese stations may be slow from other countries. Try another station, or one closer to you.
- Stations that send Ogg or Opus are left off the lists, because most Windows PCs can't play them. A pasted Ogg or Opus link is still tried, and plays if Windows on your PC can play that format. Stations that send AC-3 can't be played.
- Some stations send no song titles. Then the bar shows the station's name only.
- Some stations play silence or send a web page instead of sound. The status line says when a link isn't a radio stream.
- Quitting to the main menu stops the radio. It isn't saved, so it is off when you load a game.
- The radio uses the decoders that come with Windows. On an N edition of Windows, install Microsoft's Media Feature Pack first.
- Playing a station from the lists tells Radio Browser it was played. That is how the Popular list is counted.

## Requirements

- Survival Log 1.1.21271. It is built for this version, so a game update can break it until the mod is updated.
- Windows 10 or 11.
- The [BepInEx Pack for Survival Log](https://www.nexusmods.com/survivallog/mods/12), the BepInEx 6 (IL2CPP) build for the game, version `6.0.0-be.788`.
- [Exiled's Toolkit](https://github.com/AnExiledDev/SurvivalLog-ExiledToolkit) 0.19.0 or newer. Radio does not load without it. It adds the Radio tab to the music screen, shows its messages, and applies settings changed on the Mod Manager's Mods screen without restarting the game.
- Optional: [Mod Manager](https://www.nexusmods.com/survivallog/mods/73). With it, you can change every setting in game on its Mods screen and see what each one does, without opening the config file.

## Install

1. Install the [BepInEx Pack for Survival Log](https://www.nexusmods.com/survivallog/mods/12) (if no other mods were installed before, start the game once so BepInEx finishes setup, then quit).
2. Install Exiled's Toolkit. Download `ExiledToolkit-x.y.z.zip` from [its latest release](https://github.com/AnExiledDev/SurvivalLog-ExiledToolkit/releases/latest) or from [Nexus Mods](https://www.nexusmods.com/survivallog/mods/61), and extract it into the game folder (the folder with `SurvivalLog.exe`).
3. Download `Radio-x.y.z.zip` from the [latest release](https://github.com/AnExiledDev/SurvivalLog-Radio/releases/latest).
4. Extract the zip into the game folder (the folder with `SurvivalLog.exe`). It holds a `BepInEx` folder, so the DLL lands in the right place. When asked, merge the folders. The result should be:
   `C:\Program Files (x86)\Steam\steamapps\common\Survival Log\BepInEx\plugins\Radio.dll`
5. Start the game, open a record player, and press Radio.

## Uninstall

Delete `Radio.dll` from the `BepInEx\plugins` folder. Radio puts nothing in your save, so there is nothing to do first. For a clean slate, also delete `BepInEx\config\com.dev.survivallog.radio.cfg`, your favorites in `BepInEx\config\radio-favorites.json`, and `BepInEx\radio.log`. Radio never deletes your songs, so the `BepInEx\Radio Music` folder is yours to keep or delete.

## Configuration

Settings live in `BepInEx\config\com.dev.survivallog.radio.cfg`, created the first time the game starts with the mod. Change them in game on the Mod Manager's Mods screen, or edit the file while the game is closed. Changed in game, every setting takes effect at once. The volume slider on the Radio tab changes `Volume` too.

See [CONFIG.md](CONFIG.md) for every setting.

## Troubleshooting

Built against BepInEx `6.0.0-be.788`.

Check `BepInEx\LogOutput.log` for a line starting `===== Radio 0.1.0 started`. If a game hook could not be set up, a line starting `could not hook` follows it, and the Mod Manager shows the mod red.

When a station stops for good, the log has a line with its name, `stopped:` and the reason. `Couldn't load the station list` means Radio Browser couldn't be reached. `The radio can't play into the game's music` means the sound couldn't be set up, and the radio stays off.

Turn on `LogToFile` to also write everything to `BepInEx\radio.log`, which survives restarts, with more detail on each station: the links it tried and each time it lost the signal.

## Feedback and bug reports

Found a bug, have an idea, or something feels off? Come say hi in the [unofficial modding Discord for Survival Log](https://discord.gg/pDrQtqbZgb). When reporting a bug, attach `BepInEx\LogOutput.log`, and `BepInEx\radio.log` if you turned on `LogToFile`. If a station won't play, include its name or link.

To reach me directly, message `exiled.dev` on Discord. You can find me in the [unofficial modding Discord](https://discord.gg/pDrQtqbZgb) and the [official Survival Log Discord](https://discord.gg/MZGg4gQAUe). I'm also on X as [@AnExiledDev](https://x.com/AnExiledDev).

A Survival Log wiki with detailed documentation for all of my mods is coming soon.

## Build

This is a BepInEx 6 IL2CPP plugin. It compiles against BepInEx, the game's IL2CPP interop assemblies and Exiled's Toolkit, so a game install with BepInEx set up and started once is required. Those assemblies are not part of this repo. The .NET 8 SDK is required.

```
dotnet build src/Radio -c Release -p:GameDir="C:\path\to\Survival Log" -p:ToolkitDll="C:\path\to\ExiledToolkit.dll"
```

`GameDir` is the game folder and `ToolkitDll` is a built `ExiledToolkit.dll` (0.19.0 or newer). The defaults in the project file are local paths you will almost certainly need to override. The output DLL is at `src\Radio\bin\Release\Radio.dll`.

The unit tests cover the parts that need nothing from the game: reading stream links, playlists, HLS, MPEG transport streams, ICY song titles and audio frames, the station lists, the music folder, the sound buffer, the radio's rules for power, records, trips and morale, and the page script that draws the Radio tab:

```
dotnet test tests/Radio.Tests
```

## License

MIT. Copyright (c) 2026 AnExiledDev. See [LICENSE](LICENSE).
