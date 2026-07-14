using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using ClaudeSupervisor.Services;

namespace ClaudeSupervisor;

/// <summary>
/// Orchestrates: detect Claude window → read the reset time via OCR → schedule →
/// auto-type the resume text once the limit is back.
/// </summary>
public partial class MainWindow : Window
{
    private ClaudeWindow? _target;
    private DateTime _resumeAt;
    private readonly DispatcherTimer _timer;

    public MainWindow()
    {
        InitializeComponent();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = v is null ? string.Empty : $"v{v.Major}.{v.Minor}.{v.Build}";

        Loaded += (_, _) => DetectWindow(quiet: true);
    }

    // ----- Window detection -----

    private void DetectButton_Click(object sender, RoutedEventArgs e) => DetectWindow(quiet: false);

    private ClaudeWindow? DetectWindow(bool quiet)
    {
        _target = ClaudeWindow.Find();
        if (_target is null)
        {
            TargetText.Text = "Not detected — open the Claude desktop app.";
            if (!quiet)
                Log("Claude window not found. Make sure the Claude desktop app is running and visible.");
            return null;
        }

        TargetText.Text = $"“{_target.Title}”  ({_target.ProcessName}, PID {_target.Pid})";
        if (!quiet)
            Log($"Detected Claude window: {_target.Title} (PID {_target.Pid}).");
        return _target;
    }

    // ----- OCR read -----

    private async void ReadButton_Click(object sender, RoutedEventArgs e)
    {
        var target = _target ?? DetectWindow(quiet: false);
        if (target is null)
            return;

        ReadButton.IsEnabled = false;
        SetStatus("Capturing the Claude window and reading text…");
        try
        {
            string text = await Task.Run(async () =>
            {
                using var bmp = target.Capture();
                return await OcrService.RecognizeAsync(bmp);
            });

            string preview = text.Length > 300 ? text[..300] + "…" : text;
            Log("OCR text: " + preview.Replace("\r", " ").Replace("\n", " "));

            if (ResetTimeParser.TryExtractFromOcr(text, out DateTime reset, out string display))
            {
                ResetTimeBox.Text = display;
                SetStatus($"Found reset time: {display}. Click “Arm / Schedule” to set the auto-resume.");
                Log($"Parsed reset time → {display} (next at {reset:yyyy-MM-dd HH:mm}).");
            }
            else
            {
                SetStatus("Couldn’t find a reset time in the message. Type it into the field (e.g. 3pm), then Arm.");
                Log("No reset time matched in the OCR text.");
            }
        }
        catch (Exception ex)
        {
            SetStatus("OCR failed: " + ex.Message);
            Log("ERROR during OCR: " + ex.Message);
        }
        finally
        {
            ReadButton.IsEnabled = true;
        }
    }

    // ----- Arm / schedule -----

    private void ArmButton_Click(object sender, RoutedEventArgs e)
    {
        if (_target is null && DetectWindow(quiet: false) is null)
            return;

        if (!ResetTimeParser.TryParseField(ResetTimeBox.Text, out DateTime reset, out string display))
        {
            SetStatus("Enter a valid reset time first — e.g. 3pm, 3:00 PM, or 15:00.");
            Log("Invalid reset-time field: " + ResetTimeBox.Text);
            return;
        }

        int buffer = int.TryParse(BufferBox.Text, out int b) && b >= 0 ? b : 30;
        _resumeAt = reset.AddSeconds(buffer);

        _timer.Start();
        ArmButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        ReadButton.IsEnabled = false;

        Log($"Armed. Reset {display}, +{buffer}s buffer → resume at {_resumeAt:yyyy-MM-dd HH:mm:ss}. " +
            $"Will send: \"{SendTextBox.Text}\".");
        UpdateCountdown();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        StopTimer();
        SetStatus("Cancelled. Not armed.");
        Log("Schedule cancelled by user.");
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (DateTime.Now >= _resumeAt)
        {
            StopTimer();
            DoResume(auto: true);
        }
        else
        {
            UpdateCountdown();
        }
    }

    private void UpdateCountdown()
    {
        TimeSpan left = _resumeAt - DateTime.Now;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        SetStatus($"Armed — resuming at {_resumeAt:HH:mm:ss}  (in {(int)left.TotalHours:00}:{left.Minutes:00}:{left.Seconds:00}).");
    }

    private void StopTimer()
    {
        _timer.Stop();
        ArmButton.IsEnabled = true;
        CancelButton.IsEnabled = false;
        ReadButton.IsEnabled = true;
    }

    // ----- Resume action -----

    private void ResumeNowButton_Click(object sender, RoutedEventArgs e) => DoResume(auto: false);

    private void DoResume(bool auto)
    {
        // Re-detect in case the window handle changed while we waited.
        var target = DetectWindow(quiet: true) ?? _target;
        if (target is null)
        {
            SetStatus("Resume failed — Claude window not found.");
            Log("ERROR: could not find the Claude window at resume time.");
            return;
        }

        string text = string.IsNullOrEmpty(SendTextBox.Text) ? "resume" : SendTextBox.Text;
        try
        {
            target.SendTextAndEnter(text);
            SetStatus($"{(auto ? "Auto-resumed" : "Sent")} at {DateTime.Now:HH:mm:ss}: typed \"{text}\" + Enter.");
            Log($"{(auto ? "AUTO" : "MANUAL")} resume: sent \"{text}\" + Enter to {target.Title}.");
        }
        catch (Exception ex)
        {
            SetStatus("Resume failed: " + ex.Message);
            Log("ERROR sending resume: " + ex.Message);
        }
    }

    // ----- Helpers -----

    private void SetStatus(string text) => StatusText.Text = text;

    private void Log(string line)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }
}
