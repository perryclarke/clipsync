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
