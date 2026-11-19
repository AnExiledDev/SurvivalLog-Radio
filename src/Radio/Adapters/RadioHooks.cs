using System;
using System.Collections.Generic;
using ExiledToolkit.Game;
using GameCore.HotUpdate;
using GameCore.HotUpdate.Battle.Logic;
using GameCore.HotUpdate.ReduxUI;
using Radio.Core;
using MapSearcher = Il2CppSystem.Collections.Generic.Dictionary<long, long>;

namespace Radio.Adapters;

/// <summary>
/// Where the game tells the radio what is going on: the music it asks for, records starting and stopping, loads and
/// trips, and the music screen opening and closing. Each hook passes the news to the session and never throws into
/// the game.
/// </summary>
internal static class RadioHooks
{
    private const string MusicConsequence = "the game music may play over the radio";
    private const string ScreenConsequence = "the Radio tab may not show on the music screen";

    private static RadioSession? _session;
    private static MusicHold _hold = new();
    private static PatchKit? _hooks;

    public static void Install(PatchKit hooks, RadioSession session, MusicHold hold)
    {
        _hooks = hooks;
        _session = session;
        _hold = hold;

        var t = typeof(RadioHooks);
        var fmod = typeof(FmodManager);
        var tools = typeof(MusicPlaybackTools);
        var power = typeof(PowerManager);
        var screen = typeof(Reducer_Web_MusicPanel);

        hooks.Patch(t, fmod, nameof(FmodManager.PlayBgMusic), new[] { typeof(string) }, prefix: nameof(BeforeMusicPlays), consequence: MusicConsequence);
        hooks.Patch(t, fmod, nameof(FmodManager.PlayBgMusicFadeInTo), new[] { typeof(string), typeof(float), typeof(float) }, prefix: nameof(BeforeMusicFadesIn), consequence: MusicConsequence);
        hooks.Patch(t, fmod, nameof(FmodManager.PlayBgMusicExplore), new[] { typeof(string) }, prefix: nameof(BeforeTripMusic), consequence: "stopping the radio on a trip may bring back the home music");
        hooks.Patch(t, fmod, nameof(FmodManager.StopBgMusic), Type.EmptyTypes, prefix: nameof(BeforeMusicStops), consequence: "stopping the radio may bring back music the game had stopped");

        hooks.Patch(t, tools, nameof(MusicPlaybackTools.PlayTrack), new[] { typeof(Furniture), typeof(Config_MusicTrack) }, prefix: nameof(BeforeRecordPlays), consequence: "a record may play over the radio");
        hooks.Patch(t, tools, nameof(MusicPlaybackTools.StopPlayback), new[] { typeof(Furniture) }, prefix: nameof(BeforePlayerStops), consequence: "the radio may keep playing after a power cut or the record player's stop button");

        hooks.Patch(t, power, nameof(PowerManager.RemapInstanceIds), new[] { typeof(MapSearcher) }, prefix: nameof(BeforeIdsChange), consequence: "the radio stops when you come home from a trip");
        hooks.Patch(t, power, nameof(PowerManager.RestoreGeneratorShowStates), Type.EmptyTypes, postfix: nameof(AfterPlayersReset), consequence: "the radio may play with the record player off after a load or a trip");

        hooks.Patch(t, screen, nameof(Reducer_Web_MusicPanel.RA_Open), new[] { typeof(Ac_MusicPanel_Open), typeof(State_Web_MusicPanel) }, postfix: nameof(AfterScreenOpens), consequence: ScreenConsequence);
        hooks.Patch(t, screen, nameof(Reducer_Web_MusicPanel.RA_Close), new[] { typeof(Ac_MusicPanel_Close), typeof(State_Web_MusicPanel) }, postfix: nameof(AfterScreenCloses), consequence: ScreenConsequence);
        hooks.Patch(t, screen, nameof(Reducer_Web_MusicPanel.RA_ClearUI), new[] { typeof(Ac_ClearUI), typeof(State_Web_MusicPanel) }, postfix: nameof(AfterScreenCloses), consequence: ScreenConsequence);
        hooks.Patch(t, typeof(Reducer_Web_MainUI), nameof(Reducer_Web_MainUI.RA_Open), new[] { typeof(Ac_MainUI_Open), typeof(State_Web_MainUI) }, postfix: nameof(AfterTitleScreen), consequence: "the radio may keep playing on the title screen");
    }

    /// <summary>False skips the game's call: it asked for music while the radio plays.</summary>
    private static bool BeforeMusicPlays(string __0) => !_hold.OnPlay(__0);

    private static bool BeforeMusicFadesIn(string __0, float __1, float __2) => !_hold.OnFadeIn(__0, __1, __2);

    /// <summary>
    /// Trip music starts quiet and fades in after a while. If the radio stops before then, the quiet is right,
    /// and the fade still comes when the game is ready.
    /// </summary>
    private static void BeforeTripMusic() => _hold.OnStop();

    private static void BeforeMusicStops() => _hold.OnStop();

    private static void BeforeRecordPlays()
    {
        try
        {
            _session?.OnRecordStarting();
        }
        catch (Exception ex)
        {
            _hooks?.Fail("stopping the radio for a record", ex);
        }
    }

    private static void BeforePlayerStops(Furniture __0)
    {
        try
        {
            if (_session == null || __0 == null) return;

            var id = __0.InstanceId;
            var wasOn = GameSingleton.Existing<BattleLogicWorld>()?._PowerManager?.IsConsumerOn(id) == true;
            _session.OnPlayerStopping(id, wasOn);
        }
        catch (Exception ex)
        {
            _hooks?.Fail("stopping the radio with the record player", ex);
        }
    }

    private static void BeforeIdsChange(MapSearcher __0)
    {
        try
        {
            // A trip's map never has the record player, so only the way home matters.
            if (_session == null || __0 == null || GameSingleton.Existing<BattleLogicWorld>()?.GameModeKey == GameMode.Explore) return;

            var newIds = new Dictionary<long, long>();

            foreach (var pair in __0) newIds[pair.Key] = pair.Value;

            _session.OnIdsChanged(id => newIds.TryGetValue(id, out var newId) ? newId : null);
        }
        catch (Exception ex)
        {
            _hooks?.Fail("following the record player to its new id", ex);
        }
    }

    private static void AfterPlayersReset()
    {
        try
        {
            if (_session == null) return;

            var isAtHome = GameSingleton.Existing<BattleLogicWorld>()?.GameModeKey != GameMode.Explore;
            _session.OnPlayersReset(isAtHome);
        }
        catch (Exception ex)
        {
            _hooks?.Fail("putting the radio back after a load", ex);
        }
    }

    private static void AfterScreenOpens(Ac_MusicPanel_Open __0)
    {
        try
        {
            if (__0 != null) _session?.OnScreenOpened(__0.FurnitureId);
        }
        catch (Exception ex)
        {
            _hooks?.Fail("showing the Radio tab", ex);
        }
    }

    private static void AfterScreenCloses()
    {
        try
        {
            _session?.OnScreenClosed();
        }
        catch (Exception ex)
        {
            _hooks?.Fail("closing the Radio tab", ex);
        }
    }

    private static void AfterTitleScreen()
    {
        try
        {
            _session?.OnLeftGame();
        }
        catch (Exception ex)
        {
            _hooks?.Fail("stopping the radio at the title screen", ex);
        }
    }
}
