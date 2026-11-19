using System;

namespace Radio.Core;

/// <summary>What the MPEG-TS stream says its audio is.</summary>
public enum TsAudio
{
    /// <summary>The program table hasn't come by yet.</summary>
    Unknown,

    /// <summary>AAC in ADTS or MPEG audio: the frame splitter takes it.</summary>
    Playable,

    /// <summary>Only audio this mod can't decode, like AC-3 or LATM AAC.</summary>
    Unsupported,

    /// <summary>The program has no audio at all.</summary>
    None,
}

/// <summary>
/// Takes the audio out of an MPEG transport stream, the format HLS radio segments use. It reads the program tables
/// to find the audio track, strips the 188-byte packets and the PES headers, and hands the audio bytes on.
/// Program tables split over two packets aren't handled; radio streams keep them in one.
/// </summary>
public sealed class TsDemux : IByteSink
{
    private const int PacketSize = 188;
    private const byte Sync = 0x47;

    private readonly IByteSink _audioSink;
    private readonly byte[] _packet = new byte[PacketSize];
    private int _have;
    private int _pmtPid = -1;
    private int _audioPid = -1;

    public TsDemux(IByteSink audioSink) => _audioSink = audioSink;

    public TsAudio Audio { get; private set; }

    /// <summary>The stream type number the audio track or the unsupported one has, for the log.</summary>
    public int StreamType { get; private set; }

    /// <summary>True when the bytes start the way a transport stream does: a sync byte every 188 bytes.</summary>
    public static bool LooksLikeTs(ReadOnlySpan<byte> data) =>
        data.Length >= PacketSize * 2 + 1 && data[0] == Sync && data[PacketSize] == Sync && data[PacketSize * 2] == Sync;

    /// <summary>A discontinuity: the next bytes may come from a different encoder.</summary>
    public void Reset()
    {
        _have = 0;
        _pmtPid = -1;
        _audioPid = -1;
        Audio = TsAudio.Unknown;
    }

    public void Bytes(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            if (_have == 0)
            {
                // Find the next sync byte; anything before it is noise.
                var sync = data.IndexOf(Sync);

                if (sync < 0) return;

                data = data.Slice(sync);
            }

            var take = Math.Min(PacketSize - _have, data.Length);
            data.Slice(0, take).CopyTo(_packet.AsSpan(_have));
            _have += take;
            data = data.Slice(take);

            if (_have < PacketSize) return;

            _have = 0;
            Packet(_packet);
        }
    }

    private void Packet(ReadOnlySpan<byte> p)
    {
        var startsUnit = (p[1] & 0x40) != 0;
        var pid = ((p[1] & 0x1F) << 8) | p[2];
        var adaptation = (p[3] >> 4) & 3;
        var offset = 4;

        if ((adaptation & 2) != 0) offset += 1 + p[4];

        if ((adaptation & 1) == 0 || offset >= PacketSize) return;

        var payload = p.Slice(offset);

        if (pid == 0 && startsUnit) ReadPat(payload);
        else if (pid == _pmtPid && startsUnit) ReadPmt(payload);
        else if (pid == _audioPid) AudioPayload(payload, startsUnit);
    }

    private void AudioPayload(ReadOnlySpan<byte> payload, bool startsUnit)
    {
        if (startsUnit)
        {
            // PES header: 00 00 01, stream id, length (2), two flag bytes, then how long the rest of the header is.
            if (payload.Length < 9 || payload[0] != 0 || payload[1] != 0 || payload[2] != 1) return;

            var skip = 9 + payload[8];

            if (skip >= payload.Length) return;

            payload = payload.Slice(skip);
        }

        _audioSink.Bytes(payload);
    }

    private void ReadPat(ReadOnlySpan<byte> payload)
    {
        if (!Section(payload, 0x00, out var body)) return;

        // After the 5 bytes of table header, 4 bytes per program: number, then its table's PID.
        for (var i = 5; i + 4 <= body.Length; i += 4)
        {
            var program = (body[i] << 8) | body[i + 1];

            if (program == 0) continue;

            _pmtPid = ((body[i + 2] & 0x1F) << 8) | body[i + 3];
            return;
        }
    }

    private void ReadPmt(ReadOnlySpan<byte> payload)
    {
        if (!Section(payload, 0x02, out var body) || body.Length < 9) return;

        var infoLength = ((body[7] & 0x0F) << 8) | body[8];
        var unsupported = 0;

        for (var i = 9 + infoLength; i + 5 <= body.Length;)
        {
            var type = body[i];
            var pid = ((body[i + 1] & 0x1F) << 8) | body[i + 2];
            var esInfo = ((body[i + 3] & 0x0F) << 8) | body[i + 4];
            i += 5 + esInfo;

            // 0x0F is AAC in ADTS, 0x03 and 0x04 are MPEG audio.
            if (type is 0x0F or 0x03 or 0x04)
            {
                if (_audioPid != pid) _audioPid = pid;

                Audio = TsAudio.Playable;
                StreamType = type;
                return;
            }

            // 0x11 is AAC in LATM, 0x81 is AC-3, 0x87 is E-AC-3.
            if (type is 0x11 or 0x81 or 0x87) unsupported = type;
        }

        _audioPid = -1;
        Audio = unsupported != 0 ? TsAudio.Unsupported : TsAudio.None;
        StreamType = unsupported;
    }

    /// <summary>The table after the pointer field, without its CRC, when it is the kind asked for.</summary>
    private static bool Section(ReadOnlySpan<byte> payload, byte tableId, out ReadOnlySpan<byte> body)
    {
        body = default;

        if (payload.Length < 1) return false;

        var start = 1 + payload[0];

        if (start + 3 > payload.Length || payload[start] != tableId) return false;

        var length = ((payload[start + 1] & 0x0F) << 8) | payload[start + 2];
        var end = start + 3 + length - 4;

        if (length < 9 || end > payload.Length) return false;

        body = payload.Slice(start + 3, end - start - 3);
        return true;
    }
}
