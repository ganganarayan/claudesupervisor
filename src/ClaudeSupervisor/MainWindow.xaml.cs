using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;

namespace ClaudeSupervisor;

/// <summary>
/// Main window: lists Claude-related processes and lets the user refresh,
/// filter, auto-refresh, and terminate them.
/// </summary>
public partial class MainWindow : Window
{
    // Process names (case-insensitive, substring match) that count as "Claude related".
    private static readonly string[] DefaultMatches = { "claude", "node", "anthropic" };

    private readonly ObservableCollection<ProcessRow> _rows = new();
    private readonly DispatcherTimer _autoRefreshTimer;

    public MainWindow()
    {
        InitializeComponent();

        ProcessGrid.ItemsSource = _rows;

        _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _autoRefreshTimer.Tick += (_, _) => Refresh();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? string.Empty : $"v{version.Major}.{version.Minor}.{version.Build}";

        Loaded += (_, _) => Refresh();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => Refresh();

    private void AutoRefreshCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (AutoRefreshCheck.IsChecked == true)
            _autoRefreshTimer.Start();
        else
            _autoRefreshTimer.Stop();
    }

    private void FilterBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Refresh();

    private void TerminateButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = ProcessGrid.SelectedItems.Cast<ProcessRow>().ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "No process selected.";
            return;
        }

        var names = string.Join(", ", selected.Select(r => $"{r.Name} ({r.Pid})"));
        var confirm = MessageBox.Show(
            $"Terminate {selected.Count} process(es)?\n\n{names}",
            "Confirm termination",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
            return;

        int killed = 0, failed = 0;
        foreach (var row in selected)
        {
            try
            {
                using var proc = Process.GetProcessById(row.Pid);
                proc.Kill(entireProcessTree: true);
                killed++;
            }
            catch (Exception ex)
            {
                failed++;
                Debug.WriteLine($"Failed to kill PID {row.Pid}: {ex.Message}");
            }
        }

        StatusText.Text = $"Terminated {killed} process(es)" + (failed > 0 ? $", {failed} failed (try running as administrator)." : ".");
        Refresh();
    }

    /// <summary>
    /// Reloads the process list, applying the current name filter.
    /// </summary>
    private void Refresh()
    {
        var filterText = FilterBox.Text?.Trim();
        var matches = string.IsNullOrWhiteSpace(filterText)
            ? DefaultMatches
            : new[] { filterText };

        // Preserve the currently selected PIDs across the refresh.
        var selectedPids = ProcessGrid.SelectedItems.Cast<ProcessRow>().Select(r => r.Pid).ToHashSet();

        _rows.Clear();

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (!matches.Any(m => proc.ProcessName.Contains(m, StringComparison.OrdinalIgnoreCase)))
                    continue;

                DateTime? started = null;
                try { started = proc.StartTime; } catch { /* access denied on some system procs */ }

                _rows.Add(new ProcessRow
                {
                    Pid = proc.Id,
                    Name = proc.ProcessName,
                    MemoryMb = Math.Round(proc.WorkingSet64 / 1024d / 1024d, 1),
                    Threads = proc.Threads.Count,
                    StartTime = started?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—",
                });
            }
            catch
            {
                // Process may have exited between enumeration and inspection; skip it.
            }
            finally
            {
                proc.Dispose();
            }
        }

        // Restore selection.
        foreach (var row in _rows.Where(r => selectedPids.Contains(r.Pid)))
            ProcessGrid.SelectedItems.Add(row);

        StatusText.Text = $"{_rows.Count} process(es) found · Last updated {DateTime.Now:HH:mm:ss}";
    }
}

/// <summary>
/// A single row displayed in the process grid.
/// </summary>
public sealed class ProcessRow
{
    public int Pid { get; init; }
    public string Name { get; init; } = string.Empty;
    public double MemoryMb { get; init; }
    public int Threads { get; init; }
    public string StartTime { get; init; } = string.Empty;
}
