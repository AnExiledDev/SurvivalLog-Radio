# Radio settings

Settings live in `BepInEx\config\com.dev.survivallog.radio.cfg`, created the first time the game starts with the mod. Change them in game on the Mod Manager's Mods screen, or edit the file while the game is closed. Changed in game, every setting takes effect at once. Delete the file to get the defaults back. The file describes each setting in English and Simplified Chinese.

With the defaults the radio plays at full volume, keeps playing while you explore, plays songs from `BepInEx\Radio Music`, and gives morale like a record.

## General

| Setting | Default | What it does |
| --- | --- | --- |
| `Volume` | `100` | How loud the radio plays, from 0 to 100. The game's music volume turns it down on top of this. The slider on the Radio tab changes this setting too. |
| `PlayWhileExploring` | `true` | `true` keeps the radio playing when you go out to explore. `false` pauses it when you leave home and turns it back on when you get home. |
| `MusicFolder` | empty | The folder the My music tab plays songs from, subfolders included. Empty uses `BepInEx\Radio Music`, which is made the first time you open that tab. It plays MP3, M4A, AAC, WMA, WAV and FLAC files, and the sound of MP4 videos, up to 5000 songs. A path like `D:\Music` or `%USERPROFILE%\Music` works. |

## Morale

| Setting | Default | What it does |
| --- | --- | --- |
| `GiveMorale` | `true` | `true` makes listening to the radio at home give morale the way a record does. A song from your music folder gives it when it plays to the end, and a station gives it after `StreamMoraleMinutes` of listening. The game gives music morale only once a day, and the radio counts toward that same once a day. |
| `StreamMoraleMinutes` | `3` | How many minutes you listen to a station before it gives the morale of one record played to the end, from 1 to 60. |

## Interface

| Setting | Default | What it does |
| --- | --- | --- |
| `UiScale` | `0` | How big the Radio tab is drawn, in percent, from 75 to 200. At `0` it follows the UI size set in Exiled's Toolkit (its `UiScale`). |

## Diagnostics

| Setting | Default | What it does |
| --- | --- | --- |
| `LogToFile` | `false` | `true` also writes what Radio does to `BepInEx\radio.log`, which is kept between game starts, with more detail on each station. Turn it on when reporting a problem. |
