using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ClipSync.Settings;
using WinRT.Interop;

namespace ClipSync.UI;

/// One picker row, ready to display.
///
/// The `ImageSource` is built once, when the list is first loaded, and then
/// reused for the lifetime of the dialog: filtering re-binds the same row
/// objects, so typing in the search box costs no PNG decodes. It is created
/// on the UI thread, as `ToImageSource` requires.
public sealed class AppRow
{
    public AppRow(InstalledApp app)
    {
        Identity = app.Identity;
        Name = app.Identity.DisplayName;
        // Only merged rows earn the second line, where it says which single
        // executable all those names share. A one-name row would just be
        // repeating itself.
        Detail = app.Names.Count > 1 ? app.Identity.Key : "";
        Icon = InstalledApps.ToImageSource(app.IconPng);
        _searchable = app.Names;
    }

    public AppIdentity Identity { get; }
    public string Name { get; }
    public string Detail { get; }
    public ImageSource? Icon { get; }

    public Visibility DetailVisibility =>
        Detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    private readonly IReadOnlyList<string> _searchable;

    /// Matches on any of the app's names, not just the displayed one: on a
    /// merged row eleven of the twelve names are not shown, and typing
    /// "Command Prompt" must still find the row that excludes it.
    public bool Matches(string query)
    {
        foreach (var name in _searchable)
            if (name.Contains(query, StringComparison.CurrentCultureIgnoreCase)) return true;
        return Identity.Key.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed partial class AppPickerDialog : ContentDialog
{
    private readonly Window _owner;
    private IReadOnlyList<AppRow> _all = Array.Empty<AppRow>();
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
        if (result == ContentDialogResult.Primary && dialog.AppList.SelectedItem is AppRow picked)
            return picked.Identity;
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
        _all = apps.Where(a => !alreadyExcluded.Contains(a.Identity))
                   .Select(a => new AppRow(a))
                   .ToList();

        Busy.IsActive = false;
        Busy.Visibility = Visibility.Collapsed;
        if (_enumerationFailed) ErrorText.Visibility = Visibility.Visible;

        AppList.ItemsSource = _all;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        AppList.ItemsSource = string.IsNullOrEmpty(q)
            ? _all
            : _all.Where(a => a.Matches(q)).ToList();
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
