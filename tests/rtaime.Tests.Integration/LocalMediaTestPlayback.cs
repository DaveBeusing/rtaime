// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

internal static class LocalMediaTestPlayback
{
    // Test-side synchronization only. Production admission never waits for the worker.
    internal static LocalMediaRuntimeBoundaryResult Next(LocalMediaRuntimeSession session)
    {
        var deadline = Environment.TickCount64 + 8_000;
        while (true)
        {
            var result = session.ProcessNextBoundary();
            if (result.Status != LocalMediaRuntimeBoundaryStatus.Buffering) return result;
            Assert.True(Environment.TickCount64 < deadline, $"Preroll/admission timed out: {session.DecodeSnapshot}");
            Thread.Sleep(1);
        }
    }

    internal static MediaDeckRuntimeSnapshot Next(LocalMediaDeckRuntimeService deck)
    {
        var deadline = Environment.TickCount64 + 8_000;
        while (true)
        {
            var snapshot = deck.ProcessBoundary();
            if (deck.LatestBoundary?.Status != LocalMediaRuntimeBoundaryStatus.Buffering)
            {
                if (snapshot.State == MediaDeckState.Loading) { Ready(deck); return deck.Snapshot; }
                return snapshot;
            }
            Assert.True(Environment.TickCount64 < deadline, $"Preroll/admission timed out: {deck.Snapshot.State}");
            Thread.Sleep(1);
        }
    }

    internal static void Ready(LocalMediaDeckRuntimeService deck)
    {
        Assert.True(SpinWait.SpinUntil(() => deck.Snapshot.State != MediaDeckState.Loading, TimeSpan.FromSeconds(8)));
        Assert.NotEqual(MediaDeckState.Error, deck.Snapshot.State);
    }
}
