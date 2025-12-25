namespace AudioRecorder.Audio;

/// <summary>
/// 오디오 레벨 미터 - Peak 레벨 계산
/// </summary>
public class LevelMeter
{
    private float _peakLevel;
    private readonly object _lock = new();

    /// <summary>
    /// 현재 Peak 레벨 (0.0 ~ 1.0)
    /// </summary>
    public float PeakLevel
    {
        get
        {
            lock (_lock)
            {
                return _peakLevel;
            }
        }
    }

    /// <summary>
    /// dB 단위의 레벨 (-60 ~ 0)
    /// </summary>
    public float LevelDb
    {
        get
        {
            var peak = PeakLevel;
            if (peak <= 0) return -60f;
            var db = 20 * MathF.Log10(peak);
            return MathF.Max(-60f, db);
        }
    }

    /// <summary>
    /// 오디오 샘플로부터 Peak 레벨 계산 (float 샘플)
    /// </summary>
    public void ProcessSamples(float[] samples, int count)
    {
        float maxPeak = 0;
        for (int i = 0; i < count; i++)
        {
            var abs = MathF.Abs(samples[i]);
            if (abs > maxPeak) maxPeak = abs;
        }

        lock (_lock)
        {
            // 부드러운 감쇠 적용
            _peakLevel = MathF.Max(maxPeak, _peakLevel * 0.95f);
        }
    }

    /// <summary>
    /// 오디오 샘플로부터 Peak 레벨 계산 (byte 배열, 32-bit float)
    /// </summary>
    public void ProcessSamples(byte[] buffer, int bytesRecorded)
    {
        int sampleCount = bytesRecorded / 4; // 32-bit float = 4 bytes
        float maxPeak = 0;

        for (int i = 0; i < sampleCount; i++)
        {
            float sample = BitConverter.ToSingle(buffer, i * 4);
            var abs = MathF.Abs(sample);
            if (abs > maxPeak) maxPeak = abs;
        }

        lock (_lock)
        {
            _peakLevel = MathF.Max(maxPeak, _peakLevel * 0.95f);
        }
    }

    /// <summary>
    /// 레벨 리셋
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _peakLevel = 0;
        }
    }
}
