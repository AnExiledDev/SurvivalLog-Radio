using Radio.Core;
using Xunit;

namespace Radio.Tests;

public class HlsPlaylistTests
{
    private static readonly Uri Base = new("https://cdn.example/live/index.m3u8");

    [Fact]
    public void AMediaListGivesItsSegmentsInOrderWithTheirKeys()
    {
        var text = string.Join("\n",
            "#EXTM3U",
            "#EXT-X-TARGETDURATION:10",
            "#EXT-X-MEDIA-SEQUENCE:100",
            "#EXTINF:9.5,",
            "seg100.ts",
            "#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\",IV=0x1A",
            "#EXTINF:10.0,Song title, with a comma",
            "seg101.ts",
            "#EXT-X-DISCONTINUITY",
            "#EXTINF:10,",
            "http://other.example/seg102.ts",
            "#EXT-X-KEY:METHOD=NONE",
            "#EXTINF:10,",
            "seg103.ts",
            "#EXT-X-ENDLIST");

        var list = Hls.ParseMedia(text, Base);

        Assert.Equal(new long[] { 100, 101, 102, 103 }, list.Segments.Select(s => s.Sequence));
        Assert.Equal(new[] { 9.5, 10, 10, 10 }, list.Segments.Select(s => s.Seconds));
        Assert.Equal("https://cdn.example/live/seg100.ts", list.Segments[0].Uri.AbsoluteUri);
        Assert.Equal("http://other.example/seg102.ts", list.Segments[2].Uri.AbsoluteUri);
        Assert.Null(list.Segments[0].Key);
        Assert.Equal("https://cdn.example/live/key.bin", list.Segments[1].Key?.Uri.AbsoluteUri);
        Assert.Same(list.Segments[1].Key, list.Segments[2].Key);
        Assert.Null(list.Segments[3].Key);
        Assert.Equal(new[] { false, false, true, false }, list.Segments.Select(s => s.StartsNewPart));
        Assert.Equal(10, list.TargetSeconds);
        Assert.True(list.HasEnd);
        Assert.Null(list.Problem);
    }

    [Fact]
    public void AShortHexIvIsPaddedOnTheLeft()
    {
        var list = Hls.ParseMedia("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"k\",IV=0x1A\n#EXTINF:6,\na.ts\n", Base);

        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x1A }, list.Segments[0].Key?.Iv);
    }

    [Fact]
    public void AKeyWithoutAnIvLeavesItToTheSequenceNumber()
    {
        var list = Hls.ParseMedia("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"k\"\n#EXTINF:6,\na.ts\n", Base);

        Assert.NotNull(list.Segments[0].Key);
        Assert.Null(list.Segments[0].Key?.Iv);
    }

    [Fact]
    public void ALiveListHasNoEndAndStartsAtSequenceZeroWhenItSaysNothing()
    {
        var list = Hls.ParseMedia("#EXTM3U\r\n#EXTINF:6,\r\na.ts\r\n#EXTINF:6,\r\nb.ts\r\n", Base);

        Assert.False(list.HasEnd);
        Assert.Equal(new long[] { 0, 1 }, list.Segments.Select(s => s.Sequence));
        Assert.Equal(6, list.TargetSeconds);
    }

    [Theory]
    [InlineData("#EXT-X-MAP:URI=\"init.mp4\"", "fMP4 segments")]
    [InlineData("#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"k\"", "SAMPLE-AES encryption")]
    public void SegmentsThisModCannotReadAreNamed(string tag, string problem)
    {
        var list = Hls.ParseMedia($"#EXTM3U\n{tag}\n#EXTINF:6,\na.m4s\n", Base);

        Assert.Equal(problem, list.Problem);
    }

    [Fact]
    public void ASeparateAudioTrackIsPickedAndTheDefaultOneWins()
    {
        var master = string.Join("\n",
            "#EXTM3U",
            "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"a\",NAME=\"Other\",URI=\"other/audio.m3u8\"",
            "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"a\",NAME=\"Main\",DEFAULT=YES,URI=\"main/audio.m3u8\"",
            "#EXT-X-STREAM-INF:BANDWIDTH=64000,CODECS=\"mp4a.40.2\",AUDIO=\"a\"",
            "audio-only.m3u8");

        Assert.Equal("https://cdn.example/live/main/audio.m3u8", Hls.PickVariant(master, Base)?.AbsoluteUri);
    }

    [Fact]
    public void TheBestAudioOnlyQualityBeatsTheVideoOnes()
    {
        var master = string.Join("\n",
            "#EXTM3U",
            "#EXT-X-STREAM-INF:BANDWIDTH=64000,CODECS=\"mp4a.40.5\"",
            "low.m3u8",
            "#EXT-X-STREAM-INF:BANDWIDTH=128000,CODECS=\"mp4a.40.2\"",
            "high.m3u8",
            "#EXT-X-STREAM-INF:BANDWIDTH=50000,CODECS=\"avc1.4d401f,mp4a.40.2\"",
            "video.m3u8");

        Assert.Equal("https://cdn.example/live/high.m3u8", Hls.PickVariant(master, Base)?.AbsoluteUri);
    }

    [Fact]
    public void WithOnlyVideoQualitiesTheSmallestIsPicked()
    {
        var master = string.Join("\n",
            "#EXTM3U",
            "#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360",
            "360.m3u8",
            "#EXT-X-STREAM-INF:BANDWIDTH=200000,RESOLUTION=320x180",
            "180.m3u8",
            "#EXT-X-STREAM-INF:BANDWIDTH=1500000,RESOLUTION=1280x720",
            "720.m3u8");

        Assert.Equal("https://cdn.example/live/180.m3u8", Hls.PickVariant(master, Base)?.AbsoluteUri);
    }

    [Fact]
    public void QuotedValuesMayHoldCommas()
    {
        var attributes = Hls.Attributes("BANDWIDTH=128000,CODECS=\"mp4a.40.2,mp4a.40.5\",NAME=\"A, B\",DEFAULT=YES");

        Assert.Equal("128000", attributes["BANDWIDTH"]);
        Assert.Equal("mp4a.40.2,mp4a.40.5", attributes["CODECS"]);
        Assert.Equal("A, B", attributes["NAME"]);
        Assert.Equal("YES", attributes["DEFAULT"]);
    }

    [Fact]
    public void OnlyAnM3uWithHlsTagsIsAStreamPlaylist()
    {
        Assert.True(Hls.IsPlaylist("\n#EXTM3U\n#EXT-X-VERSION:3\n"));
        Assert.False(Hls.IsPlaylist("#EXTM3U\n#EXTINF:-1,Station\nhttp://radio.example/live\n"));
        Assert.True(Hls.IsMaster("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\na.m3u8\n"));
        Assert.False(Hls.IsMaster("#EXTM3U\n#EXT-X-TARGETDURATION:6\n"));
    }
}

public class HlsTrackerTests
{
    private static MediaPlaylist Window(long from, long to, bool hasEnd = false)
    {
        var segments = new List<Segment>();

        for (var n = from; n <= to; n++) segments.Add(new Segment(new Uri($"http://cdn.example/{n}.ts"), n, 6, null, false));

        return new MediaPlaylist(segments, 6, hasEnd, null);
    }

    private static long[] Numbers(List<Segment> segments) => segments.Select(s => s.Sequence).ToArray();

    [Fact]
    public void ALiveStationStartsAFewSegmentsBehindItsLiveEnd()
    {
        var tracker = new HlsTracker();

        Assert.Equal(new long[] { 7, 8, 9 }, Numbers(tracker.Next(Window(0, 9))));
    }

    [Fact]
    public void ARecordingStartsAtTheBeginning()
    {
        var tracker = new HlsTracker();

        Assert.Equal(new long[] { 0, 1, 2, 3, 4 }, Numbers(tracker.Next(Window(0, 4, hasEnd: true))));
    }

    [Fact]
    public void EachFetchGivesOnlyTheNewSegments()
    {
        var tracker = new HlsTracker();

        tracker.Next(Window(0, 9));

        Assert.Equal(new long[] { 10, 11 }, Numbers(tracker.Next(Window(2, 11))));
        Assert.Empty(tracker.Next(Window(2, 11)));
        Assert.Equal(new long[] { 12 }, Numbers(tracker.Next(Window(3, 12))));
    }

    [Fact]
    public void FallingFarBehindSkipsAheadToTheLiveEnd()
    {
        var tracker = new HlsTracker();

        tracker.Next(Window(0, 9));

        Assert.Equal(new long[] { 23, 24, 25 }, Numbers(tracker.Next(Window(5, 25))));
    }

    [Fact]
    public void AStationThatRestartsItsNumbersIsFollowed()
    {
        var tracker = new HlsTracker();

        tracker.Next(Window(90, 99));

        Assert.Equal(new long[] { 3, 4, 5 }, Numbers(tracker.Next(Window(0, 5))));
        Assert.Equal(new long[] { 6 }, Numbers(tracker.Next(Window(1, 6))));
    }

    [Fact]
    public void AListThatIsOnlyALittleBehindIsNotARestart()
    {
        var tracker = new HlsTracker();

        tracker.Next(Window(0, 9));

        // A server behind a cache can hand out an older copy of the list for a moment.
        Assert.Empty(tracker.Next(Window(0, 7)));
        Assert.Equal(new long[] { 10 }, Numbers(tracker.Next(Window(1, 10))));
    }
}
