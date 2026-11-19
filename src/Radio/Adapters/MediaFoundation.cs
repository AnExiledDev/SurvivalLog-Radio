using System;
using System.IO;
using System.Runtime.InteropServices;
using Radio.Core;

namespace Radio.Adapters;

/// <summary>
/// The Windows Media Foundation calls the radio decodes with. Its objects are COM pointers here, and each method is
/// called by its place in the object's method table, in the order the Windows headers give.
/// </summary>
internal static unsafe class Mf
{
    public const int NeedMoreInput = unchecked((int)0xC00D6D72);
    public const int StreamChange = unchecked((int)0xC00D6D61);
    public const int NotAccepting = unchecked((int)0xC00D36B5);
    public const int NoMoreTypes = unchecked((int)0xC00D36B9);

    public const uint FirstAudioStream = 0xFFFFFFFD;
    public const uint AllStreams = 0xFFFFFFFE;
    public const uint ReaderError = 0x1;
    public const uint EndOfStream = 0x2;
    public const uint TypeChanged = 0x20;

    public const uint CommandFlush = 0;
    public const uint BeginStreaming = 0x10000000;
    public const uint StartOfStream = 0x10000003;
    public const uint ProvidesSamples = 0x100;

    private const uint Version = 0x20070;
    private const uint SyncMft = 0x1;
    private const uint LocalMft = 0x10;
    private const uint SortAndFilter = 0x40;
    private const uint MultiThreaded = 0;

    public static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid Channels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    public static readonly Guid SampleRate = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    public static readonly Guid BitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    public static readonly Guid UserData = new("b6bc765f-4c3b-40a4-bd51-2535b66fe09d");
    public static readonly Guid AacPayloadType = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188");

    public static readonly Guid Audio = new("73647561-0000-0010-8000-00aa00389b71");
    public static readonly Guid Pcm = AudioSubtype(0x1);
    public static readonly Guid Float = AudioSubtype(0x3);
    public static readonly Guid Mpeg = AudioSubtype(0x50);
    public static readonly Guid Mp3 = AudioSubtype(0x55);
    public static readonly Guid Aac = AudioSubtype(0x1610);

    private static readonly Guid AudioDecoders = new("9ea73fb4-ef7a-4559-8d5d-719d8f0426c7");
    private static readonly Guid TransformId = new("bf94c121-5b05-4e6f-8000-ba598961414d");

    /// <summary>Errors that mean Windows has no way to read this format, as opposed to a file or connection problem.</summary>
    private static readonly int[] UnsupportedErrors =
    {
        unchecked((int)0xC00D36B4), // MF_E_INVALIDMEDIATYPE
        unchecked((int)0xC00D36B3), // MF_E_INVALIDSTREAMNUMBER: no sound in it
        unchecked((int)0xC00D36B9), // MF_E_NO_MORE_TYPES
        unchecked((int)0xC00D36BD), // MF_E_INVALIDTYPE
        unchecked((int)0xC00D36C3), // MF_E_UNSUPPORTED_SCHEME
        unchecked((int)0xC00D36C4), // MF_E_UNSUPPORTED_BYTESTREAM_TYPE
        unchecked((int)0xC00D3E98), // MF_E_UNSUPPORTED_FORMAT
        unchecked((int)0xC00D5212), // MF_E_TOPO_CODEC_NOT_FOUND
    };

    private static readonly object StartLock = new();
    private static int? _started;

    [ThreadStatic]
    private static bool _isThreadReady;

    /// <summary>
    /// Starts Media Foundation for the process, once, and COM for this thread. Windows editions without the media
    /// features have none of it, which shows as the format not being supported.
    /// </summary>
    public static void Ready(string format)
    {
        try
        {
            if (!_isThreadReady)
            {
                CoInitializeEx(IntPtr.Zero, MultiThreaded);
                _isThreadReady = true;
            }

            lock (StartLock) _started ??= MFStartup(Version, 0);
        }
        catch (DllNotFoundException)
        {
            throw new UnsupportedAudio(format);
        }

        if (_started < 0) throw new UnsupportedAudio(format);
    }

    public static bool IsUnsupported(int result) => Array.IndexOf(UnsupportedErrors, result) >= 0;

    public static void Check(int result, string what)
    {
        if (result < 0) throw new IOException($"{what} failed (0x{result:X8})", result);
    }

    // Plain calls.

    public static IntPtr NewMediaType()
    {
        IntPtr type;
        Check(MFCreateMediaType(&type), "Making a media type");

        return type;
    }

    public static IntPtr NewSample()
    {
        IntPtr sample;
        Check(MFCreateSample(&sample), "Making a sample");

        return sample;
    }

    public static IntPtr NewBuffer(int bytes)
    {
        IntPtr buffer;
        Check(MFCreateMemoryBuffer((uint)bytes, &buffer), "Making a buffer");

        return buffer;
    }

    /// <summary>The installed decoders that take this audio subtype, best first. The caller releases each one.</summary>
    public static IntPtr[] FindDecoders(Guid subtype)
    {
        var input = new TypeInfo { Major = Audio, Sub = subtype };
        IntPtr* found;
        uint count;

        if (MFTEnumEx(AudioDecoders, SyncMft | LocalMft | SortAndFilter, &input, null, &found, &count) < 0) return Array.Empty<IntPtr>();

        var decoders = new IntPtr[count];

        for (var i = 0; i < count; i++) decoders[i] = found[i];

        Marshal.FreeCoTaskMem((IntPtr)found);

        return decoders;
    }

    public static int NewSourceReader(string pathOrLink, out IntPtr reader)
    {
        IntPtr made;
        var result = MFCreateSourceReaderFromURL(pathOrLink, IntPtr.Zero, &made);
        reader = made;

        return result;
    }

    // IUnknown.

    public static void Release(IntPtr unknown)
    {
        if (unknown != IntPtr.Zero) ((delegate* unmanaged<IntPtr, uint>)Method(unknown, 2))(unknown);
    }

    // IMFAttributes, which media types inherit.

    public static int GetUInt32(IntPtr attributes, Guid key, out uint value)
    {
        uint read;
        var result = ((delegate* unmanaged<IntPtr, Guid*, uint*, int>)Method(attributes, 7))(attributes, &key, &read);
        value = read;

        return result;
    }

    public static int GetGuid(IntPtr attributes, Guid key, out Guid value)
    {
        Guid read;
        var result = ((delegate* unmanaged<IntPtr, Guid*, Guid*, int>)Method(attributes, 10))(attributes, &key, &read);
        value = read;

        return result;
    }

    public static void SetUInt32(IntPtr attributes, Guid key, uint value) =>
        Check(((delegate* unmanaged<IntPtr, Guid*, uint, int>)Method(attributes, 21))(attributes, &key, value), "Setting a media type value");

    public static void SetGuid(IntPtr attributes, Guid key, Guid value) =>
        Check(((delegate* unmanaged<IntPtr, Guid*, Guid*, int>)Method(attributes, 24))(attributes, &key, &value), "Setting a media type value");

    public static void SetBlob(IntPtr attributes, Guid key, ReadOnlySpan<byte> blob)
    {
        fixed (byte* bytes = blob)
            Check(((delegate* unmanaged<IntPtr, Guid*, byte*, uint, int>)Method(attributes, 26))(attributes, &key, bytes, (uint)blob.Length), "Setting a media type value");
    }

    // IMFSample.

    public static int ConvertToContiguousBuffer(IntPtr sample, out IntPtr buffer)
    {
        IntPtr made;
        var result = ((delegate* unmanaged<IntPtr, IntPtr*, int>)Method(sample, 41))(sample, &made);
        buffer = made;

        return result;
    }

    public static void AddBuffer(IntPtr sample, IntPtr buffer) => Check(((delegate* unmanaged<IntPtr, IntPtr, int>)Method(sample, 42))(sample, buffer), "Adding a buffer");

    // IMFMediaBuffer.

    public static int Lock(IntPtr buffer, out byte* data, out int length)
    {
        byte* start;
        uint max;
        uint current;
        var result = ((delegate* unmanaged<IntPtr, byte**, uint*, uint*, int>)Method(buffer, 3))(buffer, &start, &max, &current);
        data = start;
        length = (int)current;

        return result;
    }

    public static void Unlock(IntPtr buffer) => ((delegate* unmanaged<IntPtr, int>)Method(buffer, 4))(buffer);

    public static void SetCurrentLength(IntPtr buffer, int length) => Check(((delegate* unmanaged<IntPtr, uint, int>)Method(buffer, 6))(buffer, (uint)length), "Sizing a buffer");

    // IMFActivate.

    public static int ActivateTransform(IntPtr activate, out IntPtr transform)
    {
        var id = TransformId;
        IntPtr made;
        var result = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Method(activate, 33))(activate, &id, &made);
        transform = made;

        return result;
    }

    public static void ShutdownObject(IntPtr activate) => ((delegate* unmanaged<IntPtr, int>)Method(activate, 34))(activate);

    // IMFTransform. The Windows audio decoders have one input and one output stream, both id 0.

    public static int GetOutputStreamInfo(IntPtr transform, out OutputStreamInfo info)
    {
        OutputStreamInfo read;
        var result = ((delegate* unmanaged<IntPtr, uint, OutputStreamInfo*, int>)Method(transform, 7))(transform, 0, &read);
        info = read;

        return result;
    }

    public static int GetOutputAvailableType(IntPtr transform, uint index, out IntPtr type)
    {
        IntPtr made;
        var result = ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)Method(transform, 14))(transform, 0, index, &made);
        type = made;

        return result;
    }

    public static int SetInputType(IntPtr transform, IntPtr type) => ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Method(transform, 15))(transform, 0, type, 0);

    public static int SetOutputType(IntPtr transform, IntPtr type) => ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Method(transform, 16))(transform, 0, type, 0);

    public static int ProcessMessage(IntPtr transform, uint message) => ((delegate* unmanaged<IntPtr, uint, UIntPtr, int>)Method(transform, 23))(transform, message, UIntPtr.Zero);

    public static int ProcessInput(IntPtr transform, IntPtr sample) => ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Method(transform, 24))(transform, 0, sample, 0);

    public static int ProcessOutput(IntPtr transform, OutputData* output)
    {
        uint status;

        return ((delegate* unmanaged<IntPtr, uint, uint, OutputData*, uint*, int>)Method(transform, 25))(transform, 0, 1, output, &status);
    }

    // IMFSourceReader.

    public static int SetStreamSelection(IntPtr reader, uint stream, bool isSelected) =>
        ((delegate* unmanaged<IntPtr, uint, int, int>)Method(reader, 4))(reader, stream, isSelected ? 1 : 0);

    public static int GetCurrentMediaType(IntPtr reader, out IntPtr type)
    {
        IntPtr made;
        var result = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Method(reader, 6))(reader, FirstAudioStream, &made);
        type = made;

        return result;
    }

    public static int SetCurrentMediaType(IntPtr reader, IntPtr type) =>
        ((delegate* unmanaged<IntPtr, uint, uint*, IntPtr, int>)Method(reader, 7))(reader, FirstAudioStream, null, type);

    /// <summary>Waits for the next block of sound. The sample is null at the end and on gaps.</summary>
    public static int ReadSample(IntPtr reader, out uint flags, out IntPtr sample)
    {
        uint actualStream;
        uint readFlags;
        long time;
        IntPtr read;
        var result = ((delegate* unmanaged<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)Method(reader, 9))(reader, FirstAudioStream, 0, &actualStream, &readFlags, &time, &read);
        flags = readFlags;
        sample = read;

        return result;
    }

    private static void* Method(IntPtr unknown, int index) => (*(void***)unknown)[index];

    private static Guid AudioSubtype(uint formatTag) => new(formatTag, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);

    [StructLayout(LayoutKind.Sequential)]
    private struct TypeInfo
    {
        public Guid Major;
        public Guid Sub;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OutputStreamInfo
    {
        public uint Flags;
        public uint Size;
        public uint Alignment;
    }

    /// <summary>MFT_OUTPUT_DATA_BUFFER: 32 bytes on 64 bit.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct OutputData
    {
        public uint StreamId;
        public IntPtr Sample;
        public uint Status;
        public IntPtr Events;
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint mode);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType(IntPtr* type);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateSample(IntPtr* sample);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMemoryBuffer(uint maxLength, IntPtr* buffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFTEnumEx(Guid category, uint flags, TypeInfo* input, TypeInfo* output, IntPtr** activates, uint* count);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, IntPtr* reader);
}
