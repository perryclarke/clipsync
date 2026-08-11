using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ClipSync.Settings;

namespace ClipSync.UI;

public sealed partial class SettingsWindow : Window
{
    /// Long enough to Alt-Tab or click into the target app without being a
    /// wait; short enough that the user does not lose track of what it is
    /// counting down to.
    private const int CaptureSeconds = 5;

    private static SettingsWindow? _instance;

    private CancellationTokenSource? _capture;
    private bool _closed;

    public SettingsWindow()
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            _instance = null;
            _closed = true;
            // A countdown outliving its window would resume onto disposed
            // XAML elements.
            _capture?.Cancel();
        };
        // Constructors can't await; RefreshList fetches icons on a background
        // thread and populates the list once that completes. It handles its
        // own failures, so discarding the Task loses nothing.
        _ = RefreshList();
    }

    /// One settings window at a time; re-activate the existing one.
    public static void ShowSingleton()
    {
        _instance ??= new SettingsWindow();
        _instance.Activate();
    }

    public async Task RefreshList()
    {
        try
        {
            var excluded = App.Current.Settings.Excluded;

            // IconBytesForExecutable does disk I/O (Icon.ExtractAssociatedIcon +
            // PNG encoding); keep it off the UI thread. ToImageSource must run on
            // the UI thread, so only that half happens back here after the await.
            //
            // Nothing on screen is touched until the icons are in hand: clearing
            // first and throwing later is exactly how this window ended up
            // permanently blank.
            var paths = excluded.Select(a => a.Path).ToList();
            var icons = await Task.Run(() =>
                paths.Select(p => p is null ? null : InstalledApps.IconBytesForExecutable(p)).ToList());

            if (_closed) return;

            ExcludedList.Items.Clear();
            EmptyText.Visibility = excluded.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            for (int i = 0; i < excluded.Count; i++)
                ExcludedList.Items.Add(BuildRow(excluded[i], icons[i]));
        }
        catch (Exception ex)
        {
            // Previously unobserved: the Task was discarded in the
            // constructor, so a throw here left a blank window and no log.
            Security.Identity.Log($"Settings: refreshing the excluded list failed: " +
                                  $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private UIElement BuildRow(AppIdentity app, byte[]? iconBytes)
    {
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Image
        {
            Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center,
            Source = InstalledApps.ToImageSource(iconBytes),
        };
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = app.DisplayName, FontSize = 14 });
        text.Children.Add(new TextBlock
        {
            Text = app.Path ?? app.Key,
            FontSize = 11,
            Foreground = SecondaryTextBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var remove = new Button { Content = "Remove", VerticalAlignment = VerticalAlignment.Center };
        remove.Click += async (_, _) =>
        {
            App.Current.Settings.Remove(app);
            Security.Identity.Log($"Settings: removed exclusion {app.DisplayName}");
            await RefreshList();
        };
        Grid.SetColumn(remove, 2);
        row.Children.Add(remove);

        return row;
    }

    /// The theme's muted foreground when it is there, a literal otherwise.
    /// A hard `(Brush)Resources[...]` cast would throw on a theme that does
    /// not define the key, and this runs while building a row that has
    /// already been committed to.
    private static Brush SecondaryTextBrush()
    {
        if (Application.Current.Resources.TryGetValue("SystemControlForegroundBaseMediumBrush", out var value)
            && value is Brush brush)
            return brush;
        return new SolidColorBrush(Colors.Gray);
    }

    private async void OnAddApp(object sender, RoutedEventArgs e)
    {
        try
        {
            var picked = await AppPickerDialog.PickAsync(Content.XamlRoot, this);
            if (picked is null) return;

            App.Current.Settings.Add(picked);
            Security.Identity.Log($"Settings: added exclusion {picked.DisplayName}");
            // RefreshList became Task-returning in Task 7 (icon prefetch moved
            // off the UI thread), so it must be awaited.
            await RefreshList();
        }
        catch (Exception ex)
        {
            Security.Identity.Log($"Settings: add failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// Exclude whatever app is in the foreground when the countdown ends.
    ///
    /// The picker cannot get this right for every app: it keys a Win32 entry
    /// on its Start Menu shortcut's target, and a launcher-based app
    /// (Proton VPN's ProtonVPN.Launcher.exe, anything Squirrel-packaged)
    /// runs its UI from a different executable, so the stored key never
    /// matches and the exclusion silently never fires. Reading the identity
    /// back out of ForegroundTracker -- the very object the clipboard
    /// watcher consults -- cannot disagree with copy-time matching.
    ///
    /// The countdown exists because ClipSync must not be the foreground app
    /// at the moment of capture.
    private async void OnCaptureCurrentApp(object sender, RoutedEventArgs e)
    {
        // A second click cancels; it never starts an overlapping capture.
        if (_capture is { } running)
        {
            running.Cancel();
            return;
        }

        var cts = new CancellationTokenSource();
        _capture = cts;
        CaptureButton.Content = "Cancel";
        AddButton.IsEnabled = false;

        try
        {
            for (var left = CaptureSeconds; left > 0; left--)
            {
                CaptureStatus.Text = $"Switch to the app you want to exclude… {left}";
                await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
            }

            var app = App.Current.Foreground.Current;
            if (app is null || IsClipSyncItself(app))
            {
                CaptureStatus.Text = "Couldn't identify the app in the foreground. Nothing was added.";
                Security.Identity.Log("Settings: capture found no identifiable foreground app; added nothing");
                return;
            }

            if (App.Current.Settings.IsExcluded(app))
            {
                CaptureStatus.Text = $"{app.DisplayName} is already excluded.";
                return;
            }

            App.Current.Settings.Add(app);
            Security.Identity.Log($"Settings: added exclusion {app.DisplayName} by foreground capture");
            CaptureStatus.Text = $"Excluded {app.DisplayName}.";
            await RefreshList();
        }
        catch (OperationCanceledException)
        {
            if (!_closed) CaptureStatus.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            Security.Identity.Log($"Settings: capture failed: {ex.GetType().Name}: {ex.Message}");
            if (!_closed) CaptureStatus.Text = "Couldn't identify the app in the foreground. Nothing was added.";
        }
        finally
        {
            _capture = null;
            cts.Dispose();
            if (!_closed)
            {
                CaptureButton.Content = "Exclude current app…";
                AddButton.IsEnabled = true;
            }
        }
    }

    /// Belt and braces. ForegroundTracker registers its hook with
    /// WINEVENT_SKIPOWNPROCESS, so ClipSync's own windows never enter the
    /// ring, but excluding ourselves would be a confusing no-op if they did.
    private static bool IsClipSyncItself(AppIdentity app)
    {
        if (app.Kind != AppKind.Exe) return false;
        var self = System.IO.Path.GetFileName(Environment.ProcessPath ?? "");
        return self.Length > 0 && string.Equals(app.Key, self, StringComparison.OrdinalIgnoreCase);
    }
}
