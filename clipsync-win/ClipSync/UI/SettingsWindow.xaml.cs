using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using ClipSync.Settings;
using WinRT.Interop;

namespace ClipSync.UI;

/// One row of the excluded list, ready to display.
///
/// Public and property-bearing because the row template binds to it with
/// `x:Bind`, which compiles against the declared type.
public sealed class ExcludedAppRow
{
    /// Must be constructed on the UI thread: `ToImageSource` and the
    /// `IconElement` below are both `DependencyObject`s.
    public ExcludedAppRow(AppIdentity app, byte[]? iconPng)
    {
        Identity = app;
        Name = app.DisplayName;
        // The path is the useful disambiguator when it exists; a packaged app
        // only ever has its family name.
        Detail = app.Path ?? app.Key;
        // Screen readers get a row's worth of context on a button whose label
        // is a bin glyph; "Remove" alone would be ambiguous in a list where
        // every row has one.
        RemoveLabel = $"Stop excluding {Name}";

        var source = InstalledApps.ToImageSource(iconPng);
        // Packaged apps captured from the foreground carry no icon, and the
        // shell has none to give for some Win32 targets either; a generic app
        // glyph reads better than a hole in the row.
        HeaderIcon = source is null
            ? new FontIcon { Glyph = "\uECAA" }
            : new ImageIcon { Source = source };
    }

    public AppIdentity Identity { get; }
    public string Name { get; }
    public string Detail { get; }
    public string RemoveLabel { get; }

    /// One element per row, built once. Safe as a single instance because
    /// SettingsExpander's items are not virtualised, so it is never asked to
    /// live under two parents at the same time.
    public IconElement HeaderIcon { get; }
}

public sealed partial class SettingsWindow : Window
{
    /// Long enough to Alt-Tab or click into the target app without being a
    /// wait; short enough that the user does not lose track of what it is
    /// counting down to.
    private const int CaptureSeconds = 5;

    /// WinUI 3 has no SizeToContent, and the ~1024x768 default it otherwise
    /// falls back to is several times the area this content needs.
    ///
    /// The width is not free to shrink further: SettingsCard drops its
    /// content below the header once the card is narrower than
    /// `SettingsCardWrapThreshold` (476), so anything under 476 + the
    /// ScrollViewer's 40 of padding turns every row into the stacked
    /// small-screen layout. The height fits the group header, two app rows
    /// and the add row before the page starts scrolling.
    private const int WidthDip = 560;
    private const int HeightDip = 408;

    /// Breathing room between this window and the tray popup it sits beside.
    private const int GapDip = 8;

    private static SettingsWindow? _instance;

    private CancellationTokenSource? _capture;
    private bool _closed;

    public SettingsWindow()
    {
        InitializeComponent();
        ConfigureWindow();
        Activated += OnFirstActivated;
        Closed += (_, _) =>
        {
            _instance = null;
            _closed = true;
            // A countdown outliving its window would resume onto disposed
            // XAML elements.
            _capture?.Cancel();
        };
        // Esc is what people press to dismiss a settings window that opened
        // out of a tray menu; there is no Cancel button to reach for.
        var escape = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, args) => { args.Handled = true; Close(); };
        Root.KeyboardAccelerators.Add(escape);
        // Constructors can't await; RefreshList fetches icons on a background
        // thread and populates the list once that completes. It handles its
        // own failures, so discarding the Task loses nothing.
        _ = RefreshList();
    }

    /// One settings window at a time; re-activate the existing one.
    ///
    /// `anchor` is the tray popup's bounds in physical pixels, so this window
    /// can sit beside it rather than on top of it.
    public static void ShowSingleton(RectInt32? anchor = null)
    {
        var created = _instance is null;
        _instance ??= new SettingsWindow();

        // Only on first show: moving a window the user has already dragged
        // somewhere would be the tray dictating their layout.
        if (created && anchor is { } rect)
        {
            // Twice, deliberately. Placing it here is what stops activation
            // flashing the window up at whatever default spot WinUI picked.
            // But the resize-border correction depends on the window's DPI,
            // and before it has been shown that is the DPI of whichever
            // monitor Windows guessed -- which is the wrong one whenever the
            // tray is on the other display. The handler below repeats the
            // placement once the window is really on screen and the DPI is
            // settled; with the same inputs it is a no-op.
            _instance._anchor = rect;
            _instance.PositionBeside(rect);
        }

        _instance.Activate();
    }

    private RectInt32? _anchor;

    private void OnFirstActivated(object sender, WindowActivatedEventArgs e)
    {
        Activated -= OnFirstActivated;
        if (_anchor is { } rect) PositionBeside(rect);
    }

    /// Place this window alongside `anchor` without covering it.
    ///
    /// Both rectangles are the windows' *visible* frames in physical pixels,
    /// on whichever display holds the anchor. The side with more room wins;
    /// when neither side can take the whole window the anchor is a lost cause
    /// and we centre instead, which is the "screen too small to show both"
    /// case.
    private void PositionBeside(RectInt32 anchor)
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));

            // Everything below is computed against the visible frame, not the
            // window rect, and converted back at the Move. This window is
            // resizable and the tray popup is not, so their invisible borders
            // differ; aligning the rects leaves the visible edges out by the
            // width of ours, which is the handful of pixels the bottoms were
            // off by. See WindowFrame, which returns these already converted
            // into AppWindow's units so nothing here mixes coordinate spaces.
            var size = appWindow.Size;
            var (insetLeft, insetTop, insetRight, insetBottom) = WindowFrame.ResizeBorder(hwnd);
            var visibleWidth = size.Width - insetLeft - insetRight;
            var visibleHeight = size.Height - insetTop - insetBottom;

            var display = DisplayArea.GetFromPoint(
                new PointInt32(anchor.X + anchor.Width / 2, anchor.Y + anchor.Height / 2),
                DisplayAreaFallback.Nearest);
            var work = display.WorkArea;

            var gap = (int)(GapDip * (GetDpiForWindow(hwnd) / 96.0));
            var leftRoom = anchor.X - work.X;
            var rightRoom = (work.X + work.Width) - (anchor.X + anchor.Width);

            int x;
            if (rightRoom >= visibleWidth + gap && rightRoom >= leftRoom)
                x = anchor.X + anchor.Width + gap;
            else if (leftRoom >= visibleWidth + gap)
                x = anchor.X - gap - visibleWidth;
            else
                // Neither flank fits. Overlapping is now unavoidable, so stop
                // pretending and centre on the work area.
                x = work.X + (work.Width - visibleWidth) / 2;

            // Bottom-aligned with the popup, which itself sits above the
            // taskbar: two panels standing on the same line read as a pair.
            var y = anchor.Y + anchor.Height - visibleHeight;

            x = Math.Clamp(x, work.X, Math.Max(work.X, work.X + work.Width - visibleWidth));
            y = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Y + work.Height - visibleHeight));

            appWindow.Move(new PointInt32(x - insetLeft, y - insetTop));
        }
        catch (Exception ex)
        {
            // A settings window in the middle of the screen is a worse
            // outcome than one beside the popup, not a broken one.
            Security.Identity.Log($"Settings: positioning failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// Size to the content and pick a backdrop.
    ///
    /// AppWindow.Resize is in physical pixels, so the DIP figures above have
    /// to be scaled by the monitor's DPI; XamlRoot.RasterizationScale is null
    /// this early, which is why this asks Win32 directly.
    private void ConfigureWindow()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));

            var scale = GetDpiForWindow(hwnd) / 96.0;
            appWindow.Resize(new SizeInt32((int)(WidthDip * scale), (int)(HeightDip * scale)));

            // No system title bar: the content runs to the top of the window
            // and TitleBarDrag stands in as the drag region. Without the
            // SetTitleBar call the window would be undraggable, since there
            // is no caption left to grab.
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleBarDrag);

            // Match the strip to the caption buttons rather than guessing at
            // it. The title is vertically centred in this strip, so any
            // height other than the real one leaves it sitting above or below
            // the minimise/maximise/close row. RightInset is the width the
            // system reserved for those buttons, which is the one number that
            // must not be hard-coded: it changes with DPI, with the
            // PreferredHeightOption, and it moves to the left in RTL.
            //
            // Both are physical pixels; the strip is laid out in DIPs.
            var bar = appWindow.TitleBar;
            if (bar.Height > 0) TitleBarDrag.Height = bar.Height / scale;
            TitleBarDrag.Padding = new Thickness(
                16 + bar.LeftInset / scale, 0, 16 + bar.RightInset / scale, 0);

            // Maximised, this window is a small card of content adrift in a
            // full screen of empty space. Resizing stays available.
            if (appWindow.Presenter is OverlappedPresenter presenter)
                presenter.IsMaximizable = false;

            if (MicaController.IsSupported())
            {
                SystemBackdrop = new MicaBackdrop();
            }
            else if (Application.Current.Resources.TryGetValue(
                         "ApplicationPageBackgroundThemeBrush", out var page) && page is Brush brush)
            {
                // No Mica means no backdrop at all, and a transparent root
                // would render as a black hole rather than a window.
                Root.Background = brush;
            }
        }
        catch (Exception ex)
        {
            // Cosmetic to a fault: a settings window at the wrong size still
            // works, so nothing here is worth failing construction over.
            Security.Identity.Log($"Settings: window setup failed: {ex.GetType().Name}: {ex.Message}");
        }
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

            var rows = new List<ExcludedAppRow>(excluded.Count);
            for (int i = 0; i < excluded.Count; i++)
                rows.Add(new ExcludedAppRow(excluded[i], icons[i]));

            ExcludedList.ItemsSource = rows;
            CapListHeight(rows.Count);
            EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            GroupHeader.Description = rows.Count == 0
                ? "Items copied while these apps are in the foreground are not sent to your other devices."
                : $"{rows.Count} app{(rows.Count == 1 ? "" : "s")} excluded. " +
                  "Items copied while they are in the foreground are not sent to your other devices.";
        }
        catch (Exception ex)
        {
            // Previously unobserved: the Task was discarded in the
            // constructor, so a throw here left a blank window and no log.
            Security.Identity.Log($"Settings: refreshing the excluded list failed: " +
                                  $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// How many rows the list shows before it starts scrolling. The half is
    /// the point: a whole number of rows looks like the whole list, and there
    /// is then nothing on screen to say that scrolling would reveal more.
    private const double VisibleRows = 2.5;

    /// Nominal row height, used until a real one can be measured. Matches
    /// SettingsCard's default; a row whose path wraps to two lines is taller.
    private const double FallbackRowHeight = 68;

    /// Cap the list at `VisibleRows`, measuring a realised row so the cap
    /// follows the theme's metrics and the actual wrapped height rather than
    /// a number written here.
    ///
    /// The measurement has to wait for a layout pass -- containers do not
    /// exist the instant ItemsSource is assigned -- so this runs itself again
    /// off the dispatcher once there is something to measure.
    private void CapListHeight(int count, int attempt = 0)
    {
        if (count == 0)
        {
            ExcludedList.MaxHeight = double.PositiveInfinity;
            return;
        }

        var row = ExcludedList.ContainerFromIndex(0) as FrameworkElement;
        var height = row?.ActualHeight ?? 0;

        if (height <= 0)
        {
            // Nothing realised yet. Take the nominal height for now so the
            // window never opens with an uncapped list, and come back once
            // the layout pass has happened. Bounded, because rescheduling
            // itself on a condition that never comes true is a spin, not a
            // retry -- and the fallback below is a perfectly usable answer.
            ExcludedList.MaxHeight = FallbackRowHeight * VisibleRows;
            if (attempt < 5)
                DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () => { if (!_closed) CapListHeight(count, attempt + 1); });
            return;
        }

        ExcludedList.MaxHeight = height * VisibleRows;
    }

    /// The row's identity travels on the button's inherited DataContext, which
    /// the items control sets on each container.
    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ExcludedAppRow row }) return;

        App.Current.Settings.Remove(row.Identity);
        Security.Identity.Log($"Settings: removed exclusion {row.Identity.DisplayName}");
        await RefreshList();
        // Removing a row destroys the button that had focus, and focus lands
        // wherever the framework leaves it. Sending it somewhere deliberate
        // keeps a keyboard user oriented.
        AddButton.Focus(FocusState.Programmatic);
        Announce($"Stopped excluding {row.Identity.DisplayName}");
    }

    /// `announce` is for the settled outcome of an action, not for the
    /// countdown: the ticking message updates once a second, and a live
    /// region would read every tick aloud.
    private void ShowStatus(InfoBarSeverity severity, string message, bool announce = true)
    {
        CaptureStatus.Severity = severity;
        CaptureStatus.Message = message;
        CaptureStatus.IsOpen = true;
        if (announce) Announce(message);
    }

    /// Screen-reader announcement for something that happened without the
    /// user moving focus. InfoBar announces itself when it opens, but not
    /// when an already-open bar changes its message, which is exactly the
    /// countdown-then-result shape used here.
    private void Announce(string message)
    {
        try
        {
            var peer = FrameworkElementAutomationPeer.FromElement(CaptureStatus)
                       ?? FrameworkElementAutomationPeer.CreatePeerForElement(CaptureStatus);
            peer?.RaiseNotificationEvent(
                AutomationNotificationKind.ActionCompleted,
                AutomationNotificationProcessing.MostRecent,
                message,
                "ClipSyncSettingsStatus");
        }
        catch (Exception ex)
        {
            // No assistive tech listening, or no peer yet. Never worth
            // failing the action that triggered it.
            Security.Identity.Log($"Settings: announcement failed: {ex.GetType().Name}");
        }
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
            ShowStatus(InfoBarSeverity.Success, $"Excluded {picked.DisplayName}.");
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
        // The menu item is disabled while a capture runs, so this is the
        // belt to that braces: whatever route gets here twice, it must never
        // start a second countdown racing the first.
        if (_capture is not null) return;

        var cts = new CancellationTokenSource();
        _capture = cts;
        CaptureItem.IsEnabled = false;

        // The countdown's own cancel affordance lives on the status bar
        // rather than in the menu that started it: that menu is closed by the
        // time the countdown is running, and the bar is the only thing on
        // screen that says a countdown exists at all.
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => cts.Cancel();
        CaptureStatus.ActionButton = cancel;

        try
        {
            // Sampled before the countdown so we can tell afterwards whether
            // focus moved at all. See the check below.
            var transitionsBefore = App.Current.Foreground.Transitions;

            for (var left = CaptureSeconds; left > 0; left--)
            {
                // announce:false — a live region here would read the ticking
                // number aloud once a second. The opening announcement below
                // says what is about to happen; the result announces itself.
                ShowStatus(InfoBarSeverity.Informational,
                           $"Switch to the app you want to exclude… {left}",
                           announce: false);
                if (left == CaptureSeconds)
                    Announce($"Switch to the app you want to exclude within {CaptureSeconds} seconds.");
                await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
            }

            // The window can close during that last second: Cancel() then
            // raises nothing, and without this the code below would persist
            // an exclusion and only afterwards throw on a torn-down element,
            // logging a failure for an add that actually happened.
            if (_closed) return;

            // Nothing focused during the countdown means the user watched it
            // run without switching. The hook is registered
            // WINEVENT_SKIPOWNPROCESS, so ClipSync's own windows never enter
            // the ring and `Current` would hand back whatever was in front
            // before the settings window -- realistically explorer.exe, since
            // getting here means clicking the tray and then Settings.
            // Excluding that would silently stop syncing every File Explorer
            // and desktop copy, with nothing tying cause to effect.
            //
            // Counting transitions rather than comparing identities is what
            // makes this exact: switching away and back records two, never
            // switching records none, and those are indistinguishable by
            // identity alone.
            if (App.Current.Foreground.Transitions == transitionsBefore)
            {
                ShowStatus(InfoBarSeverity.Warning,
                           "Nothing added — switch to the app you want to exclude " +
                           "while the countdown is running, then try again.");
                Security.Identity.Log("Settings: capture saw no foreground change; added nothing");
                return;
            }

            var app = App.Current.Foreground.Current;
            if (app is null || IsClipSyncItself(app))
            {
                ShowStatus(InfoBarSeverity.Error,
                           "Couldn't identify the app in the foreground. Nothing was added.");
                Security.Identity.Log("Settings: capture found no identifiable foreground app; added nothing");
                return;
            }

            if (App.Current.Settings.IsExcluded(app))
            {
                ShowStatus(InfoBarSeverity.Informational, $"{app.DisplayName} is already excluded.");
                return;
            }

            App.Current.Settings.Add(app);
            Security.Identity.Log($"Settings: added exclusion {app.DisplayName} by foreground capture");
            ShowStatus(InfoBarSeverity.Success, $"Excluded {app.DisplayName}.");
            await RefreshList();
        }
        catch (OperationCanceledException)
        {
            if (!_closed) CaptureStatus.IsOpen = false;
        }
        catch (Exception ex)
        {
            Security.Identity.Log($"Settings: capture failed: {ex.GetType().Name}: {ex.Message}");
            if (!_closed)
                ShowStatus(InfoBarSeverity.Error,
                           "Couldn't identify the app in the foreground. Nothing was added.");
        }
        finally
        {
            _capture = null;
            cts.Dispose();
            if (!_closed)
            {
                CaptureStatus.ActionButton = null;
                CaptureItem.IsEnabled = true;
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
