using NAudio.CoreAudioApi;
using AudioRecorder.Models;

namespace AudioRecorder.Audio;

/// <summary>
/// 오디오 장치 관리자 - 마이크/출력 장치 열거 및 선택
/// </summary>
public class DeviceManager : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator;
    private bool _disposed;

    public event EventHandler<DeviceChangedEventArgs>? DeviceChanged;

    public DeviceManager()
    {
        _enumerator = new MMDeviceEnumerator();
    }

    /// <summary>
    /// 마이크(입력) 장치 목록 가져오기
    /// </summary>
    public IReadOnlyList<AudioDevice> GetInputDevices()
    {
        var devices = new List<AudioDevice>();
        var defaultDevice = GetDefaultInputDevice();

        try
        {
            var collection = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            foreach (var device in collection)
            {
                var isDefault = defaultDevice != null && device.ID == defaultDevice.Id;
                devices.Add(AudioDevice.FromMMDevice(device, isDefault));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"마이크 장치 열거 실패: {ex.Message}");
        }

        return devices;
    }

    /// <summary>
    /// 출력(스피커) 장치 목록 가져오기
    /// </summary>
    public IReadOnlyList<AudioDevice> GetOutputDevices()
    {
        var devices = new List<AudioDevice>();
        var defaultDevice = GetDefaultOutputDevice();

        try
        {
            var collection = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var device in collection)
            {
                var isDefault = defaultDevice != null && device.ID == defaultDevice.Id;
                devices.Add(AudioDevice.FromMMDevice(device, isDefault));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"출력 장치 열거 실패: {ex.Message}");
        }

        return devices;
    }

    /// <summary>
    /// 기본 마이크 장치 가져오기
    /// </summary>
    public AudioDevice? GetDefaultInputDevice()
    {
        try
        {
            var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            return AudioDevice.FromMMDevice(device, true);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 기본 출력 장치 가져오기
    /// </summary>
    public AudioDevice? GetDefaultOutputDevice()
    {
        try
        {
            var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return AudioDevice.FromMMDevice(device, true);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 장치 ID로 MMDevice 가져오기
    /// </summary>
    public MMDevice? GetDeviceById(string deviceId)
    {
        try
        {
            return _enumerator.GetDevice(deviceId);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _enumerator.Dispose();
    }
}

public class DeviceChangedEventArgs : EventArgs
{
    public string DeviceId { get; init; } = string.Empty;
    public DeviceChangeType ChangeType { get; init; }
}

public enum DeviceChangeType
{
    Added,
    Removed,
    DefaultChanged
}
