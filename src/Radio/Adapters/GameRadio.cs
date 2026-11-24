using System;
using System.Collections.Generic;
using ExiledToolkit.Game;
using GameCore.HotUpdate;
using GameCore.HotUpdate.Battle.Logic;
using Radio.Core;

namespace Radio.Adapters;

/// <summary>
/// The record player and the game music, through the same calls a record uses. Each call fails soft: if a game update
/// breaks one, the radio loses that call and logs it once, and the game carries on.
/// </summary>
internal sealed class GameRadio : IRadioGame
{
    private const int MaxReportedFailures = 20;

    private readonly Action<string> _log;
    private readonly HashSet<string> _reported = new();

    public GameRadio(Action<string> log) => _log = log;

    public string? SaveKey => SaveSlot.CurrentKey();

    public bool IsInGame
    {
        get
        {
            try
            {
                return World()?._AgentManager != null;
            }
            catch (Exception ex)
            {
                Report("checking for a loaded game", ex);
                return false;
            }
        }
    }

    public bool IsPowerOff()
    {
        try
        {
            return Power() is not { } power || power.IsPowerOff();
        }
        catch (Exception ex)
        {
            Report("checking the power", ex);
            return true;
        }
    }

    public bool SwitchOn(long playerId)
    {
        try
        {
            var power = Power();

            if (power == null || !IsRecordPlayer(playerId)) return false;

            // What a record does when it starts: every other record player stops, then this one draws power.
            MusicPlaybackTools.StopAllPlayback(playerId);
            power.SetConsumerSwitch(playerId, true);

            return power.IsConsumerOn(playerId);
        }
        catch (Exception ex)
        {
            Report("switching the record player on", ex);
            return false;
        }
    }

    public void SwitchOff(long playerId)
    {
        try
        {
            Power()?.SetConsumerSwitch(playerId, false);
        }
        catch (Exception ex)
        {
            Report("switching the record player off", ex);
        }
    }

    public void StopGameMusic()
    {
        try
        {
            GameSingleton.Existing<FmodManager>()?.StopBgMusic();
        }
        catch (Exception ex)
        {
            Report("stopping the game music", ex);
        }
    }

    public void PlayGameMusic(HeldMusic music)
    {
        try
        {
            var fmod = GameSingleton.Existing<FmodManager>();

            switch (music)
            {
                case HeldMusic.Home:
                    MusicPlaybackTools.RestoreChapterBgMusic(MusicPlaybackTools.CHAPTER_FADE_IN_SECONDS);
                    break;
                case HeldMusic.Play play:
                    fmod?.PlayBgMusic(play.Path);
                    break;
                case HeldMusic.FadeIn fade:
                    fmod?.PlayBgMusicFadeInTo(fade.Path, fade.Seconds, fade.Volume);
                    break;
            }
        }
        catch (Exception ex)
        {
            Report("starting the game music", ex);
        }
    }

    public void GrantMorale()
    {
        try
        {
            MusicPlaybackTools.GrantTrackFinishMorale();
        }
        catch (Exception ex)
        {
            Report("giving morale", ex);
        }
    }

    private static BattleLogicWorld? World() => GameSingleton.Existing<BattleLogicWorld>();

    private static PowerManager? Power() => World()?._PowerManager;

    private static bool IsRecordPlayer(long id)
    {
        var furniture = World()?._AgentManager?.GetAgent(id)?.TryCast<Furniture>();

        return furniture != null && AgentTools.GetAgentComponent<MusicPlayerComponent>(furniture) != null;
    }

    private void Report(string doing, Exception ex)
    {
        if (_reported.Count >= MaxReportedFailures || !_reported.Add(doing + ": " + ex.GetType().FullName + ": " + ex.Message)) return;

        _log($"failed {doing} (each failure is logged once): {ex}");
    }
}
