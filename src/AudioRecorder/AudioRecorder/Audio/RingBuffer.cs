using System.Runtime.InteropServices;

namespace AudioRecorder.Audio;

/// <summary>
/// 고성능 스레드 세이프 링 버퍼 - 오디오 데이터 버퍼링용
/// Array.Copy/Span.CopyTo를 사용한 블록 전송으로 성능 최적화
/// 읽기 전용 Count는 volatile로 lock-free 접근
/// </summary>
public class RingBuffer
{
    private readonly float[] _buffer;
    private readonly object _lock = new();
    private int _writePosition;
    private int _readPosition;
    private volatile int _count;

    public int Capacity { get; }

    /// <summary>
    /// 현재 버퍼에 있는 샘플 수 (Lock-free 읽기)
    /// </summary>
    public int Count => _count;

    /// <summary>
    /// 남은 공간 (Lock-free 읽기)
    /// </summary>
    public int FreeSpace => Capacity - _count;

    public RingBuffer(int capacity)
    {
        Capacity = capacity;
        _buffer = new float[capacity];
    }

    /// <summary>
    /// 데이터 쓰기 - Array.Copy 사용으로 최적화
    /// </summary>
    public int Write(float[] data, int offset, int count)
    {
        lock (_lock)
        {
            int toWrite = Math.Min(count, Capacity - _count);
            if (toWrite == 0) return 0;

            int firstPart = Math.Min(toWrite, Capacity - _writePosition);
            Array.Copy(data, offset, _buffer, _writePosition, firstPart);

            if (firstPart < toWrite)
            {
                Array.Copy(data, offset + firstPart, _buffer, 0, toWrite - firstPart);
            }

            _writePosition = (_writePosition + toWrite) % Capacity;
            _count += toWrite;
            return toWrite;
        }
    }

    /// <summary>
    /// 바이트 배열에서 float로 변환하여 쓰기 - Span 기반 제로카피
    /// </summary>
    public int WriteFromBytes(byte[] data, int bytesCount)
    {
        int sampleCount = bytesCount / 4;

        lock (_lock)
        {
            int toWrite = Math.Min(sampleCount, Capacity - _count);
            if (toWrite == 0) return 0;

            var floatSpan = MemoryMarshal.Cast<byte, float>(data.AsSpan(0, toWrite * 4));

            int firstPart = Math.Min(toWrite, Capacity - _writePosition);
            floatSpan.Slice(0, firstPart).CopyTo(_buffer.AsSpan(_writePosition, firstPart));

            if (firstPart < toWrite)
            {
                floatSpan.Slice(firstPart).CopyTo(_buffer.AsSpan(0, toWrite - firstPart));
            }

            _writePosition = (_writePosition + toWrite) % Capacity;
            _count += toWrite;
            return toWrite;
        }
    }

    /// <summary>
    /// 데이터 읽기 - Array.Copy 사용으로 최적화
    /// </summary>
    public int Read(float[] data, int offset, int count)
    {
        lock (_lock)
        {
            int toRead = Math.Min(count, _count);
            if (toRead == 0) return 0;

            int firstPart = Math.Min(toRead, Capacity - _readPosition);
            Array.Copy(_buffer, _readPosition, data, offset, firstPart);

            if (firstPart < toRead)
            {
                Array.Copy(_buffer, 0, data, offset + firstPart, toRead - firstPart);
            }

            _readPosition = (_readPosition + toRead) % Capacity;
            _count -= toRead;
            return toRead;
        }
    }

    /// <summary>
    /// 지정된 샘플 수만큼 건너뛰기 (드리프트 보정용)
    /// </summary>
    public int Skip(int count)
    {
        lock (_lock)
        {
            int toSkip = Math.Min(count, _count);
            _readPosition = (_readPosition + toSkip) % Capacity;
            _count -= toSkip;
            return toSkip;
        }
    }

    /// <summary>
    /// 무음 삽입 - Array.Clear 사용으로 최적화
    /// </summary>
    public int InsertSilence(int count)
    {
        lock (_lock)
        {
            int toInsert = Math.Min(count, Capacity - _count);
            if (toInsert == 0) return 0;

            int firstPart = Math.Min(toInsert, Capacity - _writePosition);
            Array.Clear(_buffer, _writePosition, firstPart);

            if (firstPart < toInsert)
            {
                Array.Clear(_buffer, 0, toInsert - firstPart);
            }

            _writePosition = (_writePosition + toInsert) % Capacity;
            _count += toInsert;
            return toInsert;
        }
    }

    /// <summary>
    /// 버퍼 클리어
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _writePosition = 0;
            _readPosition = 0;
            _count = 0;
        }
    }
}
