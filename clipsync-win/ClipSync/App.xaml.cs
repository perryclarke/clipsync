using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using ClipSync.Clipboard;
using ClipSync.Net;
using ClipSync.Security;
using ClipSync.Settings;
using ClipSync.Sync;
using ClipSync.UI;

namespace ClipSync;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;
    public static DispatcherQueue UIDispatcher { get; private set; } = null!;

    public Identity Identity { get; private set; } = null!;
    public TrustStore TrustStore { get; private set; } = null!;
    public PeerRegistry Peers { get; private set; } = null!;
    public ClipboardWatcher Watcher { get; private set; } = null!;
    public ClipboardWriter Writer { get; private set; } = null!;
    public Discovery Discovery { get; private set; } = null!;
    public TrayIcon Tray { get; private set; } = null!;
    public AppSettings Settings { get; private set; } = null!;
    public ForegroundTracker Foreground { get; private set; } = null!;
    public SyncPause Pause { get; private set; } = null!;

    public App() { InitializeComponent(); }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            UIDispatcher = DispatcherQueue.GetForCurrentThread();
            Identity = Identity.LoadOrCreate();
            // Route Core's diagnostics into the same opt-in debug log.
            ClipSync.Security.Log.Sink = Identity.Log;
            TrustStore = TrustStore.Load();
            Settings = AppSettings.Load();
            Peers = new PeerRegistry(Identity.DidHex);
            Writer = new ClipboardWriter();
            Foreground = new ForegroundTracker();
            Watcher = new ClipboardWatcher(Writer, Foreground, Settings);
            Discovery = new Discovery(Identity, TrustStore, Peers);

            Pause = new SyncPause(Settings);
            // Sending is gated per peer; receiving deliberately is not, so a
            // paused device still takes what its peers send it.
            Peers.ShouldSendTo = did => Pause.ShouldSendTo(did);

            Watcher.OnLocalCopy = item => Peers.Broadcast(item);
            Peers.OnRemoteItem = item => Writer.Apply(item);

            // Before the watcher, so the seed entry predates any copy.
            Foreground.Start();
            Watcher.Start();
            Discovery.Start();

            Tray = new TrayIcon();
            Tray.Show();
        }
        catch (System.Exception ex)
        {
            var log = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "ClipSync", "crash.log");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(log)!);
            System.IO.File.WriteAllText(log, $"{System.DateTime.Now}\n{ex}\n");
        }
    }
}
