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
    private DateTimeOffset _resumeAt;
    private DateTimeOffset? _ocrResetAt; // reset time parsed by OCR, if the field is unedited
    private bool _settingField;          // guards programmatic edits of the reset-time field
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

            if (ResetTimeParser.TryExtractFromOcr(text, out DateTimeOffset reset))
            {
                string full = ResetTimeParser.FormatIst(reset);
                _settingField = true;
                ResetTimeBox.Text = ResetTimeParser.FormatClock(reset);
                _settingField = false;
                _ocrResetAt = reset;

                SetStatus($"Found reset time: {full}. Click “Arm / Schedule” to set the auto-resume.");
                Log($"Parsed reset time → {full}.");
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

        // Prefer the OCR-parsed time if the field hasn't been edited; otherwise parse the field.
        DateTimeOffset reset;
        if (_ocrResetAt.HasValue)
        {
            reset = _ocrResetAt.Value;
        }
        else if (!ResetTimeParser.TryParseField(ResetTimeBox.Text, out reset))
        {
            SetStatus("Enter a valid reset time first — e.g. 3pm, 3:00 PM, or 15:00 (IST).");
            Log("Invalid reset-time field: " + ResetTimeBox.Text);
            return;
        }

        int buffer = int.TryParse(BufferBox.Text, out int b) && b >= 0 ? b : 30;
        _resumeAt = reset.AddSeconds(buffer);

        _timer.Start();
        ArmButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        ReadButton.IsEnabled = false;

        Log($"Armed. Reset {ResetTimeParser.FormatIst(reset)}, +{buffer}s buffer → resume at " +
            $"{ResetTimeParser.FormatIst(_resumeAt)}. Will send: \"{SendTextBox.Text}\".");
        UpdateCountdown();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        StopTimer();
        SetStatus("Cancelled. Not armed.");
        Log("Schedule cancelled by user.");
    }

    private void ResetTimeBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // A manual edit invalidates the OCR-parsed time; Arm will re-parse the field.
        if (!_settingField)
            _ocrResetAt = null;
    }

    private void EnterOnlyCheck_Changed(object sender, RoutedEventArgs e)
    {
        // When we're only pressing Enter, the send text is irrelevant — grey it out.
        if (SendTextBox is not null)
            SendTextBox.IsEnabled = EnterOnlyCheck.IsChecked != true;
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (DateTimeOffset.Now >= _resumeAt)
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
        TimeSpan left = _resumeAt - DateTimeOffset.Now;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        SetStatus($"Armed — resuming at {ResetTimeParser.FormatIst(_resumeAt)}  " +
                  $"(in {(int)left.TotalHours:00}:{left.Minutes:00}:{left.Seconds:00}).");
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

        bool enterOnly = EnterOnlyCheck.IsChecked == true;
        string text = SendTextBox.Text ?? string.Empty;
        bool willType = !enterOnly && text.Length > 0;

        string what = willType
            ? $"appended \"{Preview(text)}\" + Enter"
            : "pressed Enter only";
        try
        {
            target.Resume(text, enterOnly);
            SetStatus($"{(auto ? "Auto-sent" : "Sent")} at {DateTime.Now:HH:mm:ss}: {what}.");
            Log($"{(auto ? "AUTO" : "MANUAL")} send to {target.Title}: {what}.");
        }
        catch (Exception ex)
        {
            SetStatus("Send failed: " + ex.Message);
            Log("ERROR sending: " + ex.Message);
        }
    }

    private static string Preview(string text)
    {
        string oneLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return oneLine.Length > 60 ? oneLine[..60] + "…" : oneLine;
    }

    // ----- Helpers -----

    private void SetStatus(string text) => StatusText.Text = text;

    private void Log(string line)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }
}
