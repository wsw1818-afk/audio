using NAudio.Wave;

namespace AudioRecorder.Audio;

/// <summary>
/// 오디오 믹서 - 마이크와 시스템 오디오를 실시간 믹싱
/// </summary>
public class AudioMixer
{
    private readonly object _lock = new();
    private readonly Queue<float[]> _micBuffer = new();
    private readonly Queue<float[]> _systemBuffer = new();

    public WaveFormat OutputFormat { get; }
    public float MicVolume { get; set; } = 1.0f;
    public float SystemVolume { get; set; } = 1.0f;

    public AudioMixer(int sampleRate = 48000, int channels = 2)
    {
        OutputFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    }

    /// <summary>
    /// 마이크 샘플 추가
    /// </summary>
    public void AddMicSamples(float[] samples)
    {
        lock (_lock)
        {
            var copy = new float[samples.Length];
            Array.Copy(samples, copy, samples.Length);
            _micBuffer.Enqueue(copy);
        }
    }

    /// <summary>
    /// 시스템 오디오 샘플 추가
    /// </summary>
    public void AddSystemSamples(float[] samples)
    {
        lock (_lock)
        {
            var copy = new float[samples.Length];
            Array.Copy(samples, copy, samples.Length);
            _systemBuffer.Enqueue(copy);
        }
    }

    /// <summary>
    /// 믹싱된 샘플 읽기
    /// </summary>
    public int ReadMixed(float[] buffer)
    {
        lock (_lock)
        {
            int samplesWritten = 0;
            int bufferIndex = 0;

            // 두 버퍼에서 동시에 데이터 꺼내서 믹싱
            while (bufferIndex < buffer.Length)
            {
                float micSample = 0;
                float sysSample = 0;

                // 마이크 샘플
                if (_micBuffer.Count > 0)
                {
                    var micData = _micBuffer.Peek();
                    if (samplesWritten < micData.Length)
                    {
                        micSample = micData[samplesWritten] * MicVolume;
                    }
                    else
                    {
                        _micBuffer.Dequeue();
                        if (_micBuffer.Count > 0)
                        {
                            samplesWritten = 0;
                            micSample = _micBuffer.Peek()[0] * MicVolume;
                        }
                    }
                }

                // 시스템 샘플
                if (_systemBuffer.Count > 0)
                {
                    var sysData = _systemBuffer.Peek();
                    if (samplesWritten < sysData.Length)
                    {
                        sysSample = sysData[samplesWritten] * SystemVolume;
                    }
                    else
                    {
                        _systemBuffer.Dequeue();
                        if (_systemBuffer.Count > 0)
                        {
                            samplesWritten = 0;
                            sysSample = _systemBuffer.Peek()[0] * SystemVolume;
                        }
                    }
                }

                // 믹싱 (클리핑 방지)
                float mixed = micSample + sysSample;
                buffer[bufferIndex] = Math.Clamp(mixed, -1.0f, 1.0f);

                bufferIndex++;
                samplesWritten++;

                // 더 이상 데이터가 없으면 종료
                if (_micBuffer.Count == 0 && _systemBuffer.Count == 0)
                    break;
            }

            return bufferIndex;
        }
    }

    /// <summary>
    /// 버퍼 클리어
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _micBuffer.Clear();
            _systemBuffer.Clear();
        }
    }
}
