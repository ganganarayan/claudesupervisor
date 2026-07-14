using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClaudeSupervisor.Services;
using Microsoft.Win32;

namespace ClaudeSupervisor;

/// <summary>
/// Orchestrates: detect Claude → read the reset time via OCR → keep awake → at the reset,
/// paste attachments, type the prompt, press Enter (optionally to a second window too),
/// then optionally sleep the PC.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp" };

    private ClaudeWindow? _target;         // Claude desktop app
    private ClaudeWindow? _secondTarget;   // e.g. terminal running Claude Code
    private DateTimeOffset _resumeAt;
    private DateTimeOffset? _ocrResetAt;   // reset time parsed by OCR, if the field is unedited
    private bool _settingField;            // guards programmatic edits of the reset-time field
    private readonly ObservableCollection<string> _attachments = new();
    private readonly DispatcherTimer _timer;

    public MainWindow()
    {
        InitializeComponent();

        AttachmentsList.ItemsSource = _attachments;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = v is null ? string.Empty : $"v{v.Major}.{v.Minor}.{v.Build}";

        Loaded += (_, _) => DetectWindow(quiet: true);
        Closed += (_, _) => SystemPower.AllowSleep();
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

    // ----- Second target -----

    private void SecondTargetCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (PickWindowButton is not null)
            PickWindowButton.IsEnabled = SecondTargetCheck.IsChecked == true;
    }

    private void PickWindowButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new PickerWindow { Owner = this };
        if (picker.ShowDialog() == true && picker.Selected is not null)
        {
            _secondTarget = picker.Selected;
            SecondTargetText.Text = $"“{_secondTarget.Title}”  ({_secondTarget.ProcessName}, PID {_secondTarget.Pid})";
            Log($"Second target set: {_secondTarget.Title} (PID {_secondTarget.Pid}).");
        }
    }

    // ----- Attachments -----

    private void AddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Title = "Add attachments" };
        if (dlg.ShowDialog(this) == true)
        {
            foreach (string f in dlg.FileNames)
            {
                if (!_attachments.Contains(f))
                    _attachments.Add(f);
            }
        }
    }

    private void RemoveFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (AttachmentsList.SelectedItem is string s)
            _attachments.Remove(s);
    }

    private void ClearFilesButton_Click(object sender, RoutedEventArgs e) => _attachments.Clear();

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
        SystemPower.KeepAwake(keepDisplayOn: true); // keep Claude rendered & the PC awake while we wait
        ArmButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        ReadButton.IsEnabled = false;

        Log($"Armed. Reset {ResetTimeParser.FormatIst(reset)}, +{buffer}s buffer → send at " +
            $"{ResetTimeParser.FormatIst(_resumeAt)}. PC kept awake until then.");
        UpdateCountdown();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        StopTimer();
        SystemPower.AllowSleep();
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
        if (SendTextBox is not null)
            SendTextBox.IsEnabled = EnterOnlyCheck.IsChecked != true;
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (DateTimeOffset.Now >= _resumeAt)
        {
            StopTimer();
            DoSend(auto: true);
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
        SetStatus($"Armed — sending at {ResetTimeParser.FormatIst(_resumeAt)}  " +
                  $"(in {(int)left.TotalHours:00}:{left.Minutes:00}:{left.Seconds:00}).");
    }

    private void StopTimer()
    {
        _timer.Stop();
        ArmButton.IsEnabled = true;
        CancelButton.IsEnabled = false;
        ReadButton.IsEnabled = true;
    }

    // ----- Send action -----

    private void ResumeNowButton_Click(object sender, RoutedEventArgs e) => DoSend(auto: false);

    private void DoSend(bool auto)
    {
        bool enterOnly = EnterOnlyCheck.IsChecked == true;
        string text = SendTextBox.Text ?? string.Empty;

        try
        {
            // Target 1: the Claude desktop app (re-detect in case its handle changed).
            var primary = DetectWindow(quiet: true) ?? _target;
            if (primary is null)
            {
                SetStatus("Send failed — Claude window not found.");
                Log("ERROR: could not find the Claude window at send time.");
                return;
            }
            SendTo(primary, "Claude", text, enterOnly);

            // Target 2: the picked window (e.g. Claude Code), if enabled.
            if (SecondTargetCheck.IsChecked == true && _secondTarget is not null)
            {
                Thread.Sleep(700);
                SendTo(_secondTarget, _secondTarget.ProcessName, text, enterOnly);
            }

            SetStatus($"{(auto ? "Auto-sent" : "Sent")} at {DateTime.Now:HH:mm:ss}.");
        }
        catch (Exception ex)
        {
            SetStatus("Send failed: " + ex.Message);
            Log("ERROR sending: " + ex.Message);
        }
        finally
        {
            SystemPower.AllowSleep();
        }

        if (SleepWhenDoneCheck.IsChecked == true)
        {
            Log("Sleeping the PC…");
            // Give the UI a moment to flush the log before suspending.
            Dispatcher.BeginInvoke(new Action(() => SystemPower.Sleep()), DispatcherPriority.ApplicationIdle);
        }
    }

    private void SendTo(ClaudeWindow window, string label, string text, bool enterOnly)
    {
        window.ForceForeground();
        Thread.Sleep(500);

        PasteAttachments(window);

        window.Submit(text, enterOnly);

        bool typed = !enterOnly && text.Length > 0;
        string what = typed ? $"appended \"{Preview(text)}\" + Enter" : "pressed Enter only";
        int n = _attachments.Count;
        Log($"Sent to {label} ({window.Title}): {what}" + (n > 0 ? $"; {n} attachment(s)." : "."));
    }

    /// <summary>Puts each attachment on the clipboard and pastes it into the focused composer.</summary>
    private void PasteAttachments(ClaudeWindow window)
    {
        foreach (string path in _attachments)
        {
            try
            {
                if (!File.Exists(path))
                {
                    Log($"Skipped missing attachment: {path}");
                    continue;
                }

                if (IsImage(path))
                {
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.UriSource = new Uri(path);
                    img.EndInit();
                    Clipboard.SetImage(img);
                }
                else
                {
                    var files = new System.Collections.Specialized.StringCollection { path };
                    Clipboard.SetFileDropList(files);
                }

                Thread.Sleep(200);   // let the clipboard settle
                window.Paste();
                Thread.Sleep(1500);  // let Claude ingest/upload before the next paste or Enter
            }
            catch (Exception ex)
            {
                Log($"Attachment failed ({Path.GetFileName(path)}): {ex.Message}");
            }
        }
    }

    private static bool IsImage(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

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
