using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RoomNotes.Core;
using RoomNotes.Speech;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace RoomNotes.App;

public sealed partial class MainWindow : Window
{
    private readonly FoundryLocalClient _foundry = new();
    private readonly LocalSpeechEngine _speech = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ObservableCollection<FormField> _fields = [];
    private CancellationTokenSource? _operation;
    private MicrophoneSession? _microphone;
    private AnalysisResult? _analysis;
    private bool _busy, _recording, _stopping, _applying, _loaded, _closing;
    private int _captureGeneration;
    private MeetingTemplate Template => (MeetingTemplate?)TemplatePicker.SelectedItem ?? MeetingTemplate.All[0];

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1600, 1060));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 1120;
            presenter.PreferredMinimumHeight = 760;
        }
        TemplatePicker.ItemsSource = MeetingTemplate.All;
        TemplatePicker.SelectedIndex = 0;
        FormItems.ItemsSource = _fields;
        Closed += (_, _) =>
        {
            _closing = true;
            _captureGeneration++;
            _lifetime.Cancel();
            _operation?.Cancel();
            _microphone?.Cancel();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await ConnectAsync();
    }

    private async void ConnectClicked(object sender, RoutedEventArgs e) => await ConnectAsync();

    private async Task ConnectAsync()
    {
        await RunOperationAsync("Connecting local models. The microphone remains off.", async token =>
        {
            Notice.IsOpen = false;
            var errors = new List<string>();
            try
            {
                MicrophonePicker.ItemsSource = MicrophoneSession.GetDevices();
                if (MicrophonePicker.Items.Count > 0) MicrophonePicker.SelectedIndex = 0;
                await _speech.InitializeAsync(Path.Combine(AppContext.BaseDirectory, "models", LocalSpeechEngine.ModelFileName), token);
                SpeechStatus.Text = "Speech ready: Whisper Base English / CPU / ARM64";
            }
            catch (Exception ex) when (IsOperationalError(ex))
            {
                SpeechStatus.Text = "Speech unavailable - paste a transcript instead";
                errors.Add(FriendlyError(ex));
            }
            token.ThrowIfCancellationRequested();
            try
            {
                var runtime = await _foundry.ConnectAsync(token);
                ModelStatus.Text = $"Analysis ready: {runtime.ModelId} / {runtime.ExecutionProvider}";
            }
            catch (Exception ex) when (IsOperationalError(ex))
            {
                ModelStatus.Text = "Analysis unavailable - reconnect after resolving the error";
                errors.Add(FriendlyError(ex));
            }
            if (errors.Count > 0) ShowNotice("Local model connection", string.Join("\n", errors));
            StatusText.Text = errors.Count == 0
                ? "Ready. Local speech + NPU analysis. Session content stays in memory until you export."
                : "Partially connected. See the error above; no cloud fallback will be used.";
        });
    }

    private void TemplateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FormItems is null || TemplatePicker.SelectedItem is null) return;
        ResetDraft();
        TemplateDescription.Text = Template.Description;
    }

    private void ResetDraft()
    {
        _applying = true;
        try
        {
            foreach (var field in _fields) field.PropertyChanged -= FieldChanged;
            _fields.Clear();
            foreach (var definition in Template.Fields)
            {
                var field = new FormField(definition);
                field.PropertyChanged += FieldChanged;
                _fields.Add(field);
            }
            _analysis = null;
            ReviewCheck.IsChecked = false;
            SummaryText.Text = "Capture speech or load a fictional sample, then analyze.";
            ActionsText.Text = "Not analyzed yet";
            RisksText.Text = "Not analyzed yet";
            WarningsText.Text = "";
            AnalysisStats.Text = "No inference has run. No simulated results.";
        }
        finally { _applying = false; }
        UpdateControls();
    }

    private void FieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_applying && e.PropertyName == nameof(FormField.Value)) ReviewCheck.IsChecked = false;
        UpdateControls();
    }

    private void TranscriptChanged(object sender, TextChangedEventArgs e)
    {
        if (ReviewCheck is null) return;
        ReviewCheck.IsChecked = false;
        TranscriptStats.Text = $"{TranscriptBox.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length:N0} words / memory only";
        if (_analysis is not null && !IsCurrentAnalysis())
            AnalysisStats.Text = "Transcript changed. Analyze again before exporting. Your field edits will be preserved.";
        UpdateControls();
    }

    private void InputStateChanged(object sender, RoutedEventArgs e) => UpdateControls();

    private async void SampleClicked(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmReplaceAsync()) return;
        ResetDraft();
        TranscriptBox.Text = Template.SampleTranscript;
        ShowNotice("Fictional sample loaded", "This is sample text, not speech recognition. Press Analyze to run the real local model.",
            InfoBarSeverity.Informational);
        StatusText.Text = "Fictional sample ready for local analysis.";
    }

    private async void NewClicked(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmReplaceAsync()) return;
        _captureGeneration++;
        TranscriptBox.Text = "";
        ConsentCheck.IsChecked = false;
        IncludeTranscriptCheck.IsChecked = false;
        ResetDraft();
        Notice.IsOpen = false;
        CaptureStatus.Text = "Microphone off. Audio is not saved by RoomNotes.";
        StatusText.Text = "New session. Nothing from the previous session was saved by RoomNotes.";
    }

    private async Task<bool> ConfirmReplaceAsync()
    {
        if (string.IsNullOrWhiteSpace(TranscriptBox.Text) && _fields.All(f => f.Value.Length == 0)) return true;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "Discard the current session?",
            Content = "The transcript, analysis, and unsaved form edits will be cleared. Existing exported files are not deleted.",
            PrimaryButtonText = "Discard session", CloseButtonText = "Keep session",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void RecordClicked(object sender, RoutedEventArgs e)
    {
        if (_busy || _recording || ConsentCheck.IsChecked != true ||
            MicrophonePicker.SelectedItem is not MicrophoneDevice device) return;
        Notice.IsOpen = false;
        _recording = true;
        _stopping = false;
        var generation = ++_captureGeneration;
        var successful = false;
        UpdateControls();
        CaptureStatus.Text = "Listening locally. Transcript updates at pauses after about 8 seconds.";
        StatusText.Text = "Microphone active. Press Stop to finish the final speech chunk and auto-fill.";
        try
        {
            _microphone = new MicrophoneSession(device.Id, _speech,
                text => DispatcherQueue.TryEnqueue(() =>
                {
                    if (_closing || generation != _captureGeneration) return;
                    if (TranscriptBox.Text.Length + text.Length + 1 > 60000)
                    {
                        _microphone?.Cancel();
                        ShowNotice("Session limit reached", "Capture stopped at 60,000 characters. The transcript is incomplete; start a shorter session.");
                        return;
                    }
                    TranscriptBox.Text += (TranscriptBox.Text.Length > 0 ? "\n" : "") + text;
                    TranscriptBox.SelectionStart = TranscriptBox.Text.Length;
                }),
                level => DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_closing && generation == _captureGeneration) AudioLevel.Value = level * 100;
                }));
            await _microphone.Completion;
            successful = true;
            StatusText.Text = "Capture finished. Audio buffers released.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Capture canceled. Existing transcript retained; analysis was not run.";
        }
        catch (Exception ex) when (IsOperationalError(ex))
        {
            ShowNotice("Capture stopped", FriendlyError(ex));
            StatusText.Text = "Capture failed. The transcript may be incomplete; no analysis was run.";
        }
        finally
        {
            _microphone?.Dispose();
            _microphone = null;
            _recording = false;
            _stopping = false;
            AudioLevel.Value = 0;
            CaptureStatus.Text = "Microphone off. Audio is not saved by RoomNotes.";
            UpdateControls();
        }
        if (!_closing && successful)
        {
            if (string.IsNullOrWhiteSpace(TranscriptBox.Text))
                ShowNotice("No speech recognized", "Check the selected microphone and Windows microphone privacy permissions.");
            else if (AutoAnalyzeCheck.IsChecked == true)
                await AnalyzeAsync();
        }
    }

    private void StopClicked(object sender, RoutedEventArgs e)
    {
        if (_microphone is null) return;
        _stopping = true;
        _microphone.RequestStop();
        CaptureStatus.Text = "Microphone stopped. Finishing the final speech chunk...";
        UpdateControls();
    }

    private async void ImportClicked(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".wav");
        await RunOperationAsync("Choose a local WAV file. No file is uploaded.", async token =>
        {
            var file = await picker.PickSingleFileAsync();
            if (file is null) { StatusText.Text = "Import canceled."; return; }
            var progress = new Progress<string>(message => StatusText.Text = message);
            var text = await Task.Run(() => _speech.TranscribeWaveAsync(file.Path, progress, token), token);
            if (TranscriptBox.Text.Length + text.Length + 1 > 60000)
                throw new InvalidOperationException("The combined transcript exceeds 60,000 characters. Start a new session before importing.");
            TranscriptBox.Text += (TranscriptBox.Text.Length > 0 ? "\n" : "") + text;
            StatusText.Text = "Local WAV transcribed. Your source file was not modified.";
        });
        if (!_closing && AutoAnalyzeCheck.IsChecked == true && StatusText.Text.StartsWith("Local WAV transcribed"))
            await AnalyzeAsync();
    }

    private async void AnalyzeClicked(object sender, RoutedEventArgs e) => await AnalyzeAsync();

    private async Task AnalyzeAsync()
    {
        await RunOperationAsync("Analyzing the transcript on the local NPU...", async token =>
        {
            var transcript = TranscriptBox.Text;
            var template = Template;
            var progress = new Progress<string>(message => StatusText.Text = message);
            var result = await _foundry.AnalyzeAsync(transcript, template, progress, token);
            token.ThrowIfCancellationRequested();
            _applying = true;
            try
            {
                foreach (var field in _fields) field.Apply(result.Fields[field.Definition.Id]);
                _analysis = result;
                SummaryText.Text = result.Summary;
                ActionsText.Text = result.Actions.Count == 0 ? "No explicit actions identified by the model."
                    : string.Join("\n\n", result.Actions.Select(a => "- " + a));
                RisksText.Text = result.Risks.Count == 0 ? "No incomplete-review phrases flagged. This is not a risk clearance."
                    : string.Join("\n\n", result.Risks.Select(r => "- " + r));
                WarningsText.Text = string.Join("\n", result.Warnings);
                AnalysisStats.Text = $"{result.Elapsed.TotalSeconds:0.0}s / " +
                    (result.Tokens.HasValue ? $"{result.Tokens.Value:N0} reported tokens" : "token usage not reported") +
                    " / QNN NPU\nAI draft + rule-based review flags. Human review required.";
                ReviewCheck.IsChecked = false;
            }
            finally { _applying = false; }
            StatusText.Text = "Analysis complete. Matching excerpts filled the form; your manual edits were preserved.";
            if (result.Warnings.Count > 0)
                ShowNotice("Draft review notes", "See the analysis and field notes. Extra model fields are ignored; unsupported values are not applied.",
                    InfoBarSeverity.Warning);
        });
    }

    private async void ExportClicked(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync("Saving only after your review...", async token =>
        {
            var analysis = _analysis ?? throw new InvalidOperationException("Analyze the transcript first.");
            var json = ReviewedExport.Create(Template, TranscriptBox.Text, analysis, _fields,
                ReviewCheck.IsChecked == true, IncludeTranscriptCheck.IsChecked == true);
            var picker = new FileSavePicker { SuggestedFileName = "RoomNotes-" + Template.Id + "-" + DateTime.Now.ToString("yyyyMMdd-HHmm") };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeChoices.Add("Reviewed JSON form", new List<string> { ".json" });
            var file = await picker.PickSaveFileAsync();
            if (file is null) { StatusText.Text = "Export canceled. Nothing was saved."; return; }
            token.ThrowIfCancellationRequested();
            await FileIO.WriteTextAsync(file, json);
            StatusText.Text = "Reviewed form saved to your chosen location. No automatic submission was made.";
        });
    }

    private async Task RunOperationAsync(string status, Func<CancellationToken, Task> action)
    {
        if (_busy || _recording || _closing) return;
        _busy = true;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = operation;
        Notice.IsOpen = false;
        StatusText.Text = status;
        UpdateControls();
        try { await action(operation.Token); }
        catch (OperationCanceledException)
        {
            StatusText.Text = operation.IsCancellationRequested ? "Operation canceled. No new analysis was applied."
                : "The local operation timed out. Reconnect the local models and retry.";
            if (!operation.IsCancellationRequested) ShowNotice("Local operation timed out", StatusText.Text);
        }
        catch (Exception ex) when (IsOperationalError(ex))
        {
            ShowNotice(ex is AnalysisFormatException ? "Local model output needs retry" : "Local operation failed", FriendlyError(ex));
            StatusText.Text = "Operation failed. Existing transcript and form edits were retained.";
        }
        finally
        {
            _operation = null;
            _busy = false;
            UpdateControls();
        }
    }

    private void CancelClicked(object sender, RoutedEventArgs e)
    {
        _operation?.Cancel();
        _microphone?.Cancel();
    }

    private bool IsCurrentAnalysis() => _analysis?.Fingerprint == AnalysisParser.Fingerprint(Template.Id, TranscriptBox.Text);

    private void UpdateControls()
    {
        if (RecordButton is null || _closing) return;
        var idle = !_busy && !_recording;
        ConnectButton.IsEnabled = idle;
        RecordButton.IsEnabled = idle && _speech.IsReady && ConsentCheck.IsChecked == true && MicrophonePicker.SelectedItem is not null;
        StopButton.IsEnabled = _recording && !_stopping;
        ImportButton.IsEnabled = idle && _speech.IsReady;
        SampleButton.IsEnabled = idle;
        NewButton.IsEnabled = idle;
        TemplatePicker.IsEnabled = idle && string.IsNullOrWhiteSpace(TranscriptBox.Text) && _fields.All(f => f.Value.Length == 0);
        MicrophonePicker.IsEnabled = idle;
        ConsentCheck.IsEnabled = idle;
        TranscriptBox.IsReadOnly = !idle;
        AnalyzeButton.IsEnabled = idle && _foundry.Runtime is not null && !string.IsNullOrWhiteSpace(TranscriptBox.Text);
        ReviewCheck.IsEnabled = idle && IsCurrentAnalysis();
        FormItems.IsEnabled = idle;
        ExportButton.IsEnabled = idle && IsCurrentAnalysis() && ReviewCheck.IsChecked == true &&
            _fields.All(f => !f.Definition.Required || !string.IsNullOrWhiteSpace(f.Value));
        BusyRing.IsActive = _busy || _stopping;
        CancelButton.Visibility = _busy || _stopping ? Visibility.Visible : Visibility.Collapsed;
        FormStats.Text = $"{_fields.Count(f => !string.IsNullOrWhiteSpace(f.Value))} / {_fields.Count} fields filled - review required";
    }

    private void ShowNotice(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Error)
    {
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }

    private static bool IsOperationalError(Exception exception) => exception is IOException or InvalidOperationException or
        ArgumentException or HttpRequestException or System.Text.Json.JsonException or AnalysisFormatException or
        Win32Exception or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or
        DllNotFoundException or BadImageFormatException or TypeInitializationException or NAudio.MmException or
        System.Threading.Channels.ChannelClosedException or NotSupportedException;

    private static string FriendlyError(Exception exception) => exception switch
    {
        Win32Exception => "Foundry Local could not be started. Install the official Microsoft Foundry Local CLI and reopen RoomNotes.",
        NAudio.MmException => "The microphone could not be opened. Check the selected device and Windows Settings > Privacy & security > Microphone > Let desktop apps access your microphone.",
        DllNotFoundException or BadImageFormatException or TypeInitializationException =>
            "A native speech dependency could not load. Use the complete ARM64 publish folder and the ARM64 Visual C++ runtime.",
        System.Runtime.InteropServices.COMException => $"Windows could not complete the operation (0x{exception.HResult:X8}). Check microphone or file access permissions.",
        _ => exception.Message
    };
}
