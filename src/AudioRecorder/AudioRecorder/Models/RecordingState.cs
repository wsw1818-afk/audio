namespace AudioRecorder.Models;

/// <summary>
/// 녹음 상태
/// </summary>
public enum RecordingState
{
    Stopped,
    Recording,
    Paused,
    Stopping  // 녹화 정지 진행 중 (인코딩/합성 중)
}
