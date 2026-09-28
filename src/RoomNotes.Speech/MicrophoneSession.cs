using System.Threading.Channels;
using NAudio.Wave;
using RoomNotes.Core;

namespace RoomNotes.Speech;

public sealed record MicrophoneDevice(int Id, string Name);

public sealed class MicrophoneSession : IDisposable
{
    private readonly WaveInEvent _capture;
    private readonly Channel<float[]> _chunks = Channel.CreateBounded<float[]>(
        new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly SpeechSegmenter _segmenter = new();
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancel = new();
    private Exception? _failure;
    private bool _stopped;
    private bool _disposed;
    public Task Completion { get; }

    public static IReadOnlyList<MicrophoneDevice> GetDevices() => Enumerable.Range(0, WaveIn.DeviceCount)
        .Select(i => new MicrophoneDevice(i, WaveIn.GetCapabilities(i).ProductName)).ToArray();

    public MicrophoneSession(int device, LocalSpeechEngine engine, Action<string> onTranscript, Action<double> onLevel)
    {
        if (!engine.IsReady) throw new InvalidOperationException("The speech model is not ready.");
        _capture = new WaveInEvent
        {
            DeviceNumber = device, WaveFormat = new WaveFormat(LocalSpeechEngine.SampleRate, 16, 1),
            BufferMilliseconds = 100, NumberOfBuffers = 3
        };
        _capture.DataAvailable += (_, e) =>
        {
            lock (_sync)
            {
                if (_stopped || _disposed) return;
                double peak = 0;
                var samples = new float[e.BytesRecorded / 2];
                for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
                {
                    samples[i / 2] = BitConverter.ToInt16(e.Buffer, i) / 32768f;
                    peak = Math.Max(peak, Math.Abs(samples[i / 2]));
                }
                onLevel(peak);
                foreach (var chunk in _segmenter.Append(samples)) Enqueue(chunk);
                Array.Clear(samples);
            }
        };
        _capture.RecordingStopped += (_, e) =>
        {
            lock (_sync)
            {
                if (_disposed) return;
                _stopped = true;
                if (e.Exception is not null) _failure = e.Exception;
                if (!_cancel.IsCancellationRequested) Enqueue(_segmenter.Flush());
                _chunks.Writer.TryComplete(_failure);
            }
        };
        try { _capture.StartRecording(); }
        catch
        {
            _capture.Dispose();
            _segmenter.Clear();
            _cancel.Dispose();
            throw;
        }
        Completion = ConsumeAsync(engine, onTranscript);
    }

    private void Enqueue(float[] samples)
    {
        if (samples.Length == 0) return;
        if (!_chunks.Writer.TryWrite(samples))
        {
            Array.Clear(samples);
            _failure = new InvalidOperationException("Speech processing could not keep up. Capture stopped; " +
                "the transcript is incomplete. Close other heavy workloads and try a shorter session.");
            _capture.StopRecording();
        }
    }

    private async Task ConsumeAsync(LocalSpeechEngine engine, Action<string> onTranscript)
    {
        try
        {
            await foreach (var samples in _chunks.Reader.ReadAllAsync(_cancel.Token))
            {
                try
                {
                    var text = await engine.TranscribeAsync(samples, _cancel.Token);
                    if (text.Length > 0 && !_cancel.IsCancellationRequested) onTranscript(text);
                }
                finally { Array.Clear(samples); }
            }
        }
        finally
        {
            _capture.StopRecording();
            while (_chunks.Reader.TryRead(out var samples)) Array.Clear(samples);
        }
    }

    public void RequestStop() => _capture.StopRecording();

    public async Task StopAsync()
    {
        _capture.StopRecording();
        await Completion;
    }

    public void Cancel()
    {
        _cancel.Cancel();
        _capture.StopRecording();
        _chunks.Writer.TryComplete();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _capture.Dispose();
        lock (_sync)
        {
            _segmenter.Clear();
            _cancel.Dispose();
        }
    }
}
