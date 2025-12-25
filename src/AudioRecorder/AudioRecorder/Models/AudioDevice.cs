using NAudio.CoreAudioApi;

namespace AudioRecorder.Models;

/// <summary>
/// 오디오 장치 정보를 담는 모델
/// </summary>
public class AudioDevice
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string FriendlyName { get; init; } = string.Empty;
    public DataFlow DataFlow { get; init; }
    public bool IsDefault { get; init; }
    public MMDevice? Device { get; init; }

    public static AudioDevice FromMMDevice(MMDevice device, bool isDefault = false)
    {
        return new AudioDevice
        {
            Id = device.ID,
            Name = device.DeviceFriendlyName,
            FriendlyName = device.FriendlyName,
            DataFlow = device.DataFlow,
            IsDefault = isDefault,
            Device = device
        };
    }

    public override string ToString() => IsDefault ? $"{FriendlyName} (기본)" : FriendlyName;
}
