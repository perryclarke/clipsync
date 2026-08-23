using System;
using System.Collections.Generic;
using System.Linq;
using ClipSync.Net;

namespace ClipSync.Ui;

/// One device row in the window: what it says and which controls it gets.
internal sealed record DeviceRow(
    string Did,
    string Title,
    string Subtitle,
    bool OffersTrust,
    bool OffersSendSwitch,
    bool Sending,
    bool OffersHide);

internal sealed record HiddenRow(string Did, string Title);

internal sealed record WindowContent(
    bool Paused,
    string DeviceName,
    string Fingerprint,
    IReadOnlyList<DeviceRow> Devices,
    IReadOnlyList<HiddenRow> Hidden);

/// Maps daemon state to the window's rows and wording. Pure on purpose:
/// GTK is untestable here, and what regresses silently is the shape and
/// the words, so those live where the tests can reach them. MainWindow
/// renders this and adds nothing of its own.
internal static class WindowModel
{
    public const string PauseTitle = "Pause Syncing";
    public const string PauseSubtitle =
        "Nothing is sent while paused. Items from other devices still arrive.";
    public const string DevicesTitle = "Devices";
    public const string HiddenTitle = "Hidden devices";
    public const string NoDevices = "No devices found";
    public const string TrustLabel = "Trust";
    public const string ShowLabel = "Show";
    public const string HideTooltip = "Hide this device";
    public const string SendTooltip = "Send to this device";

    public static WindowContent Build(TrayState state, string deviceName, string fingerprint)
    {
        var hiddenDids = state.Hidden.Select(h => h.DidHex)
                                     .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var devices = state.Peers
            .Where(p => !hiddenDids.Contains(p.DidHex))
            .Select(p => Row(p, state.IsMuted(p.DidHex)))
            .ToList();

        var hidden = state.Hidden
            .Select(h => new HiddenRow(h.DidHex, h.Name))
            .ToList();

        return new WindowContent(state.Paused, deviceName, fingerprint, devices, hidden);
    }

    private static DeviceRow Row(Peer peer, bool muted)
    {
        var pending = peer.State == PeerState.Pending;
        return new DeviceRow(
            Did: peer.DidHex,
            Title: peer.Name,
            Subtitle: Describe(peer, muted),
            OffersTrust: pending,
            OffersSendSwitch: !pending,
            Sending: !muted,
            // Hiding is for dismissing an unwanted pending peer; a trusted
            // device is managed with its send switch instead, matching the
            // other platforms.
            OffersHide: pending);
    }

    /// Subtitle wording. The fingerprint is on the pending row because
    /// eyeballing it against the other device is the only verification
    /// two-sided TOFU offers. A muted peer reads "Paused" rather than
    /// "Online": the reason nothing reaches it is the mute, not the
    /// network.
    private static string Describe(Peer peer, bool muted) => peer.State switch
    {
        PeerState.Pending => $"Waiting to be trusted — {peer.DidHex[..8]}",
        _ when muted => "Paused",
        PeerState.Online => peer.Version is { } v ? $"Online, {v}" : "Online",
        PeerState.Looking => "Looking…",
        _ => "Offline",
    };
}
