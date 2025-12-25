using System;
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
            catch { return TimeSpan.Zero; }
        }
    }

    public TimeSpan TotalDuration
    {
        get
        {
            try { return _mediaPlayer?.NaturalDuration.HasTimeSpan == true
                ? _mediaPlayer.NaturalDuration.TimeSpan : TimeSpan.Zero; }
            catch { return TimeSpan.Zero; }
        }
    }

    public TimeSpan Position
    {
        get
        {
            try { return _mediaPlayer?.Position ?? TimeSpan.Zero; }
            catch { return TimeSpan.Zero; }
        }
    }

    public float Volume
    {
        get
        {
            try { return (float)(_mediaPlayer?.Volume ?? 1.0); }
            catch { return 1.0f; }
        }
        set
        {
            try
            {
                if (_mediaPlayer != null)
                    _mediaPlayer.Volume = Math.Clamp(value, 0f, 1f);
            }
            catch { }
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
        catch { }
    }

    public void Resume()
    {
        if (_disposed || _mediaPlayer == null) return;

        try
        {
            _mediaPlayer.Play();
            _isPlaying = true;
        }
        catch { }
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
            catch { }
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
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
