using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ClipSync;

public static class Program
{
    [System.STAThread]
    public static void Main(string[] args)
    {
        try
        {
            // Command-line switches, handled before the app (and its trust
            // store) start up. `--debug` (also `-d` / `/debug`) turns on
            // diagnostic logging without the CLIPSYNC_DEBUG env var or marker
            // file. `--reset` (also `/reset`) forgets all trusted peers so the
            // user must re-approve connections.
            bool debug = false, reset = false;
            foreach (var a in args)
            {
                if (a is "--debug" or "-d" or "/debug") debug = true;
                else if (a is "--reset" or "/reset") reset = true;
            }
            if (debug)
            {
                ClipSync.Security.Identity.EnableLogging();
                ClipSync.Security.Identity.Log("Program: debug logging enabled via command line");
            }
            if (reset)
            {
                ClipSync.Security.Identity.Log("Program: --reset clearing trusted peers");
                try { ClipSync.Security.TrustStore.Load().Clear(); }
                catch (System.Exception ex) { ClipSync.Security.Identity.Log($"Program: --reset failed: {ex.Message}"); }
            }

            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(p =>
            {
                var ctx = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                System.Threading.SynchronizationContext.SetSynchronizationContext(ctx);
                _ = new App();
            });
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
