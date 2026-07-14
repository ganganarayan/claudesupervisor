using System.Windows;
using System.Windows.Input;
using ClaudeSupervisor.Services;

namespace ClaudeSupervisor;

/// <summary>Modal dialog that lets the user pick a window as the second send target.</summary>
public partial class PickerWindow : Window
{
    public ClaudeWindow? Selected { get; private set; }

    public PickerWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    private void Reload() => WindowList.ItemsSource = ClaudeWindow.EnumerateAll();

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    private void Select_Click(object sender, RoutedEventArgs e) => Confirm();

    private void WindowList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Confirm();

    private void Confirm()
    {
        if (WindowList.SelectedItem is ClaudeWindow w)
        {
            Selected = w;
            DialogResult = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
