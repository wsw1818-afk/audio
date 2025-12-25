using System.Runtime.InteropServices;

namespace AudioRecorder.Audio;

/// <summary>
/// 스레드 세이프 링 버퍼 - 오디오 데이터 버퍼링용
/// </summary>
public class RingBuffer
{
    private readonly float[] _buffer;
    private readonly object _lock = new();
    private volatile int _writePosition;
    private volatile int _readPosition;
    private volatile int _count;

    public int Capacity { get; }

    // lock-free read for checking (approximate count is OK)
    public int Count => _count;
    public int FreeSpace => Capacity - _count;

    public RingBuffer(int capacity)
    {
        Capacity = capacity;
        _buffer = new float[capacity];
    }

    /// <summary>
    /// 데이터 쓰기
    /// </summary>
    public int Write(float[] data, int offset, int count)
    {
        lock (_lock)
        {
            int toWrite = Math.Min(count, FreeSpace);
            if (toWrite == 0) return 0;

            for (int i = 0; i < toWrite; i++)
            {
                _buffer[_writePosition] = data[offset + i];
                _writePosition = (_writePosition + 1) % Capacity;
            }

            _count += toWrite;
            return toWrite;
        }
    }

    /// <summary>
    /// 바이트 배열에서 float로 변환하여 쓰기 (32-bit float)
    /// </summary>
    public int WriteFromBytes(byte[] data, int bytesCount)
    {
        int sampleCount = bytesCount / 4;

        lock (_lock)
        {
            int toWrite = Math.Min(sampleCount, FreeSpace);
            if (toWrite == 0) return 0;

            // Span을 사용한 빠른 변환
            var floatSpan = MemoryMarshal.Cast<byte, float>(data.AsSpan(0, toWrite * 4));

            for (int i = 0; i < toWrite; i++)
            {
                _buffer[_writePosition] = floatSpan[i];
                _writePosition = (_writePosition + 1) % Capacity;
            }

            _count += toWrite;
            return toWrite;
        }
    }

    /// <summary>
    /// 데이터 읽기
    /// </summary>
    public int Read(float[] data, int offset, int count)
    {
        lock (_lock)
        {
            int toRead = Math.Min(count, _count);
            if (toRead == 0) return 0;

            for (int i = 0; i < toRead; i++)
            {
                data[offset + i] = _buffer[_readPosition];
                _readPosition = (_readPosition + 1) % Capacity;
            }

            _count -= toRead;
            return toRead;
        }
    }

    /// <summary>
    /// 데이터 읽기 (제거하지 않고 peek)
    /// </summary>
    public int Peek(float[] data, int offset, int count)
    {
        lock (_lock)
        {
            int toPeek = Math.Min(count, _count);
            if (toPeek == 0) return 0;

            int pos = _readPosition;
            for (int i = 0; i < toPeek; i++)
            {
                data[offset + i] = _buffer[pos];
                pos = (pos + 1) % Capacity;
            }

            return toPeek;
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
    /// 무음 삽입 (드리프트 보정용)
    /// </summary>
    public int InsertSilence(int count)
    {
        lock (_lock)
        {
            int toInsert = Math.Min(count, FreeSpace);
            for (int i = 0; i < toInsert; i++)
            {
                _buffer[_writePosition] = 0f;
                _writePosition = (_writePosition + 1) % Capacity;
            }
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
            Array.Clear(_buffer, 0, Capacity);
        }
    }
}
