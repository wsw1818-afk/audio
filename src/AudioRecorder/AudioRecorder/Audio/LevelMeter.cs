using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace AudioRecorder.Audio;

/// <summary>
/// 고성능 오디오 레벨 미터 - Peak 레벨 계산
/// SIMD 가속 및 Lock-free 처리로 최적화
/// </summary>
public class LevelMeter
{
    private volatile float _peakLevel;

    /// <summary>
    /// 현재 Peak 레벨 (0.0 ~ 1.0)
    /// </summary>
    public float PeakLevel => _peakLevel;

    /// <summary>
    /// dB 단위의 레벨 (-60 ~ 0)
    /// </summary>
    public float LevelDb
    {
        get
        {
            float peak = _peakLevel;
            if (peak <= 0) return -60f;
            float db = 20f * MathF.Log10(peak);
            return MathF.Max(-60f, db);
        }
    }

    /// <summary>
    /// 오디오 샘플로부터 Peak 레벨 계산 (byte 배열, 32-bit float)
    /// Span 및 SIMD 최적화 적용, Lock-free
    /// </summary>
    public void ProcessSamples(byte[] buffer, int bytesRecorded)
    {
        if (bytesRecorded < 4) return;

        var floatSpan = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, bytesRecorded));
        float maxPeak = CalculateMaxAbs(floatSpan);

        // Lock-free 업데이트 (약간의 경합은 허용 - 레벨 미터는 정확도보다 성능 우선)
        _peakLevel = MathF.Max(maxPeak, _peakLevel * 0.95f);
    }

    /// <summary>
    /// 최대 절대값 계산 - SIMD 가속 (가능한 경우)
    /// </summary>
    private static float CalculateMaxAbs(ReadOnlySpan<float> samples)
    {
        float max = 0f;
        int i = 0;

        // AVX 사용 가능하고 충분한 데이터가 있으면 벡터 처리 (8 floats)
        if (Avx.IsSupported && samples.Length >= 8)
        {
            var maxVec = Vector256<float>.Zero;
            var signMask = Vector256.Create(0x7FFFFFFF).AsSingle();

            int vectorEnd = samples.Length - (samples.Length % 8);
            for (; i < vectorEnd; i += 8)
            {
                var vec = Vector256.Create(
                    samples[i], samples[i + 1], samples[i + 2], samples[i + 3],
                    samples[i + 4], samples[i + 5], samples[i + 6], samples[i + 7]);
                var absVec = Avx.And(vec, signMask);
                maxVec = Avx.Max(maxVec, absVec);
            }

            // 벡터 결과에서 최대값 추출
            Span<float> temp = stackalloc float[8];
            maxVec.CopyTo(temp);
            for (int j = 0; j < 8; j++)
            {
                if (temp[j] > max) max = temp[j];
            }
        }
        // SSE 사용 가능하면 4개씩 처리
        else if (Sse.IsSupported && samples.Length >= 4)
        {
            var maxVec = Vector128<float>.Zero;
            var signMask = Vector128.Create(0x7FFFFFFF).AsSingle();

            int vectorEnd = samples.Length - (samples.Length % 4);
            for (; i < vectorEnd; i += 4)
            {
                var vec = Vector128.Create(samples[i], samples[i + 1], samples[i + 2], samples[i + 3]);
                var absVec = Sse.And(vec, signMask);
                maxVec = Sse.Max(maxVec, absVec);
            }

            Span<float> temp = stackalloc float[4];
            maxVec.CopyTo(temp);
            for (int j = 0; j < 4; j++)
            {
                if (temp[j] > max) max = temp[j];
            }
        }

        // 나머지 스칼라 처리
        for (; i < samples.Length; i++)
        {
            float abs = MathF.Abs(samples[i]);
            if (abs > max) max = abs;
        }

        return max;
    }

    /// <summary>
    /// 레벨 리셋
    /// </summary>
    public void Reset()
    {
        _peakLevel = 0;
    }
}
