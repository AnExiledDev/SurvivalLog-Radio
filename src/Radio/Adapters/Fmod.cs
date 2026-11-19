using System;
using System.Runtime.InteropServices;

namespace Radio.Adapters;

/// <summary>
/// The few FMOD calls the radio makes. They come from the game's fmodstudio.dll, which has FMOD's core calls built in;
/// the game's fmod.dll is a Windows Store build that never loads. The game has loaded fmodstudio.dll long before the
/// radio first plays, so this only looks the calls up and never loads a second copy. Every call returns an FMOD result
/// code.
/// </summary>
internal static unsafe class Fmod
{
    public const int Ok = 0;

    // FMOD_MODE bits and FMOD_SOUND_FORMAT values, as the game's FMOD wrapper has them.
    public const uint Loop = 0x2;
    public const uint TwoD = 0x8;
    public const uint Stream = 0x80;
    public const uint OpenUser = 0x400;
    public const int PcmFloat = 5;

    /// <summary>FMOD_CREATESOUNDEXINFO is 0xE0 bytes on 64 bit; these are the fields a sound made by code needs.</summary>
    public const int ExInfoSize = 0xE0;
    public const int ExLength = 4;
    public const int ExChannels = 12;
    public const int ExRate = 16;
    public const int ExFormat = 20;
    public const int ExDecodeFrames = 24;
    public const int ExPcmRead = 0x38;

    private static delegate* unmanaged<IntPtr, int> _lockGroup;
    private static delegate* unmanaged<IntPtr, IntPtr*, int> _getGroup;
    private static delegate* unmanaged<IntPtr, byte*, uint, byte*, IntPtr*, int> _createSound;
    private static delegate* unmanaged<IntPtr, IntPtr, IntPtr, int, IntPtr*, int> _playSound;
    private static delegate* unmanaged<IntPtr, float, int> _setVolume;
    private static delegate* unmanaged<IntPtr, int, int> _setPaused;
    private static delegate* unmanaged<IntPtr, int, int> _setPriority;
    private static delegate* unmanaged<IntPtr, int> _stop;
    private static delegate* unmanaged<IntPtr, int> _release;

    /// <summary>Null once every call is found, otherwise what is missing.</summary>
    public static string? Find()
    {
        var studio = GetModuleHandleW("fmodstudio.dll");

        if (studio == IntPtr.Zero) return "fmodstudio.dll isn't loaded";

        try
        {
            _lockGroup = (delegate* unmanaged<IntPtr, int>)NativeLibrary.GetExport(studio, "FMOD_Studio_Bus_LockChannelGroup");
            _getGroup = (delegate* unmanaged<IntPtr, IntPtr*, int>)NativeLibrary.GetExport(studio, "FMOD_Studio_Bus_GetChannelGroup");
            _createSound = (delegate* unmanaged<IntPtr, byte*, uint, byte*, IntPtr*, int>)NativeLibrary.GetExport(studio, "FMOD5_System_CreateSound");
            _playSound = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, int, IntPtr*, int>)NativeLibrary.GetExport(studio, "FMOD5_System_PlaySound");
            _setVolume = (delegate* unmanaged<IntPtr, float, int>)NativeLibrary.GetExport(studio, "FMOD5_Channel_SetVolume");
            _setPaused = (delegate* unmanaged<IntPtr, int, int>)NativeLibrary.GetExport(studio, "FMOD5_Channel_SetPaused");
            _setPriority = (delegate* unmanaged<IntPtr, int, int>)NativeLibrary.GetExport(studio, "FMOD5_Channel_SetPriority");
            _stop = (delegate* unmanaged<IntPtr, int>)NativeLibrary.GetExport(studio, "FMOD5_Channel_Stop");
            _release = (delegate* unmanaged<IntPtr, int>)NativeLibrary.GetExport(studio, "FMOD5_Sound_Release");
        }
        catch (EntryPointNotFoundException ex)
        {
            return ex.Message;
        }

        return null;
    }

    /// <summary>Keeps the bus's channel group alive while nothing else plays on it. It is ready a frame or so later.</summary>
    public static int LockGroup(IntPtr bus) => _lockGroup(bus);

    public static int GetGroup(IntPtr bus, out IntPtr group)
    {
        IntPtr found;
        var result = _getGroup(bus, &found);
        group = found;

        return result;
    }

    public static int CreateSound(IntPtr system, uint mode, byte* exInfo, out IntPtr sound)
    {
        IntPtr made;
        var result = _createSound(system, null, mode, exInfo, &made);
        sound = made;

        return result;
    }

    public static int PlaySound(IntPtr system, IntPtr sound, IntPtr group, bool isPaused, out IntPtr channel)
    {
        IntPtr made;
        var result = _playSound(system, sound, group, isPaused ? 1 : 0, &made);
        channel = made;

        return result;
    }

    public static int SetVolume(IntPtr channel, float volume) => _setVolume(channel, volume);

    public static int SetPaused(IntPtr channel, bool isPaused) => _setPaused(channel, isPaused ? 1 : 0);

    /// <summary>0 is the highest; a channel at 0 is never cut to make room for the game's sounds.</summary>
    public static int SetPriority(IntPtr channel, int priority) => _setPriority(channel, priority);

    public static int Stop(IntPtr channel) => _stop(channel);

    public static int Release(IntPtr sound) => _release(sound);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string name);
}
