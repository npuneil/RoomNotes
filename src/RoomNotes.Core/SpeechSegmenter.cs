using System.Runtime.InteropServices;

namespace RoomNotes.Core;

public sealed class SpeechSegmenter
{
    public const int SampleRate = 16000;
    public const int TargetSeconds = 8;
    public const int MaximumSeconds = 25;
    private const int FrameSamples = SampleRate / 50;
    private readonly List<float> _samples = new(SampleRate * MaximumSeconds);
    private double _frameEnergy;
    private int _frameSize, _quietSamples;

    public IReadOnlyList<float[]> Append(ReadOnlySpan<float> input)
    {
        var chunks = new List<float[]>();
        foreach (var sample in input)
        {
            _samples.Add(sample);
            _frameEnergy += (double)sample * sample;
            if (++_frameSize < FrameSamples) continue;
            _quietSamples = _frameEnergy / _frameSize < 0.0001 ? _quietSamples + _frameSize : 0;
            _frameSize = 0;
            _frameEnergy = 0;
            if ((_samples.Count >= SampleRate * TargetSeconds && _quietSamples >= SampleRate * 0.7) ||
                _samples.Count >= SampleRate * MaximumSeconds)
                chunks.Add(Flush());
        }
        return chunks;
    }

    public float[] Flush()
    {
        var chunk = _samples.ToArray();
        Clear();
        return chunk;
    }

    public void Clear()
    {
        CollectionsMarshal.AsSpan(_samples).Clear();
        _samples.Clear();
        _frameEnergy = 0;
        _frameSize = 0;
        _quietSamples = 0;
    }
}
