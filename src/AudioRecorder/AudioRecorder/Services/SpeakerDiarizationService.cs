using System.Diagnostics;
using System.Globalization;
using System.IO;
using AudioRecorder.Models;

namespace AudioRecorder.Services;

/// <summary>
/// 화자 분리 서비스
/// FFmpeg silencedetect + 에너지 기반 음성 특징 클러스터링으로 화자 식별
/// </summary>
public class SpeakerDiarizationService
{
    private readonly AudioConversionService _audioConversion;
    private readonly string _logPath;

    /// <summary>
    /// 진행 상태 변경 이벤트
    /// </summary>
    public event EventHandler<SttProgressEventArgs>? ProgressChanged;

    public SpeakerDiarizationService(AudioConversionService audioConversion)
    {
        _audioConversion = audioConversion;

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AudioRecorder", "logs");
        if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
        _logPath = Path.Combine(logDir, "diarization.log");
    }

    private void Log(string message)
    {
        var logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        Debug.WriteLine(logMessage);
        try { File.AppendAllText(_logPath, logMessage + Environment.NewLine); }
        catch { }
    }

    /// <summary>
    /// 화자 분리 수행: 각 세그먼트에 화자 레이블 할당
    /// </summary>
    public async Task<bool> DiarizeAsync(
        string audioPath,
        TranscriptionResult result,
        int maxSpeakers = 4,
        CancellationToken cancellationToken = default)
    {
        if (result.Segments.Count == 0) return false;

        Log($"화자 분리 시작: {audioPath}, {result.Segments.Count}개 구간, 최대 {maxSpeakers}명");

        ProgressChanged?.Invoke(this, new SttProgressEventArgs
        {
            Status = "화자 분리 중...",
            Progress = 0,
            Phase = SttPhase.Diarizing
        });

        try
        {
            // 1단계: FFmpeg로 각 구간의 오디오 에너지 특징 추출
            var features = await ExtractAudioFeaturesAsync(audioPath, result.Segments, cancellationToken);
            if (features == null || features.Count == 0)
            {
                Log("오디오 특징 추출 실패 → 단일 화자로 처리");
                AssignSingleSpeaker(result);
                return true;
            }

            ProgressChanged?.Invoke(this, new SttProgressEventArgs
            {
                Status = "화자 클러스터링 중...",
                Progress = 60,
                Phase = SttPhase.Diarizing
            });

            // 2단계: 특징 벡터 기반 클러스터링
            var speakerLabels = ClusterSpeakers(features, maxSpeakers);

            // 3단계: 세그먼트에 화자 레이블 할당
            var speakerSet = new HashSet<string>();
            for (int i = 0; i < result.Segments.Count && i < speakerLabels.Count; i++)
            {
                var label = $"화자 {speakerLabels[i] + 1}";
                result.Segments[i].SpeakerLabel = label;
                speakerSet.Add(label);
            }

            result.DetectedSpeakers = speakerSet.OrderBy(s => s).ToList();

            ProgressChanged?.Invoke(this, new SttProgressEventArgs
            {
                Status = $"화자 분리 완료 ({result.DetectedSpeakers.Count}명 감지)",
                Progress = 100,
                Phase = SttPhase.Diarizing
            });

            Log($"화자 분리 완료: {result.DetectedSpeakers.Count}명 감지");
            return true;
        }
        catch (Exception ex)
        {
            Log($"화자 분리 실패: {ex.Message}");
            AssignSingleSpeaker(result);
            return false;
        }
    }

    /// <summary>
    /// 단일 화자로 할당 (폴백)
    /// </summary>
    private static void AssignSingleSpeaker(TranscriptionResult result)
    {
        foreach (var seg in result.Segments)
            seg.SpeakerLabel = "화자 1";
        result.DetectedSpeakers = new List<string> { "화자 1" };
    }

    /// <summary>
    /// FFmpeg로 각 구간의 오디오 특징 추출
    /// RMS 에너지, 제로크로싱률, 스펙트럼 중심 등을 근사
    /// </summary>
    private async Task<List<AudioFeature>?> ExtractAudioFeaturesAsync(
        string audioPath,
        List<TranscriptionSegment> segments,
        CancellationToken cancellationToken)
    {
        var ffmpeg = _audioConversion.FindFFmpeg();
        if (ffmpeg == null)
        {
            Log("FFmpeg를 찾을 수 없습니다");
            return null;
        }

        var features = new List<AudioFeature>();

        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            if (seg.Duration.TotalMilliseconds < 100) // 100ms 미만 무시
            {
                features.Add(new AudioFeature());
                continue;
            }

            if (i % 10 == 0)
            {
                var progress = (int)(i * 50.0 / segments.Count);
                ProgressChanged?.Invoke(this, new SttProgressEventArgs
                {
                    Status = $"오디오 특징 추출 중... ({i + 1}/{segments.Count})",
                    Progress = progress,
                    Phase = SttPhase.Diarizing
                });
            }

            var feature = await ExtractSegmentFeatureAsync(
                ffmpeg, audioPath, seg.StartTime, seg.EndTime, cancellationToken);
            features.Add(feature ?? new AudioFeature());
        }

        return features;
    }

    /// <summary>
    /// 단일 구간의 오디오 특징 추출
    /// FFmpeg astats 필터를 사용하여 RMS, Peak, Crest Factor 등 추출
    /// </summary>
    private async Task<AudioFeature?> ExtractSegmentFeatureAsync(
        string ffmpeg, string audioPath, TimeSpan start, TimeSpan end, CancellationToken cancellationToken)
    {
        var duration = end - start;

        var args = $"-ss {start:hh\\:mm\\:ss\\.fff} -t {duration:hh\\:mm\\:ss\\.fff} " +
                   $"-i \"{audioPath}\" " +
                   $"-af \"astats=metadata=1:reset=0,ametadata=print:key=lavfi.astats.Overall.RMS_level:key=lavfi.astats.Overall.Peak_level:key=lavfi.astats.Overall.Flat_factor:key=lavfi.astats.Overall.Zero_crossings_rate\" " +
                   $"-f null -";

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        using var process = new Process { StartInfo = startInfo };
        var errorLines = new List<string>();

        process.ErrorDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                errorLines.Add(e.Data);
        };

        process.Start();
        process.BeginErrorReadLine();

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            return null;
        }

        // 출력에서 특징값 파싱
        var feature = new AudioFeature();
        foreach (var line in errorLines)
        {
            if (line.Contains("RMS_level="))
            {
                var val = ExtractValue(line, "RMS_level=");
                if (val.HasValue) feature.RmsLevel = val.Value;
            }
            else if (line.Contains("Peak_level="))
            {
                var val = ExtractValue(line, "Peak_level=");
                if (val.HasValue) feature.PeakLevel = val.Value;
            }
            else if (line.Contains("Zero_crossings_rate="))
            {
                var val = ExtractValue(line, "Zero_crossings_rate=");
                if (val.HasValue) feature.ZeroCrossingRate = val.Value;
            }
            else if (line.Contains("Flat_factor="))
            {
                var val = ExtractValue(line, "Flat_factor=");
                if (val.HasValue) feature.FlatFactor = val.Value;
            }
        }

        return feature;
    }

    /// <summary>
    /// 문자열에서 숫자 값 추출
    /// </summary>
    private static double? ExtractValue(string line, string key)
    {
        var idx = line.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return null;

        var valueStr = line[(idx + key.Length)..].Trim();
        // 값 끝 찾기 (공백 또는 줄 끝)
        var endIdx = valueStr.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
        if (endIdx > 0) valueStr = valueStr[..endIdx];

        if (double.TryParse(valueStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
            return result;

        return null;
    }

    /// <summary>
    /// K-means 기반 화자 클러스터링
    /// </summary>
    private List<int> ClusterSpeakers(List<AudioFeature> features, int maxSpeakers)
    {
        if (features.Count <= 1)
            return features.Select(_ => 0).ToList();

        // 특징 벡터 정규화
        var normalizedFeatures = NormalizeFeatures(features);

        // 최적 클러스터 수 결정 (실루엣 점수 기반, 2~maxSpeakers)
        int bestK = 1;
        double bestScore = double.MinValue;

        for (int k = 2; k <= Math.Min(maxSpeakers, features.Count); k++)
        {
            var labels = KMeans(normalizedFeatures, k, maxIterations: 50);
            var score = CalculateSilhouetteScore(normalizedFeatures, labels, k);

            Log($"K={k}: 실루엣 점수={score:F3}");

            if (score > bestScore)
            {
                bestScore = score;
                bestK = k;
            }
        }

        // 실루엣 점수가 너무 낮으면 단일 화자
        if (bestScore < 0.1)
        {
            Log($"실루엣 점수 낮음({bestScore:F3}) → 단일 화자");
            return features.Select(_ => 0).ToList();
        }

        Log($"최적 클러스터 수: K={bestK} (실루엣={bestScore:F3})");
        return KMeans(normalizedFeatures, bestK, maxIterations: 100);
    }

    /// <summary>
    /// 특징 벡터 정규화 (z-score)
    /// </summary>
    private List<double[]> NormalizeFeatures(List<AudioFeature> features)
    {
        var vectors = features.Select(f => f.ToVector()).ToList();
        int dim = vectors[0].Length;

        // 평균 계산
        var means = new double[dim];
        var stds = new double[dim];

        for (int d = 0; d < dim; d++)
        {
            var values = vectors.Select(v => v[d]).ToList();
            means[d] = values.Average();
            var variance = values.Sum(v => (v - means[d]) * (v - means[d])) / values.Count;
            stds[d] = Math.Sqrt(variance);
            if (stds[d] < 1e-10) stds[d] = 1.0; // 0으로 나누기 방지
        }

        // 정규화
        return vectors.Select(v =>
        {
            var normalized = new double[dim];
            for (int d = 0; d < dim; d++)
                normalized[d] = (v[d] - means[d]) / stds[d];
            return normalized;
        }).ToList();
    }

    /// <summary>
    /// K-means 클러스터링 알고리즘
    /// </summary>
    private List<int> KMeans(List<double[]> data, int k, int maxIterations)
    {
        var random = new Random(42); // 재현성을 위한 고정 시드
        int n = data.Count;
        int dim = data[0].Length;

        // 초기 중심 선택 (k-means++ 초기화)
        var centroids = InitializeCentroidsKMeansPlusPlus(data, k, random);
        var labels = new int[n];

        for (int iter = 0; iter < maxIterations; iter++)
        {
            // 할당 단계: 각 데이터 포인트를 가장 가까운 중심에 할당
            bool changed = false;
            for (int i = 0; i < n; i++)
            {
                int nearest = 0;
                double minDist = double.MaxValue;

                for (int c = 0; c < k; c++)
                {
                    double dist = EuclideanDistance(data[i], centroids[c]);
                    if (dist < minDist)
                    {
                        minDist = dist;
                        nearest = c;
                    }
                }

                if (labels[i] != nearest)
                {
                    labels[i] = nearest;
                    changed = true;
                }
            }

            if (!changed) break;

            // 업데이트 단계: 중심 재계산
            for (int c = 0; c < k; c++)
            {
                var members = Enumerable.Range(0, n).Where(i => labels[i] == c).ToList();
                if (members.Count == 0) continue;

                for (int d = 0; d < dim; d++)
                {
                    centroids[c][d] = members.Average(i => data[i][d]);
                }
            }
        }

        return labels.ToList();
    }

    /// <summary>
    /// K-means++ 초기화
    /// </summary>
    private List<double[]> InitializeCentroidsKMeansPlusPlus(List<double[]> data, int k, Random random)
    {
        int dim = data[0].Length;
        var centroids = new List<double[]>();

        // 첫 번째 중심: 무작위
        centroids.Add((double[])data[random.Next(data.Count)].Clone());

        // 나머지 중심: 거리 비례 확률로 선택
        for (int c = 1; c < k; c++)
        {
            var distances = data.Select(point =>
            {
                return centroids.Min(cent => EuclideanDistance(point, cent));
            }).ToList();

            var totalDist = distances.Sum();
            if (totalDist < 1e-10)
            {
                centroids.Add((double[])data[random.Next(data.Count)].Clone());
                continue;
            }

            var threshold = random.NextDouble() * totalDist;
            double cumulative = 0;
            for (int i = 0; i < data.Count; i++)
            {
                cumulative += distances[i];
                if (cumulative >= threshold)
                {
                    centroids.Add((double[])data[i].Clone());
                    break;
                }
            }
        }

        return centroids;
    }

    /// <summary>
    /// 유클리드 거리 계산
    /// </summary>
    private static double EuclideanDistance(double[] a, double[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            var diff = a[i] - b[i];
            sum += diff * diff;
        }
        return Math.Sqrt(sum);
    }

    /// <summary>
    /// 실루엣 점수 계산 (클러스터 품질 평가)
    /// </summary>
    private double CalculateSilhouetteScore(List<double[]> data, List<int> labels, int k)
    {
        if (data.Count <= 2) return 0;

        double totalScore = 0;
        int validCount = 0;

        for (int i = 0; i < data.Count; i++)
        {
            int myCluster = labels[i];
            var sameCluster = Enumerable.Range(0, data.Count)
                .Where(j => j != i && labels[j] == myCluster).ToList();

            if (sameCluster.Count == 0) continue;

            // a(i): 같은 클러스터 내 평균 거리
            double a = sameCluster.Average(j => EuclideanDistance(data[i], data[j]));

            // b(i): 가장 가까운 다른 클러스터까지의 평균 거리
            double b = double.MaxValue;
            for (int c = 0; c < k; c++)
            {
                if (c == myCluster) continue;
                var otherCluster = Enumerable.Range(0, data.Count)
                    .Where(j => labels[j] == c).ToList();
                if (otherCluster.Count == 0) continue;

                double avgDist = otherCluster.Average(j => EuclideanDistance(data[i], data[j]));
                b = Math.Min(b, avgDist);
            }

            if (b == double.MaxValue) continue;

            double s = (b - a) / Math.Max(a, b);
            totalScore += s;
            validCount++;
        }

        return validCount > 0 ? totalScore / validCount : 0;
    }

    /// <summary>
    /// 오디오 특징 벡터
    /// </summary>
    private class AudioFeature
    {
        public double RmsLevel { get; set; } = -60;       // RMS 에너지 (dB)
        public double PeakLevel { get; set; } = -60;      // 피크 레벨 (dB)
        public double ZeroCrossingRate { get; set; } = 0;  // 제로크로싱률
        public double FlatFactor { get; set; } = 0;        // 플랫 팩터

        /// <summary>
        /// 특징 벡터 배열 반환
        /// </summary>
        public double[] ToVector()
        {
            return new[] { RmsLevel, PeakLevel, ZeroCrossingRate, FlatFactor };
        }
    }
}
