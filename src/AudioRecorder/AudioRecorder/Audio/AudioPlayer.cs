using System;
using System.Diagnostics;
using System.Windows.Media;

namespace AudioRecorder.Audio;

/// <summary>
/// WPF MediaPlayer를 사용한 오디오 재생기
/// NAudio WaveOutEvent/WasapiOut의 Dispose 블로킹 문제 회피
/// </summary>
public class AudioPlayer : IDisposable
{
    private MediaPlayer? _mediaPlayer;
    private volatile bool _disposed;
    private volatile bool _isPlaying;

    public event EventHandler? PlaybackStarted;
    public event EventHandler? PlaybackStopped;
    public event EventHandler<TimeSpan>? PositionChanged;

    public bool IsPlaying => _isPlaying && _mediaPlayer != null;
    public bool IsPaused => !_isPlaying && _mediaPlayer != null && Position > TimeSpan.Zero;

    public TimeSpan CurrentPosition
    {
        get
        {
            try { return _mediaPlayer?.Position ?? TimeSpan.Zero; }
            catch (Exception ex) { Debug.WriteLine($"[AudioPlayer] CurrentPosition error: {ex.Message}"); return TimeSpan.Zero; }
        }
    }

    public TimeSpan TotalDuration
    {
        get
        {
            try { return _mediaPlayer?.NaturalDuration.HasTimeSpan == true
                ? _mediaPlayer.NaturalDuration.TimeSpan : TimeSpan.Zero; }
            catch (Exception ex) { Debug.WriteLine($"[AudioPlayer] TotalDuration error: {ex.Message}"); return TimeSpan.Zero; }
        }
    }

    public TimeSpan Position
    {
        get
        {
            try { return _mediaPlayer?.Position ?? TimeSpan.Zero; }
            catch (Exception ex) { Debug.WriteLine($"[AudioPlayer] Position error: {ex.Message}"); return TimeSpan.Zero; }
        }
    }

    public float Volume
    {
        get
        {
            try { return (float)(_mediaPlayer?.Volume ?? 1.0); }
            catch (Exception ex) { Debug.WriteLine($"[AudioPlayer] Volume get error: {ex.Message}"); return 1.0f; }
        }
        set
        {
            try
            {
                if (_mediaPlayer != null)
                    _mediaPlayer.Volume = Math.Clamp(value, 0f, 1f);
            }
            catch (Exception ex) { Debug.WriteLine($"[AudioPlayer] Volume set error: {ex.Message}"); }
        }
    }

    private double _playbackSpeed = 1.0;
    public double PlaybackSpeed
    {
        get => _playbackSpeed;
        set
        {
            _playbackSpeed = Math.Clamp(value, 0.5, 2.0);
            try
            {
                if (_mediaPlayer != null)
                    _mediaPlayer.SpeedRatio = _playbackSpeed;
            }
            catch (Exception ex) { Debug.WriteLine($"[AudioPlayer] PlaybackSpeed set error: {ex.Message}"); }
        }
    }

    public void Play(string filePath)
    {
        if (_disposed) return;

        Stop();

        try
        {
            _mediaPlayer = new MediaPlayer();
            _mediaPlayer.MediaEnded += OnMediaEnded;
            _mediaPlayer.MediaFailed += OnMediaFailed;
            _mediaPlayer.Open(new Uri(filePath));
            _mediaPlayer.Play();
            _isPlaying = true;

            PlaybackStarted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            Stop();
        }
    }

    private void OnMediaEnded(object? sender, EventArgs e)
    {
        _isPlaying = false;
        PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }

    private void OnMediaFailed(object? sender, ExceptionEventArgs e)
    {
        _isPlaying = false;
        Stop();
    }

    public void Pause()
    {
        if (_disposed || _mediaPlayer == null) return;

        try
        {
            if (_isPlaying)
            {
                _mediaPlayer.Pause();
                _isPlaying = false;
            }
            else
            {
                _mediaPlayer.Play();
                _isPlaying = true;
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[AudioPlayer] Pause error: {ex.Message}"); }
    }

    public void Resume()
    {
        if (_disposed || _mediaPlayer == null) return;

        try
        {
            _mediaPlayer.Play();
            _isPlaying = true;
        }
        catch (Exception ex) { Debug.WriteLine($"[AudioPlayer] Resume error: {ex.Message}"); }
    }

    public void Stop()
    {
        _isPlaying = false;

        var player = _mediaPlayer;
        _mediaPlayer = null;

        if (player != null)
        {
            try
            {
                player.MediaEnded -= OnMediaEnded;
                player.MediaFailed -= OnMediaFailed;
                player.Stop();
                player.Close();
            }
            catch (Exception ex) { Debug.WriteLine($"[AudioPlayer] Stop error: {ex.Message}"); }
        }
    }

    public void Seek(TimeSpan position)
    {
        if (_mediaPlayer == null || _disposed) return;

        try
        {
            _mediaPlayer.Position = position;
            PositionChanged?.Invoke(this, position);
        }
        catch (Exception ex) { Debug.WriteLine($"[AudioPlayer] Seek error: {ex.Message}"); }
    }

    /// <summary>
    /// 현재 위치에서 지정된 초만큼 이동 (음수: 되감기, 양수: 앞으로)
    /// </summary>
    public void Skip(double seconds)
    {
        if (_mediaPlayer == null || _disposed) return;

        try
        {
            var newPosition = _mediaPlayer.Position + TimeSpan.FromSeconds(seconds);

            // 범위 제한
            if (newPosition < TimeSpan.Zero)
                newPosition = TimeSpan.Zero;
            else if (_mediaPlayer.NaturalDuration.HasTimeSpan &&
                     newPosition > _mediaPlayer.NaturalDuration.TimeSpan)
                newPosition = _mediaPlayer.NaturalDuration.TimeSpan;

            _mediaPlayer.Position = newPosition;
            PositionChanged?.Invoke(this, newPosition);
        }
        catch (Exception ex) { Debug.WriteLine($"[AudioPlayer] Skip error: {ex.Message}"); }
    }

    /// <summary>
    /// 5초 되감기
    /// </summary>
    public void Rewind5() => Skip(-5);

    /// <summary>
    /// 10초 되감기
    /// </summary>
    public void Rewind10() => Skip(-10);

    /// <summary>
    /// 5초 앞으로
    /// </summary>
    public void Forward5() => Skip(5);

    /// <summary>
    /// 10초 앞으로
    /// </summary>
    public void Forward10() => Skip(10);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
