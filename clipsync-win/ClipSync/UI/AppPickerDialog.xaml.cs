using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ClipSync.Settings;
using WinRT.Interop;

namespace ClipSync.UI;

public sealed partial class AppPickerDialog : ContentDialog
{
    private readonly Window _owner;
    private IReadOnlyList<InstalledApp> _all = Array.Empty<InstalledApp>();
    private AppIdentity? _browsed;
    private bool _enumerationFailed;

    /// `owner` supplies the HWND that FileOpenPicker must parent itself to.
    public AppPickerDialog(Window owner)
    {
        InitializeComponent();
        _owner = owner;
        SecondaryButtonClick += OnBrowse;
        Loaded += OnLoaded;
    }

    /// Shows the picker; returns the chosen app, or null if cancelled.
    public static async Task<AppIdentity?> PickAsync(XamlRoot root, Window owner)
    {
        var dialog = new AppPickerDialog(owner) { XamlRoot = root };
        var result = await dialog.ShowAsync();

        // Browse dismisses via Hide(), which yields None rather than
        // Primary, so a browsed pick has to be checked either way.
        if (dialog._browsed is { } browsed) return browsed;
        if (result == ContentDialogResult.Primary
            && dialog.AppList.SelectedItem is ListViewItem { Tag: AppIdentity picked })
            return picked;
        return null;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Enumeration plus icon rasterising is too slow for the UI thread.
        // (Lambda, not a method group: Enumerate has an optional parameter.)
        var apps = await Task.Run(() => InstalledApps.Enumerate());

        // An empty result means enumeration failed; a list that is empty
        // only after filtering just means everything is already excluded.
        _enumerationFailed = apps.Count == 0;

        var alreadyExcluded = App.Current.Settings.Excluded.ToHashSet();
        _all = apps.Where(a => !alreadyExcluded.Contains(a.Identity)).ToList();

        Busy.IsActive = false;
        Busy.Visibility = Visibility.Collapsed;
        if (_enumerationFailed) ErrorText.Visibility = Visibility.Visible;

        Populate(_all);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        Populate(string.IsNullOrEmpty(q)
            ? _all
            : _all.Where(a => a.Identity.DisplayName.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                  .ToList());
    }

    private void Populate(IReadOnlyList<InstalledApp> apps)
    {
        AppList.Items.Clear();
        foreach (var app in apps)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new Image
            {
                // Populate runs on the UI thread, which ToImageSource requires.
                Width = 24, Height = 24, Source = InstalledApps.ToImageSource(app.IconPng),
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text = app.Identity.DisplayName,
                VerticalAlignment = VerticalAlignment.Center,
            });
            AppList.Items.Add(new ListViewItem { Content = row, Tag = app.Identity });
        }
    }

    /// SecondaryButton = Browse. Keep the dialog open while the file
    /// picker runs, then close it as if Add had been pressed.
    private async void OnBrowse(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        var deferral = args.GetDeferral();
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".exe");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_owner));

            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                _browsed = InstalledApps.FromExecutable(file.Path);
                Hide();
            }
        }
        catch (Exception ex)
        {
            Security.Identity.Log($"AppPicker: browse failed: {ex.GetType().Name}");
        }
        finally { deferral.Complete(); }
    }
}
