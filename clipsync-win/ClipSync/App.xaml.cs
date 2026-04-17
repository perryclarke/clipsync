using Microsoft.UI.Xaml;
using ClipSync.Clipboard;
using ClipSync.Net;
using ClipSync.Security;
using ClipSync.UI;

namespace ClipSync;

public partial class App : Application
{
    public static App Current => (App)Application.Current;

    public Identity Identity { get; private set; } = null!;
    public TrustStore TrustStore { get; private set; } = null!;
    public PeerRegistry Peers { get; private set; } = null!;
    public ClipboardWatcher Watcher { get; private set; } = null!;
    public ClipboardWriter Writer { get; private set; } = null!;
    public Discovery Discovery { get; private set; } = null!;
    public TrayIcon Tray { get; private set; } = null!;

    public App() { InitializeComponent(); }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Identity = Identity.LoadOrCreate();
        TrustStore = TrustStore.Load();
        Peers = new PeerRegistry();
        Writer = new ClipboardWriter();
        Watcher = new ClipboardWatcher(Writer);
        Discovery = new Discovery(Identity, TrustStore, Peers);

        Watcher.OnLocalCopy = item => Peers.Broadcast(item);
        Peers.OnRemoteItem = item => Writer.Apply(item);

        Watcher.Start();
        Discovery.Start();

        Tray = new TrayIcon();
        Tray.Show();
    }
}
