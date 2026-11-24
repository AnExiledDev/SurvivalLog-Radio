using System;
using System.Collections.Generic;
using ExiledToolkit.Kit;
using Radio.Core;
using UnityEngine;

namespace Radio.Adapters;

/// <summary>What the radio does each frame on the game thread, and when the game closes.</summary>
internal sealed class RadioLoop
{
    private const int MaxReportedFailures = 20;

    private static readonly TimeSpan SaveWaitOnQuit = TimeSpan.FromSeconds(1);

    private readonly RadioSession _session;
    private readonly RadioPage _page;
    private readonly FmodOutput _output;
    private readonly RadioFiles _files;
    private readonly SavedVolume _volume;
    private readonly Action<string> _log;
    private readonly HashSet<string> _reported = new();
    private readonly Action _checkPage;
    private readonly Action _updateSession;
    private readonly Action _startSound;
    private readonly Action _reportSaveProblem;
    private readonly Action _saveVolume;

    /// <summary>The Toolkit's clock counts from system start, and the session's timers are floats, so they count from the mod's start.</summary>
    private readonly double _startedAt = Clock.Now;

    private double _now;

    public RadioLoop(RadioSession session, RadioPage page, FmodOutput output, RadioFiles files, SavedVolume volume, Action<string> log)
    {
        _session = session;
        _page = page;
        _output = output;
        _files = files;
        _volume = volume;
        _log = log;

        // Made once, so a frame allocates nothing.
        _checkPage = () => _page.KeepInstalled(_now);
        _updateSession = () => _session.Update((float)(_now - _startedAt));
        _startSound = StartSound;
        _reportSaveProblem = ReportSaveProblem;
        _saveVolume = () => _volume.SaveIfDue(_now);
    }

    /// <summary>A message from page.js. The Toolkit calls this on the game thread.</summary>
    public void OnPageMessage(string message)
    {
        if (PageCommand.Parse(message) is { } command) _session.Handle(command);
    }

    public void Update()
    {
        _now = Clock.Now;

        Guard("checking the page", _checkPage);
        Guard("updating the radio", _updateSession);
        Guard("starting the sound", _startSound);
        Guard("saving the radio's files", _reportSaveProblem);
        Guard("saving the volume", _saveVolume);
    }

    public void Quit()
    {
        _session.Shutdown();
        _volume.SaveIfDue(double.PositiveInfinity);
        _files.Finish(SaveWaitOnQuit);
    }

    private void StartSound()
    {
        _output.Update();

        if (_output.TakeFailure()) _session.OnSoundFailed();
    }

    private void ReportSaveProblem()
    {
        if (_files.TakeSaveProblem() is { } problem) _log("Couldn't save a Radio file in BepInEx\\config: " + problem);
    }

    /// <summary>Each step runs on its own, so one that breaks after a game update leaves the rest working.</summary>
    private void Guard(string doing, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            Report(doing, ex);
        }
    }

    private void Report(string doing, Exception ex)
    {
        if (_reported.Count >= MaxReportedFailures || !_reported.Add(doing + ": " + ex.GetType().FullName + ": " + ex.Message)) return;

        _log($"failed {doing} (each failure is logged once): {ex}");

        if (_reported.Count == MaxReportedFailures) _log($"{MaxReportedFailures} failures logged; later ones are not");
    }
}

/// <summary>The Unity object that runs the radio's loop each frame.</summary>
public sealed class RadioTicker : MonoBehaviour
{
    internal static RadioLoop? Loop;

    public RadioTicker(IntPtr pointer) : base(pointer)
    {
    }

    private void Update() => Loop?.Update();

    private void OnApplicationQuit()
    {
        try
        {
            Loop?.Quit();
        }
        catch (Exception)
        {
            // The game is closing; there is nowhere left to report this.
        }
    }
}
