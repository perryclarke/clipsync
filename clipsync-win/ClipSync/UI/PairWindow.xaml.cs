using System;
using Microsoft.UI.Xaml;

namespace ClipSync.UI;

public sealed partial class PairWindow : Window
{
    private static PairWindow? _instance;

    public PairWindow()
    {
        InitializeComponent();
        PinText.Text = new Random().Next(0, 1_000_000).ToString("D6");
    }

    public static void ShowInstance()
    {
        _instance ??= new PairWindow();
        _instance.Activate();
    }

    private void OnEnrollClick(object sender, RoutedEventArgs e)
    {
        // TODO: drive Security.EnrollmentSession with PinInput.Text
    }
}
