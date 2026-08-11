using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ClipSync.Settings;

namespace ClipSync.UI;

public sealed partial class SettingsWindow : Window
{
    private static SettingsWindow? _instance;

    public SettingsWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _instance = null;
        RefreshList();
    }

    /// One settings window at a time; re-activate the existing one.
    public static void ShowSingleton()
    {
        _instance ??= new SettingsWindow();
        _instance.Activate();
    }

    public void RefreshList()
    {
        var excluded = App.Current.Settings.Excluded;
        ExcludedList.Items.Clear();
        EmptyText.Visibility = excluded.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var app in excluded)
            ExcludedList.Items.Add(BuildRow(app));
    }

    private UIElement BuildRow(AppIdentity app)
    {
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Image { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center };
        // Both calls are UI-thread-safe here; ToImageSource requires it.
        if (app.Path is { } p) icon.Source = InstalledApps.ToImageSource(InstalledApps.IconBytesForExecutable(p));
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = app.DisplayName, FontSize = 14 });
        text.Children.Add(new TextBlock
        {
            Text = app.Path ?? app.Key,
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["SystemControlForegroundBaseMediumBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var remove = new Button { Content = "Remove", VerticalAlignment = VerticalAlignment.Center };
        remove.Click += (_, _) =>
        {
            App.Current.Settings.Remove(app);
            Security.Identity.Log($"Settings: removed exclusion {app.DisplayName}");
            RefreshList();
        };
        Grid.SetColumn(remove, 2);
        row.Children.Add(remove);

        return row;
    }

    private void OnAddApp(object sender, RoutedEventArgs e)
    {
        // Replaced in Task 8 by the app picker.
    }
}
