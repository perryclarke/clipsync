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

internal sealed record ExcludedRow(string Key, string Title);

internal sealed record WindowContent(
    bool Paused,
    string DeviceName,
    string Fingerprint,
    IReadOnlyList<DeviceRow> Devices,
    IReadOnlyList<HiddenRow> Hidden,
    IReadOnlyList<ExcludedRow> Excluded);

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

    public const string ExcludedTitle = "Excluded apps";
    // The first sentence matches the other platforms word for word; the
    // second is the Linux caveat from the README.
    public const string ExcludedDescription =
        "Items copied while these apps are in the foreground are not sent to " +
        "your other devices. Apps are matched by WM_CLASS, so this covers " +
        "X11 and XWayland apps only.";
    public const string NoExclusions = "No apps excluded. Everything you copy is synced.";
    public const string ExcludePlaceholder = "WM_CLASS, e.g. org.keepassxc.KeePassXC";
    public const string AddLabel = "Add";
    public const string RemoveTooltip = "Remove";

    public const string GeneralTitle = "General";
    public const string LoginTitle = "Start ClipSync when you sign in";
    public const string StartOverLabel = "Start over…";
    public const string StartOverHeading = "Start over?";
    // Same wording as the other platforms, with "computer" where they say
    // "PC" / "Mac".
    public const string StartOverBody =
        "ClipSync will forget every trusted device, hidden device, excluded " +
        "app and paused peer on this computer, then restart as it was when " +
        "first installed.\n\nThis computer keeps its identity, and other " +
        "devices are not told: to reconnect, both sides will need to trust " +
        "each other again.";
    public const string StartOverConfirm = "Start over";
    public const string CancelLabel = "Cancel";

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

        var excluded = state.Excluded
            .Select(a => new ExcludedRow(a.Key, a.DisplayName))
            .ToList();

        return new WindowContent(state.Paused, deviceName, fingerprint,
                                 devices, hidden, excluded);
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
            // Every row can be hidden. The other platforms only offer hide
            // on pending peers; here a trusted machine can be hidden too —
            // hiding stays display-only (it keeps syncing), it is just not
            // listed. A deliberate deviation, noted in HANDOFF.md.
            OffersHide: true);
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
