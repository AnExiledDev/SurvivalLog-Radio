extern alias dotnet;

using System;
using System.Runtime.InteropServices;
using System.Threading;
using ExiledToolkit.Game;
using FMODUnity;
using GameCore.HotUpdate;
using Radio.Core;

namespace Radio.Adapters;

/// <summary>
/// Plays the radio through the game's FMOD, on the music bus, so it follows the music slider and the game's ducking.
/// It is one endless FMOD stream made by code: FMOD's stream thread asks for sound and gets it from the ring. Everything
/// but that callback runs on the game thread.
/// </summary>
internal sealed unsafe class FmodOutput : IAudioOut
{
    /// <summary>Frames FMOD asks for at a time: about 40 ms.</summary>
    private const int FramesPerRead = 2048;

    /// <summary>The stream has to have a length, so it is this long and loops forever.</summary>
    private const int LoopSeconds = 10;

    /// <summary>Frames to wait for the music bus after locking it before giving up: about five seconds.</summary>
    private const int MaxWaitFrames = 300;

    private const uint Mode = Fmod.OpenUser | Fmod.Stream | Fmod.Loop | Fmod.TwoD;

    /// <summary>
    /// The ring FMOD's stream thread reads. Static because FMOD calls a plain function, and there is only ever one
    /// radio stream.
    /// </summary>
    private static SampleRing? _playing;

    private readonly Action<string> _log;

    private string? _missing;
    private bool _hasLookedUp;
    private IntPtr _bus;
    private IntPtr _group;
    private IntPtr _sound;
    private IntPtr _channel;
    private bool _isWanted;
    private bool _hasFailed;
    private bool _hasNewFailure;
    private int _waitedFrames;
    private float _volume = 1;

    public FmodOutput(Action<string> log) => _log = log;

    /// <summary>
    /// True once after the sound couldn't start, so the player can hear why. It is asked for on the next frame
    /// instead of told at once, since a failed start can happen in the middle of the radio starting.
    /// </summary>
    public bool TakeFailure()
    {
        if (!_hasNewFailure) return false;

        _hasNewFailure = false;
        return true;
    }

    public void Play(SampleRing ring)
    {
        Volatile.Write(ref _playing, ring);
        _isWanted = true;
        _hasFailed = false;
        _waitedFrames = 0;

        if (_channel == IntPtr.Zero) TryStart();
    }

    public void SetVolume(float volume)
    {
        _volume = volume;

        if (_channel != IntPtr.Zero) Fmod.SetVolume(_channel, volume);
    }

    public void Stop()
    {
        _isWanted = false;
        Volatile.Write(ref _playing, null);

        if (_channel == IntPtr.Zero) return;

        Fmod.Stop(_channel);
        _channel = IntPtr.Zero;
    }

    /// <summary>Once a frame. Does nothing unless a start is waiting for the music bus.</summary>
    public void Update()
    {
        if (_isWanted && !_hasFailed && _channel == IntPtr.Zero) TryStart();
    }

    private void TryStart()
    {
        if (!_hasLookedUp)
        {
            _missing = Fmod.Find();
            _hasLookedUp = true;
        }

        if (_missing != null)
        {
            Fail(_missing);
            return;
        }

        if (_group == IntPtr.Zero && !TryGetGroup()) return;

        var system = CoreSystem();

        if (system == IntPtr.Zero) return;

        if (_sound == IntPtr.Zero && !TryMakeSound(system)) return;

        var result = Fmod.PlaySound(system, _sound, _group, isPaused: true, out var channel);

        if (result != Fmod.Ok)
        {
            Fail($"FMOD wouldn't play the stream (error {result})");
            return;
        }

        _channel = channel;
        Fmod.SetPriority(channel, 0);
        Fmod.SetVolume(channel, _volume);
        Fmod.SetPaused(channel, false);
    }

    /// <summary>
    /// The music bus's channel group exists only while something uses the bus. Locking it keeps it alive, but the lock
    /// is done in FMOD's next update, so the group is asked for again each frame until it is there.
    /// </summary>
    private bool TryGetGroup()
    {
        if (_bus == IntPtr.Zero)
        {
            _bus = MusicBus();

            if (_bus == IntPtr.Zero) return false;

            var locked = Fmod.LockGroup(_bus);

            if (locked != Fmod.Ok)
            {
                _bus = IntPtr.Zero;
                Fail($"FMOD wouldn't lock the music bus (error {locked})");
            }

            return false;
        }

        var result = Fmod.GetGroup(_bus, out var group);

        if (result == Fmod.Ok && group != IntPtr.Zero)
        {
            _group = group;
            return true;
        }

        if (++_waitedFrames > MaxWaitFrames) Fail($"the music bus never got ready (FMOD error {result})");

        return false;
    }

    private bool TryMakeSound(IntPtr system)
    {
        var exInfo = stackalloc byte[Fmod.ExInfoSize];
        new Span<byte>(exInfo, Fmod.ExInfoSize).Clear();
        *(int*)exInfo = Fmod.ExInfoSize;
        *(uint*)(exInfo + Fmod.ExLength) = (uint)(SampleRing.Rate * SampleRing.Channels * sizeof(float) * LoopSeconds);
        *(int*)(exInfo + Fmod.ExChannels) = SampleRing.Channels;
        *(int*)(exInfo + Fmod.ExRate) = SampleRing.Rate;
        *(int*)(exInfo + Fmod.ExFormat) = Fmod.PcmFloat;
        *(uint*)(exInfo + Fmod.ExDecodeFrames) = FramesPerRead;
        *(IntPtr*)(exInfo + Fmod.ExPcmRead) = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, uint, int>)&ReadPcm;

        var result = Fmod.CreateSound(system, Mode, exInfo, out var sound);

        if (result != Fmod.Ok)
        {
            Fail($"FMOD wouldn't make the stream (error {result})");
            return false;
        }

        _sound = sound;

        return true;
    }

    private void Fail(string why)
    {
        _hasFailed = true;
        _isWanted = false;
        _hasNewFailure = true;
        _log("The radio can't play into the game's music: " + why);
    }

    private static IntPtr MusicBus() => GameSingleton.Existing<FmodManager>() is { } manager ? manager._bgBus_k__BackingField.handle : IntPtr.Zero;

    private static IntPtr CoreSystem() => RuntimeManager.IsInitialized ? RuntimeManager.CoreSystem.handle : IntPtr.Zero;

    /// <summary>
    /// FMOD's stream thread asking for the next block of sound, as 32-bit floats. It must never throw, and never touches
    /// the game; a ring that has nothing yet gives silence.
    /// </summary>
    [dotnet::System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static int ReadPcm(IntPtr sound, IntPtr data, uint bytes)
    {
        var output = new Span<float>((void*)data, (int)(bytes / sizeof(float)));

        try
        {
            var ring = Volatile.Read(ref _playing);

            if (ring == null) output.Clear();
            else ring.Read(output);
        }
        catch
        {
            output.Clear();
        }

        return Fmod.Ok;
    }
}
