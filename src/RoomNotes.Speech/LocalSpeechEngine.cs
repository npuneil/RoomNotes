using System.Security.Cryptography;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RoomNotes.Core;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace RoomNotes.Speech;

public sealed class LocalSpeechEngine : IDisposable
{
    public const string ModelFileName = "ggml-base.en.bin";
    public const string ModelHash = "A03779C86DF3323075F5E796CB2CE5029F00EC8869EEE3FDFB897AFE36C6D002";
    public const int SampleRate = SpeechSegmenter.SampleRate;
    private readonly SemaphoreSlim _gate = new(1);
    private WhisperFactory? _factory;
    public bool IsReady => _factory is not null;

    public async Task InitializeAsync(string modelPath, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_factory is not null) return;
            if (!File.Exists(modelPath))
                throw new FileNotFoundException("Whisper Base English is not installed. Run setup-model.ps1 first.", modelPath);
            await using (var stream = File.OpenRead(modelPath))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
                if (hash != ModelHash)
                    throw new InvalidDataException("The speech model checksum is invalid. Run setup-model.ps1 to repair it.");
            }
            RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu];
            _factory = await Task.Run(() => WhisperFactory.FromPath(modelPath,
                new WhisperFactoryOptions { UseGpu = false, UseFlashAttention = true }), cancellationToken);
        }
        catch (WhisperModelLoadException ex)
        {
            throw new InvalidOperationException("Whisper could not load the local speech model. Check available memory and reconnect.", ex);
        }
        finally { _gate.Release(); }
    }

    public async Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken)
    {
        if (samples.Length == 0) return "";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var factory = _factory ?? throw new InvalidOperationException("Connect the speech model before transcribing.");
            var energy = samples.Sum(x => (double)x * x) / samples.Length;
            if (energy < 0.000004) return "";
            using var processor = factory.CreateBuilder().WithLanguage("en").WithNoContext()
                .WithThreads(Math.Min(6, Environment.ProcessorCount)).WithTemperature(0).WithTemperatureInc(0)
                .WithGreedySamplingStrategy(strategy => strategy.WithBestOf(1)).Build();
            var parts = new List<string>();
            await foreach (var segment in processor.ProcessAsync(samples, cancellationToken))
            {
                if (segment.NoSpeechProbability < 0.7 && !string.IsNullOrWhiteSpace(segment.Text))
                    parts.Add(segment.Text.Trim());
            }
            return string.Join(" ", parts);
        }
        catch (WhisperProcessingException ex)
        {
            throw new InvalidOperationException("The local speech decoder failed. Any partial transcript needs review; reconnect before retrying.", ex);
        }
        finally { _gate.Release(); }
    }

    public async Task<string> TranscribeWaveAsync(string path, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        using var reader = new WaveFileReader(path);
        if (reader.TotalTime > TimeSpan.FromMinutes(30))
            throw new InvalidDataException("Import WAV files of 30 minutes or less.");
        if (reader.WaveFormat.Encoding is not WaveFormatEncoding.Pcm and not WaveFormatEncoding.IeeeFloat)
            throw new InvalidDataException("Use an uncompressed PCM or float WAV file.");
        ISampleProvider provider = reader.ToSampleProvider();
        if (provider.WaveFormat.Channels == 2)
            provider = new StereoToMonoSampleProvider(provider) { LeftVolume = 0.5f, RightVolume = 0.5f };
        if (provider.WaveFormat.Channels != 1)
            throw new InvalidDataException("Use a mono or stereo WAV file.");
        if (provider.WaveFormat.SampleRate != SampleRate)
            provider = new WdlResamplingSampleProvider(provider, SampleRate);
        var buffer = new float[SampleRate / 10];
        var segmenter = new SpeechSegmenter();
        var parts = new List<string>();
        var seconds = 0.0;
        async Task ProcessChunkAsync(float[] samples)
        {
            try
            {
                var text = await TranscribeAsync(samples, cancellationToken);
                if (text.Length > 0) parts.Add(text);
                seconds += samples.Length / (double)SampleRate;
                progress?.Report($"Transcribed {seconds:0} / {reader.TotalTime.TotalSeconds:0} seconds on CPU");
            }
            finally { Array.Clear(samples); }
        }
        try
        {
            int read;
            while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var samples in segmenter.Append(buffer.AsSpan(0, read)))
                    await ProcessChunkAsync(samples);
            }
            await ProcessChunkAsync(segmenter.Flush());
        }
        finally { Array.Clear(buffer); segmenter.Clear(); }
        var result = string.Join("\n", parts);
        if (string.IsNullOrWhiteSpace(result))
            throw new InvalidDataException("No speech was recognized. Check the WAV file and recording level.");
        return result;
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _gate.Dispose();
    }
}
