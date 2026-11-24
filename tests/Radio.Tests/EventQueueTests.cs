using Radio.Core;
using Xunit;

namespace Radio.Tests;

public sealed class EventQueueTests
{
    private static List<PlayerEvent> Drain(EventQueue queue)
    {
        var events = new List<PlayerEvent>();

        while (queue.TryTake(out var e)) events.Add(e!);

        return events;
    }

    // A folder of thousands of broken songs fills the queue between two checks. The tab must
    // still hear that the reader gave up, or it shows Playing with no sound.
    [Fact]
    public void GivingUpIsNeverDroppedWhenTheQueueIsFull()
    {
        var queue = new EventQueue();

        for (var i = 0; i < 1000; i++) queue.Add(new PlayerEvent.SongSkipped(i, "broken"));
        queue.Add(new PlayerEvent.Failed(Trouble.NoSongs, "none played"));

        Assert.Contains(Drain(queue), e => e is PlayerEvent.Failed);
    }

    [Fact]
    public void TheEndIsNeverDroppedWhenTheQueueIsFull()
    {
        var queue = new EventQueue();

        for (var i = 0; i < 1000; i++) queue.Add(new PlayerEvent.Note("line"));
        queue.Add(new PlayerEvent.Ended());

        Assert.Contains(Drain(queue), e => e is PlayerEvent.Ended);
    }

    [Fact]
    public void OtherEventsStopPilingUpOnceTheQueueIsFull()
    {
        var queue = new EventQueue();

        for (var i = 0; i < 1000; i++) queue.Add(new PlayerEvent.Note("line"));

        Assert.InRange(Drain(queue).Count, 1, 256);
    }
}
