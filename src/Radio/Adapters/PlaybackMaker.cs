using System;
using System.Collections.Generic;
using Radio.Core;

namespace Radio.Adapters;

/// <summary>Starts a station or the music folder on its own thread, with the Windows decoders.</summary>
internal sealed class PlaybackMaker : IPlaybackMaker
{
    private readonly StreamTools _tools;

    public PlaybackMaker(Network network) =>
        _tools = new StreamTools(network.Connect, () => new MfFrameDecoder(), MfFileDecoder.Open, Network.UserAgent);

    public IPlayback Stream(Uri link)
    {
        var player = new StreamPlayer(link, _tools);
        player.Start();
        return player;
    }

    public IPlayback Songs(IReadOnlyList<string> paths, int first)
    {
        var player = new FilePlayer(paths, first, MfFileDecoder.Open);
        player.Start();
        return player;
    }
}
