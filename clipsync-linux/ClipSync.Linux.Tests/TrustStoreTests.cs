using System;
using System.IO;
using System.Text;
using ClipSync.Security;
using Xunit;

namespace ClipSync.Linux.Tests;

/// The trust store decides which peers may connect at all, so its failure
/// modes matter as much as its happy path. Mirrors the behaviour of the
/// Windows implementation, including the deliberate choice to degrade a
/// corrupt file to empty rather than throw.
public class TrustStoreTests
{
    private const string DidA = "aa11bb22cc33dd44ee55ff6600778899aabbccddeeff00112233445566778899";
    private const string DidB = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef";

    [Fact]
    public void NewStore_IsEmpty()
    {
        using var tmp = new TempStore();
        Assert.True(TrustStore.Load(tmp.Store).IsEmpty);
    }

    [Fact]
    public void Add_ThenContains_AndPersistsAcrossReload()
    {
        using var tmp = new TempStore();
        TrustStore.Load(tmp.Store).Add(DidA, "Kodachrome");

        var reloaded = TrustStore.Load(tmp.Store);
        Assert.True(reloaded.Contains(DidA));
        Assert.False(reloaded.IsEmpty);
        Assert.Equal("Kodachrome", Assert.Single(reloaded.All()).Name);
    }

    /// Peers are keyed by lowercase hex; a did that arrives upper-cased from
    /// a TXT record or a UI must still match.
    [Fact]
    public void Contains_IsCaseInsensitive()
    {
        using var tmp = new TempStore();
        var store = TrustStore.Load(tmp.Store);
        store.Add(DidA.ToUpperInvariant(), "Shouty");

        Assert.True(store.Contains(DidA));
        Assert.True(store.Contains(DidA.ToUpperInvariant()));
    }

    /// The byte[] overload is what PeerConnection calls with a hash computed
    /// straight off the peer certificate during the TLS handshake.
    [Fact]
    public void Contains_AcceptsRawHashBytes()
    {
        using var tmp = new TempStore();
        var store = TrustStore.Load(tmp.Store);
        store.Add(DidA, "Kodachrome");

        Assert.True(store.Contains(Convert.FromHexString(DidA)));
        Assert.False(store.Contains(Convert.FromHexString(DidB)));
    }

    [Fact]
    public void Remove_DropsOnlyThatPeer()
    {
        using var tmp = new TempStore();
        var store = TrustStore.Load(tmp.Store);
        store.Add(DidA, "A");
        store.Add(DidB, "B");

        store.Remove(DidA);

        Assert.False(store.Contains(DidA));
        Assert.True(store.Contains(DidB));
    }

    [Fact]
    public void Clear_EmptiesTheStoreOnDisk()
    {
        using var tmp = new TempStore();
        var store = TrustStore.Load(tmp.Store);
        store.Add(DidA, "A");
        store.Add(DidB, "B");

        store.Clear();

        Assert.True(store.IsEmpty);
        Assert.True(TrustStore.Load(tmp.Store).IsEmpty);
    }

    /// Add is an upsert: re-trusting a peer under a new advertised name
    /// updates it rather than creating a duplicate entry.
    [Fact]
    public void Add_SameDidTwice_UpdatesRatherThanDuplicates()
    {
        using var tmp = new TempStore();
        var store = TrustStore.Load(tmp.Store);
        store.Add(DidA, "Old name");
        store.Add(DidA, "New name");

        Assert.Equal("New name", Assert.Single(store.All()).Name);
    }

    /// A corrupt file must not stop the app starting. Losing trust is
    /// visible and recoverable — the user clicks Trust again — whereas
    /// throwing here would mean the daemon simply never comes up.
    [Fact]
    public void CorruptFile_DegradesToEmpty_RatherThanThrowing()
    {
        using var tmp = new TempStore();
        tmp.Store.Save("trust.json", Encoding.UTF8.GetBytes("{ this is not json"));

        var store = TrustStore.Load(tmp.Store);

        Assert.True(store.IsEmpty);
        store.Add(DidA, "recovered");           // and remains usable afterwards
        Assert.True(TrustStore.Load(tmp.Store).Contains(DidA));
    }

    [Fact]
    public void TrustFile_IsOwnerOnly()
    {
        using var tmp = new TempStore();
        TrustStore.Load(tmp.Store).Add(DidA, "A");

        var mode = File.GetUnixFileMode(Path.Combine(tmp.Dir, "trust.json"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }
}
