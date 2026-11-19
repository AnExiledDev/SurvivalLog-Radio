using System;
using System.IO;
using Radio.Core;

namespace Radio.Adapters;

/// <summary>
/// Decodes MP3, MP2 and AAC frames from a station with the decoders Windows ships with. Made and used on the
/// stream's own thread.
/// </summary>
internal sealed unsafe class MfFrameDecoder : IFrameDecoder
{
    /// <summary>Room for one decoded frame of 8 channel HE-AAC as floats.</summary>
    private const int MinOutputBytes = 1 << 16;

    /// <summary>Bad frames in a row before the stream counts as broken and reconnects.</summary>
    private const int MaxBadFrames = 50;

    /// <summary>A frame gives a few blocks of sound at most; more than this means the decoder is stuck.</summary>
    private const int MaxOutputsPerFrame = 64;

    private readonly PcmReader _pcm = new();
    private FrameFormat? _format;
    private string _name = "";
    private IntPtr _activate;
    private IntPtr _transform;
    private IntPtr _inSample;
    private IntPtr _inBuffer;
    private int _inCapacity;
    private IntPtr _outSample;
    private IntPtr _outBuffer;
    private bool _isProvidingSamples;
    private int _badFrames;

    public void Decode(FrameFormat format, ReadOnlySpan<byte> frame, IPcmSink output)
    {
        if (!format.IsSameAs(_format)) Start(format);

        FillInput(frame);
        var result = Mf.ProcessInput(_transform, _inSample);

        if (result == Mf.NotAccepting)
        {
            Drain(output);
            result = Mf.ProcessInput(_transform, _inSample);
        }

        if (result < 0)
        {
            BadFrame(result);
            return;
        }

        Drain(output);
    }

    public void Dispose()
    {
        Close();
        _format = null;
    }

    private void Start(FrameFormat format)
    {
        Close();
        _format = null;
        _name = Name(format.Codec);
        Mf.Ready(_name);

        if (!TryOpenDecoder(format)) throw new UnsupportedAudio(_name);

        PickOutputType();
        Mf.ProcessMessage(_transform, Mf.BeginStreaming);
        Mf.ProcessMessage(_transform, Mf.StartOfStream);
        _format = format;
        _badFrames = 0;
    }

    /// <summary>Tries each installed decoder for the codec, best first, until one takes the stream's format.</summary>
    private bool TryOpenDecoder(FrameFormat format)
    {
        var decoders = Mf.FindDecoders(Subtype(format.Codec));
        var inputType = InputType(format);

        try
        {
            foreach (var activate in decoders)
            {
                if (_transform != IntPtr.Zero) break;

                if (Mf.ActivateTransform(activate, out var transform) < 0) continue;

                if (Mf.SetInputType(transform, inputType) >= 0)
                {
                    _activate = activate;
                    _transform = transform;
                    continue;
                }

                Mf.Release(transform);
                Mf.ShutdownObject(activate);
            }
        }
        finally
        {
            Mf.Release(inputType);

            foreach (var activate in decoders)
            {
                if (activate != _activate) Mf.Release(activate);
            }
        }

        return _transform != IntPtr.Zero;
    }

    private static IntPtr InputType(FrameFormat format)
    {
        var type = Mf.NewMediaType();
        Mf.SetGuid(type, Mf.MajorType, Mf.Audio);
        Mf.SetGuid(type, Mf.Subtype, Subtype(format.Codec));
        Mf.SetUInt32(type, Mf.Channels, (uint)format.Channels);
        Mf.SetUInt32(type, Mf.SampleRate, (uint)format.SampleRate);

        if (format.Codec == Codec.Aac)
        {
            Mf.SetUInt32(type, Mf.BitsPerSample, 16);
            Mf.SetUInt32(type, Mf.AacPayloadType, 0);
            Mf.SetBlob(type, Mf.UserData, AacUserData(format.Config));
        }

        return type;
    }

    /// <summary>
    /// The decoder wants the end of an HEAACWAVEINFO before the AudioSpecificConfig: raw payload, no profile named,
    /// and three reserved fields.
    /// </summary>
    private static byte[] AacUserData(byte[] config)
    {
        var data = new byte[12 + config.Length];
        data[2] = 0xFE;
        config.CopyTo(data, 12);

        return data;
    }

    /// <summary>
    /// Float output when the decoder has it, otherwise 16-bit. Also run when the decoder says its output changed,
    /// which HE-AAC does once it finds the higher band.
    /// </summary>
    private void PickOutputType()
    {
        var chosen = IntPtr.Zero;
        var isFloat = false;

        for (uint i = 0; !isFloat; i++)
        {
            if (Mf.GetOutputAvailableType(_transform, i, out var type) < 0) break;

            if (PcmReader.IsFloat(type))
            {
                Mf.Release(chosen);
                chosen = type;
                isFloat = true;
            }
            else if (chosen == IntPtr.Zero && PcmReader.Is16Bit(type))
            {
                chosen = type;
            }
            else
            {
                Mf.Release(type);
            }
        }

        if (chosen == IntPtr.Zero) throw new UnsupportedAudio(_name);

        try
        {
            Mf.Check(Mf.SetOutputType(_transform, chosen), "Setting the decoder's output");
            _pcm.ReadType(chosen);
        }
        finally
        {
            Mf.Release(chosen);
        }

        Mf.Check(Mf.GetOutputStreamInfo(_transform, out var info), "Reading the decoder's output");
        _isProvidingSamples = (info.Flags & Mf.ProvidesSamples) != 0;

        if (!_isProvidingSamples) MakeOutputSample(Math.Max((int)info.Size, MinOutputBytes));
    }

    private void FillInput(ReadOnlySpan<byte> frame)
    {
        if (frame.Length > _inCapacity)
        {
            Mf.Release(_inSample);
            Mf.Release(_inBuffer);
            _inCapacity = Math.Max(frame.Length, 4096);
            _inBuffer = Mf.NewBuffer(_inCapacity);
            _inSample = Mf.NewSample();
            Mf.AddBuffer(_inSample, _inBuffer);
        }

        Mf.Check(Mf.Lock(_inBuffer, out var data, out _), "Filling the decoder's input");
        frame.CopyTo(new Span<byte>(data, _inCapacity));
        Mf.Unlock(_inBuffer);
        Mf.SetCurrentLength(_inBuffer, frame.Length);
    }

    private void MakeOutputSample(int bytes)
    {
        Mf.Release(_outSample);
        Mf.Release(_outBuffer);
        _outBuffer = Mf.NewBuffer(bytes);
        _outSample = Mf.NewSample();
        Mf.AddBuffer(_outSample, _outBuffer);
    }

    /// <summary>Takes every block of sound the decoder has ready. The input sample is free again once this returns.</summary>
    private void Drain(IPcmSink output)
    {
        for (var i = 0; i < MaxOutputsPerFrame; i++)
        {
            var data = new Mf.OutputData();

            if (!_isProvidingSamples)
            {
                Mf.SetCurrentLength(_outBuffer, 0);
                data.Sample = _outSample;
            }

            var result = Mf.ProcessOutput(_transform, &data);
            Mf.Release(data.Events);

            if (result == Mf.NeedMoreInput) return;

            if (result == Mf.StreamChange)
            {
                PickOutputType();
                continue;
            }

            if (result < 0)
            {
                if (_isProvidingSamples) Mf.Release(data.Sample);

                BadFrame(result);
                return;
            }

            _badFrames = 0;
            Send(data.Sample, output);
        }
    }

    private void Send(IntPtr sample, IPcmSink output)
    {
        if (!_isProvidingSamples)
        {
            _pcm.Send(_outBuffer, output);
            return;
        }

        IntPtr buffer = IntPtr.Zero;

        try
        {
            Mf.Check(Mf.ConvertToContiguousBuffer(sample, out buffer), "Reading decoded sound");
            _pcm.Send(buffer, output);
        }
        finally
        {
            Mf.Release(buffer);
            Mf.Release(sample);
        }
    }

    /// <summary>
    /// A frame cut short by a reconnect or a glitch on the line. The decoder is cleared and the next frame tried; a long
    /// run of them means the stream is broken, and it is set up fresh on the next try.
    /// </summary>
    private void BadFrame(int result)
    {
        Mf.ProcessMessage(_transform, Mf.CommandFlush);

        if (++_badFrames < MaxBadFrames) return;

        _format = null;

        throw new IOException($"The decoder failed {MaxBadFrames} frames in a row (0x{result:X8})", result);
    }

    private void Close()
    {
        Mf.Release(_inSample);
        Mf.Release(_inBuffer);
        Mf.Release(_outSample);
        Mf.Release(_outBuffer);
        Mf.Release(_transform);
        _inSample = _inBuffer = _outSample = _outBuffer = _transform = IntPtr.Zero;
        _inCapacity = 0;

        if (_activate == IntPtr.Zero) return;

        Mf.ShutdownObject(_activate);
        Mf.Release(_activate);
        _activate = IntPtr.Zero;
    }

    private static Guid Subtype(Codec codec) => codec switch
    {
        Codec.Mp3 => Mf.Mp3,
        Codec.MpegLayer12 => Mf.Mpeg,
        _ => Mf.Aac,
    };

    private static string Name(Codec codec) => codec switch
    {
        Codec.Mp3 => "MP3",
        Codec.MpegLayer12 => "MP2",
        _ => "AAC",
    };
}

/// <summary>
/// Reads a song file, or a link the stream reader can't split itself, with the reader that comes with Windows. It
/// plays whatever Windows can: MP3, AAC and M4A, WMA, WAV, FLAC, and Ogg or Opus where the media extensions are
/// installed.
/// </summary>
internal sealed unsafe class MfFileDecoder : IFileDecoder
{
    private readonly PcmReader _pcm = new();
    private IntPtr _reader;

    private MfFileDecoder(IntPtr reader) => _reader = reader;

    /// <summary>The <see cref="FileOpener"/> for songs and links. Runs on the reader's thread.</summary>
    public static IFileDecoder Open(string pathOrLink)
    {
        var format = FormatName(pathOrLink);
        Mf.Ready(format);
        var opened = Mf.NewSourceReader(pathOrLink, out var reader);

        if (opened < 0) throw Mf.IsUnsupported(opened) ? new UnsupportedAudio(format) : new IOException($"Windows couldn't open it (0x{opened:X8})", opened);

        var decoder = new MfFileDecoder(reader);

        try
        {
            decoder.AskForFloats(format);
        }
        catch
        {
            decoder.Dispose();
            throw;
        }

        return decoder;
    }

    public bool Read(IPcmSink output)
    {
        Mf.Check(Mf.ReadSample(_reader, out var flags, out var sample), "Reading the song");

        try
        {
            if ((flags & Mf.ReaderError) != 0) throw new IOException("Windows stopped reading the song");

            if ((flags & Mf.TypeChanged) != 0) ReadType();

            if (sample != IntPtr.Zero) Send(sample, output);

            return (flags & Mf.EndOfStream) == 0;
        }
        finally
        {
            Mf.Release(sample);
        }
    }

    public void Dispose()
    {
        Mf.Release(_reader);
        _reader = IntPtr.Zero;
    }

    /// <summary>Only the first sound track, decoded to floats, or to 16-bit when floats aren't offered.</summary>
    private void AskForFloats(string format)
    {
        Mf.SetStreamSelection(_reader, Mf.AllStreams, false);
        var selected = Mf.SetStreamSelection(_reader, Mf.FirstAudioStream, true);

        if (selected < 0) throw Mf.IsUnsupported(selected) ? new UnsupportedAudio(format) : new IOException($"Windows found no sound in it (0x{selected:X8})", selected);

        var result = TrySetOutput(Mf.Float, 32);

        if (result < 0) result = TrySetOutput(Mf.Pcm, 16);

        if (result < 0) throw Mf.IsUnsupported(result) ? new UnsupportedAudio(format) : new IOException($"Windows couldn't decode it (0x{result:X8})", result);

        ReadType();
    }

    private int TrySetOutput(Guid subtype, uint bits)
    {
        var type = Mf.NewMediaType();

        try
        {
            Mf.SetGuid(type, Mf.MajorType, Mf.Audio);
            Mf.SetGuid(type, Mf.Subtype, subtype);
            Mf.SetUInt32(type, Mf.BitsPerSample, bits);

            return Mf.SetCurrentMediaType(_reader, type);
        }
        finally
        {
            Mf.Release(type);
        }
    }

    private void ReadType()
    {
        Mf.Check(Mf.GetCurrentMediaType(_reader, out var type), "Reading the song's format");

        try
        {
            _pcm.ReadType(type);
        }
        finally
        {
            Mf.Release(type);
        }
    }

    private void Send(IntPtr sample, IPcmSink output)
    {
        Mf.Check(Mf.ConvertToContiguousBuffer(sample, out var buffer), "Reading the song");

        try
        {
            _pcm.Send(buffer, output);
        }
        finally
        {
            Mf.Release(buffer);
        }
    }

    /// <summary>The file's extension, like "FLAC", for the log when Windows can't read it.</summary>
    private static string FormatName(string pathOrLink)
    {
        var path = Uri.TryCreate(pathOrLink, UriKind.Absolute, out var link) && !link.IsFile ? link.AbsolutePath : pathOrLink;
        var extension = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();

        return extension.Length > 0 ? extension : "an unknown format";
    }
}

/// <summary>Knows the layout of decoded sound from its media type, and hands it on as floats.</summary>
internal sealed unsafe class PcmReader
{
    private float[] _floats = new float[4096];
    private int _channels;
    private int _rate;
    private bool _isFloat;

    public static bool IsFloat(IntPtr type) => Is(type, Mf.Float, 32);

    public static bool Is16Bit(IntPtr type) => Is(type, Mf.Pcm, 16);

    public void ReadType(IntPtr type)
    {
        Mf.Check(Mf.GetUInt32(type, Mf.Channels, out var channels), "Reading the channel count");
        Mf.Check(Mf.GetUInt32(type, Mf.SampleRate, out var rate), "Reading the sample rate");

        if (channels == 0 || rate == 0) throw new IOException($"The decoder gave {channels} channels at {rate} Hz");

        _isFloat = IsFloat(type);

        if (!_isFloat && !Is16Bit(type)) throw new IOException("The decoder gave a sample format the radio can't read");

        _channels = (int)channels;
        _rate = (int)rate;
    }

    public void Send(IntPtr buffer, IPcmSink output)
    {
        Mf.Check(Mf.Lock(buffer, out var data, out var length), "Reading decoded sound");

        try
        {
            Convert(data, length, output);
        }
        finally
        {
            Mf.Unlock(buffer);
        }
    }

    private void Convert(byte* data, int length, IPcmSink output)
    {
        var bytesPerSample = _isFloat ? sizeof(float) : sizeof(short);
        var count = length / bytesPerSample / _channels * _channels;

        if (count == 0) return;

        if (_isFloat)
        {
            output.Pcm(new ReadOnlySpan<float>(data, count), _channels, _rate);
            return;
        }

        if (_floats.Length < count) _floats = new float[count];

        var shorts = (short*)data;

        for (var i = 0; i < count; i++) _floats[i] = shorts[i] / 32768f;

        output.Pcm(_floats.AsSpan(0, count), _channels, _rate);
    }

    private static bool Is(IntPtr type, Guid subtype, uint bits) =>
        Mf.GetGuid(type, Mf.Subtype, out var found) >= 0 && found == subtype && Mf.GetUInt32(type, Mf.BitsPerSample, out var foundBits) >= 0 && foundBits == bits;
}
