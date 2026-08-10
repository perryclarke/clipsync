using System;
using ClipSync.Clipboard;
using ClipSync.Settings;
using Xunit;

namespace ClipSync.Tests;

public class ForegroundRingTests
{
    private static readonly DateTime T0 = new(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);

    private static AppIdentity App(string name) => new(AppKind.Exe, name + ".exe", name);

    [Fact]
    public void EmptyRingReturnsNull()
    {
        Assert.Null(new ForegroundRing().AppAt(T0));
    }

    [Fact]
    public void TimestampBeforeOldestEntryReturnsNull()
    {
        var ring = new ForegroundRing();
        ring.Record(T0, App("a"));
        Assert.Null(ring.AppAt(T0.AddSeconds(-1)));
    }

    [Fact]
    public void TimestampExactlyOnTransitionResolvesToNewlyActivatedApp()
    {
        var ring = new ForegroundRing();
        ring.Record(T0, App("a"));
        ring.Record(T0.AddSeconds(10), App("b"));

        Assert.Equal(App("b"), ring.AppAt(T0.AddSeconds(10)));
    }

    [Fact]
    public void TimestampInsideAnIntervalResolvesToThatIntervalsApp()
    {
        var ring = new ForegroundRing();
        ring.Record(T0, App("a"));
        ring.Record(T0.AddSeconds(10), App("b"));

        Assert.Equal(App("a"), ring.AppAt(T0.AddSeconds(5)));
        Assert.Equal(App("a"), ring.AppAt(T0.AddSeconds(9.999)));
    }

    [Fact]
    public void NewestEntryExtendsIndefinitely()
    {
        var ring = new ForegroundRing();
        ring.Record(T0, App("a"));
        Assert.Equal(App("a"), ring.AppAt(T0.AddHours(3)));
    }

    [Fact]
    public void NullIdentityIsRecordedAndReturned()
    {
        // An unresolvable window (e.g. an elevated app) records as null,
        // which the caller treats as fail-open.
        var ring = new ForegroundRing();
        ring.Record(T0, null);
        Assert.Null(ring.AppAt(T0.AddSeconds(1)));
    }

    [Fact]
    public void RingKeepsAtMostMaxEntries()
    {
        var ring = new ForegroundRing();
        for (int i = 0; i <= ForegroundRing.MaxEntries; i++)
            ring.Record(T0.AddSeconds(i), App("app" + i));

        // The oldest was evicted, so its interval no longer resolves.
        Assert.Null(ring.AppAt(T0));
        Assert.Equal(App("app1"), ring.AppAt(T0.AddSeconds(1)));
    }

    [Fact]
    public void EntriesOlderThanMaxAgeAreEvicted()
    {
        var ring = new ForegroundRing();
        ring.Record(T0, App("old"));
        var later = T0 + ForegroundRing.MaxAge + TimeSpan.FromSeconds(1);
        ring.Record(later, App("new"));

        Assert.Null(ring.AppAt(T0.AddSeconds(1)));
        Assert.Equal(App("new"), ring.AppAt(later));
    }

    [Fact]
    public void SoleEntryIsNotEvictedByAgeAlone()
    {
        // A user who has stayed in one app for an hour must still resolve.
        var ring = new ForegroundRing();
        ring.Record(T0, App("a"));
        Assert.Equal(App("a"), ring.AppAt(T0 + TimeSpan.FromHours(1)));
    }
}
