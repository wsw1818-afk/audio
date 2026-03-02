using System.Buffers;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AudioRecorder.Services;

/// <summary>
/// Chrome DevTools Protocol 기반 DRM 우회 캡처 서비스
/// Netflix, Disney+, Amazon Prime 등 DRM 콘텐츠 캡처 및 녹화 지원
/// </summary>
public class ChromeDrmCaptureService : IDisposable
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private const int DefaultDebugPort = 9222;
    private int _commandId;
    private bool _disposed;
    private ClientWebSocket? _webSocket;

    // 실시간 캡처용
    private volatile bool _isCapturing;
    private readonly object _frameLock = new();
    private byte[]? _currentFrame;
    private int _frameWidth;
    private int _frameHeight;
    private long _frameCount;
    private int _frameRate = 30;
    private readonly Stopwatch _stopwatch = new();
    
    // Task 관리
    private CancellationTokenSource? _screencastCts;
    private Task? _screencastTask;

    /// <summary>
    /// 캡처 진행 상태 이벤트
    /// </summary>
    public event Action<string>? StatusChanged;

    /// <summary>
    /// 새 프레임 사용 가능 이벤트
    /// </summary>
    public event EventHandler<ChromeFrameEventArgs>? FrameAvailable;

    /// <summary>
    /// 오류 발생 이벤트
    /// </summary>
    public event EventHandler<ChromeCaptureErrorEventArgs>? ErrorOccurred;

    /// <summary>
    /// 캡처 중 여부
    /// </summary>
    public bool IsCapturing => _isCapturing;

    /// <summary>
    /// 현재 프레임 너비
    /// </summary>
    public int FrameWidth => _frameWidth;

    /// <summary>
    /// 현재 프레임 높이
    /// </summary>
    public int FrameHeight => _frameHeight;

    /// <summary>
    /// 캡처된 프레임 수
    /// </summary>
    public long FrameCount => _frameCount;

    /// <summary>
    /// 경과 시간
    /// </summary>
    public TimeSpan ElapsedTime => _stopwatch.Elapsed;

    /// <summary>
    /// Chrome이 디버그 모드로 실행 중인지 확인
    /// </summary>
    public async Task<bool> IsChromeDebugAvailableAsync(int port = DefaultDebugPort)
    {
        try
        {
            var response = await _httpClient.GetStringAsync($"http://localhost:{port}/json");
            var tabs = JsonSerializer.Deserialize<JsonElement[]>(response);
            return tabs != null && tabs.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Chrome을 디버그 모드로 자동 실행
    /// </summary>
    public async Task<bool> LaunchChromeDebugModeAsync(int port = DefaultDebugPort, string? url = null)
    {
        try
        {
            StatusChanged?.Invoke("Chrome 경로 검색 중...");
            var chromePath = FindChromePath();
            if (chromePath == null)
            {
                StatusChanged?.Invoke("Chrome을 찾을 수 없습니다.");
                return false;
            }

            // 이미 디버그 모드로 실행 중인지 확인
            if (await IsChromeDebugAvailableAsync(port))
            {
                StatusChanged?.Invoke("Chrome이 이미 디버그 모드로 실행 중입니다.");
                return true;
            }

            StatusChanged?.Invoke("Chrome 디버그 모드로 실행 중...");

            // 디버그용 임시 프로필 디렉토리
            var debugUserDataDir = Path.Combine(
                Path.GetTempPath(), 
                $"AudioRecorder_ChromeDebug_{port}");

            var arguments = $"--remote-debugging-port={port} " +
                          $"--user-data-dir=\"{debugUserDataDir}\" " +
                          $"--no-first-run " +
                          $"--no-default-browser-check " +
                          $"--disable-features=IsolateOrigins,site-per-process " +
                          $"--disable-blink-features=AutomationControlled " +
                          $"--start-maximized";

            if (!string.IsNullOrEmpty(url))
                arguments += $" \"{url}\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = chromePath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = false
            };

            var process = Process.Start(startInfo);
            if (process == null)
            {
                StatusChanged?.Invoke("Chrome 실행 실패");
                return false;
            }

            // Chrome이 준비될 때까지 대기
            StatusChanged?.Invoke("Chrome 준비 대기 중...");
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(1000);
                if (await IsChromeDebugAvailableAsync(port))
                {
                    StatusChanged?.Invoke("Chrome 디버그 모드 준비 완료");
                    return true;
                }
            }

            StatusChanged?.Invoke("Chrome 디버그 모드 연결 시간 초과");
            return false;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Chrome 실행 오류: {ex.Message}");
            Debug.WriteLine($"[ChromeDrmCapture] 실행 오류: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Chrome 실행 파일 경로 찾기
    /// </summary>
    private string? FindChromePath()
    {
        // 1. 레지스트리에서 찾기
        try
        {
            var regValue = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe",
                null, null);
            if (regValue is string path && File.Exists(path))
                return path;
        }
        catch { }

        // 2. 기본 경로들 검색
        var chromePaths = new[]
        {
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"Google\Chrome\Application\chrome.exe")
        };

        foreach (var path in chromePaths)
        {
            if (File.Exists(path))
                return path;
        }

        // 3. PATH에서 찾기
        try
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var pathDir in pathEnv.Split(';'))
            {
                var fullPath = Path.Combine(pathDir.Trim(), "chrome.exe");
                if (File.Exists(fullPath))
                    return fullPath;
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// 실시간 화면 녹화 시작 (비디오 스트림)
    /// </summary>
    public async Task<bool> StartCaptureAsync(
        int frameRate = 30,
        int debugPort = DefaultDebugPort,
        string? targetUrl = null)
    {
        if (_isCapturing)
            throw new InvalidOperationException("이미 캡처 중입니다.");

        // Chrome 디버그 모드 확인/실행
        if (!await IsChromeDebugAvailableAsync(debugPort))
        {
            StatusChanged?.Invoke("Chrome 디버그 모드가 필요합니다. 자동 실행을 시도합니다...");
            if (!await LaunchChromeDebugModeAsync(debugPort, targetUrl))
            {
                return false;
            }
        }

        var wsUrl = await GetWebSocketDebuggerUrlAsync(debugPort, targetUrl);
        if (string.IsNullOrEmpty(wsUrl))
        {
            StatusChanged?.Invoke("Chrome WebSocket 디버거 URL을 가져올 수 없습니다.");
            return false;
        }

        try
        {
            StatusChanged?.Invoke("Chrome CDP WebSocket 연결 중...");
            _webSocket = new ClientWebSocket();
            await _webSocket.ConnectAsync(new Uri(wsUrl), CancellationToken.None);

            // 페이지 크기 측정
            var layoutMetrics = await SendCdpCommandAsync(_webSocket, "Page.getLayoutMetrics", new { });
            if (layoutMetrics != null && layoutMetrics.HasValue)
            {
                var visualViewport = layoutMetrics.Value.GetProperty("visualViewport");
                _frameWidth = visualViewport.GetProperty("clientWidth").GetInt32();
                _frameHeight = visualViewport.GetProperty("clientHeight").GetInt32();
            }
            else
            {
                _frameWidth = 1920;
                _frameHeight = 1080;
            }

            // 스크린캐스트 시작 (실시간 스트리밍)
            StatusChanged?.Invoke("CDP 스크린캐스트 시작...");
            await SendCdpCommandAsync(_webSocket, "Page.startScreencast", new
            {
                format = "jpeg",
                quality = 80,
                maxWidth = _frameWidth,
                maxHeight = _frameHeight,
                everyNthFrame = 1
            });

            // 스크린캐스트 이벤트 핸들러 설정
            _screencastCts = new CancellationTokenSource();
            _screencastTask = Task.Run(() => HandleScreencastEventsAsync(_screencastCts.Token));

            _frameRate = Math.Clamp(frameRate, 1, 30);
            _isCapturing = true;
            _frameCount = 0;
            _stopwatch.Restart();

            StatusChanged?.Invoke($"Chrome DRM 캡처 시작: {_frameWidth}x{_frameHeight} @ {_frameRate}fps");
            return true;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"캡처 시작 오류: {ex.Message}");
            CleanupWebSocket();
            return false;
        }
    }

    /// <summary>
    /// 스크린캐스트 이벤트 처리
    /// </summary>
    private async Task HandleScreencastEventsAsync(CancellationToken ct)
    {
        try
        {
            while (_isCapturing && _webSocket?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var message = await ReceiveFullMessageAsync(_webSocket, ct);
                var json = JsonSerializer.Deserialize<JsonElement>(message);

                // 스크린캐스트 프레임 이벤트 처리
                if (json.TryGetProperty("method", out var method) &&
                    method.GetString() == "Page.screencastFrame")
                {
                    if (json.TryGetProperty("params", out var parameters))
                    {
                        var data = parameters.GetProperty("data").GetString();
                        var sessionId = parameters.GetProperty("sessionId").GetString();

                        if (!string.IsNullOrEmpty(data))
                        {
                            await ProcessScreencastFrameAsync(data);
                        }

                        // 다음 프레임 요청
                        if (_webSocket?.State == WebSocketState.Open)
                        {
                            await SendCdpCommandAsync(_webSocket, "Page.screencastFrameAck", new
                            {
                                sessionId = sessionId
                            });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ChromeDrmCapture] 스크린캐스트 이벤트 오류: {ex.Message}");
        }
    }

    /// <summary>
    /// 스크린캐스트 프레임 처리
    /// </summary>
    private Task ProcessScreencastFrameAsync(string base64Data)
    {
        try
        {
            var imageBytes = Convert.FromBase64String(base64Data);

            using var ms = new MemoryStream(imageBytes);
            using var bitmap = new Bitmap(ms);

            // Bitmap을 byte array로 변환 (BGRA 형식)
            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var bitmapData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            try
            {
                var size = bitmapData.Stride * bitmapData.Height;
                var frameData = new byte[size];
                Marshal.Copy(bitmapData.Scan0, frameData, 0, size);

                lock (_frameLock)
                {
                    _currentFrame = frameData;
                    _frameWidth = bitmap.Width;
                    _frameHeight = bitmap.Height;
                }

                var frameNumber = Interlocked.Increment(ref _frameCount);

                // frameData는 이 메서드 내에서 새로 할당된 배열이므로
                // _currentFrame과 이벤트 수신자가 동일 참조를 가지지만,
                // 다음 프레임에서 _currentFrame은 새 배열로 교체되므로 안전.
                // 이벤트 수신자(ScreenRecordingEngine.ProcessFrame)는
                // 즉시 _frameBuffer에 BlockCopy하므로 race condition 없음.
                FrameAvailable?.Invoke(this, new ChromeFrameEventArgs
                {
                    FrameNumber = frameNumber,
                    Timestamp = _stopwatch.Elapsed,
                    Width = bitmap.Width,
                    Height = bitmap.Height,
                    Stride = bitmapData.Stride,
                    FrameData = frameData
                });
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ChromeDrmCapture] 프레임 처리 오류: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 캡처 중지
    /// </summary>
    public async Task StopCaptureAsync()
    {
        if (!_isCapturing) return;

        _isCapturing = false;
        _stopwatch.Stop();

        // 스크린캐스트 Task 취소 및 대기
        try
        {
            _screencastCts?.Cancel();
            if (_screencastTask != null)
            {
                await _screencastTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch { }
        finally
        {
            _screencastCts?.Dispose();
            _screencastCts = null;
            _screencastTask = null;
        }

        try
        {
            if (_webSocket?.State == WebSocketState.Open)
            {
                await SendCdpCommandAsync(_webSocket, "Page.stopScreencast", new { });
            }
        }
        catch { }

        CleanupWebSocket();
        StatusChanged?.Invoke("Chrome DRM 캡처 중지됨");
    }

    /// <summary>
    /// 정적 스크린샷 캡처 (단일 이미지)
    /// </summary>
    public async Task<Bitmap?> CaptureDrmContentAsync(
        int debugPort = DefaultDebugPort,
        bool captureBeyondViewport = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            StatusChanged?.Invoke("Chrome 연결 확인 중...");
            
            var wsUrl = await GetWebSocketDebuggerUrlAsync(debugPort);
            if (string.IsNullOrEmpty(wsUrl))
            {
                StatusChanged?.Invoke("Chrome 디버그 모드가 아닙니다.");
                return null;
            }

            StatusChanged?.Invoke("CDP WebSocket 연결 중...");
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(wsUrl), cancellationToken);

            StatusChanged?.Invoke("DRM 콘텐츠 캡처 중... (GPU 직접 접근)");
            
            var screenshotResult = await SendCdpCommandAsync(ws, "Page.captureScreenshot", new
            {
                format = "png",
                captureBeyondViewport = captureBeyondViewport,
                fromSurface = true  // DRM 우회 핵심
            }, cancellationToken);

            if (screenshotResult == null || !screenshotResult.HasValue)
                return null;

            var base64Data = screenshotResult.Value.GetProperty("data").GetString();
            if (string.IsNullOrEmpty(base64Data))
                return null;

            var imageBytes = Convert.FromBase64String(base64Data);
            using var ms = new MemoryStream(imageBytes);
            var bitmap = new Bitmap(ms);

            StatusChanged?.Invoke($"캡처 완료: {bitmap.Width}x{bitmap.Height}");
            return bitmap;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"캡처 오류: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 현재 프레임 데이터 가져오기
    /// </summary>
    public byte[]? GetCurrentFrame()
    {
        lock (_frameLock)
        {
            return _currentFrame;
        }
    }

    /// <summary>
    /// 현재 프레임 복사본 가져오기 (항상 복사본 반환)
    /// </summary>
    public byte[]? GetCurrentFrameCopy()
    {
        lock (_frameLock)
        {
            if (_currentFrame == null) return null;
            var copy = new byte[_currentFrame.Length];
            Buffer.BlockCopy(_currentFrame, 0, copy, 0, _currentFrame.Length);
            return copy;
        }
    }
    
    /// <summary>
    /// 현재 프레임 데이터 가져오기 (남부 버퍼 직접 반환 - 주의: 다음 프레임에서 덮어쓰여짐)
    /// </summary>
    public byte[]? GetCurrentFrameDirect()
    {
        lock (_frameLock)
        {
            return _currentFrame;
        }
    }

    /// <summary>
    /// 현재 프레임을 지정된 버퍼에 복사
    /// </summary>
    public bool CopyCurrentFrameTo(byte[] buffer)
    {
        lock (_frameLock)
        {
            if (_currentFrame == null || buffer.Length < _currentFrame.Length)
                return false;
            Buffer.BlockCopy(_currentFrame, 0, buffer, 0, _currentFrame.Length);
            return true;
        }
    }

    /// <summary>
    /// Chrome 디버거 WebSocket URL 가져오기
    /// </summary>
    private async Task<string?> GetWebSocketDebuggerUrlAsync(int port, string? targetUrl = null)
    {
        try
        {
            var response = await _httpClient.GetStringAsync($"http://localhost:{port}/json");
            var tabs = JsonSerializer.Deserialize<JsonElement[]>(response);

            if (tabs != null && tabs.Length > 0)
            {
                // 특정 URL 검색
                if (!string.IsNullOrEmpty(targetUrl))
                {
                    var targetUrlBase = targetUrl.Split('?')[0].Split('#')[0];
                    foreach (var tab in tabs)
                    {
                        if (tab.TryGetProperty("url", out var urlProp))
                        {
                            var url = urlProp.GetString() ?? "";
                            if (url.Contains(targetUrlBase) &&
                                tab.TryGetProperty("webSocketDebuggerUrl", out var wsUrl))
                            {
                                return wsUrl.GetString();
                            }
                        }
                    }
                }

                // 첫 번째 페이지 반환
                foreach (var tab in tabs)
                {
                    if (tab.TryGetProperty("type", out var type) &&
                        type.GetString() == "page" &&
                        tab.TryGetProperty("webSocketDebuggerUrl", out var wsUrl))
                    {
                        return wsUrl.GetString();
                    }
                }
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// CDP 명령 전송
    /// </summary>
    private async Task<JsonElement?> SendCdpCommandAsync(
        ClientWebSocket ws, 
        string method, 
        object parameters,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var messageId = Interlocked.Increment(ref _commandId);
            var message = new
            {
                id = messageId,
                method = method,
                @params = parameters
            };

            var json = JsonSerializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(
                new ArraySegment<byte>(bytes), 
                WebSocketMessageType.Text, 
                true, 
                cancellationToken);

            // 15초 타임아웃
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            while (!cts.Token.IsCancellationRequested)
            {
                var fullMessage = await ReceiveFullMessageAsync(ws, cts.Token);
                var responseJson = JsonSerializer.Deserialize<JsonElement>(fullMessage);

                if (responseJson.TryGetProperty("id", out var idProp) && 
                    idProp.GetInt32() == messageId)
                {
                    if (responseJson.TryGetProperty("result", out var resultElement))
                        return resultElement;
                    return null;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ChromeDrmCapture] CDP 명령 오류: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// WebSocket에서 전체 메시지 수신
    /// </summary>
    private async Task<string> ReceiveFullMessageAsync(ClientWebSocket ws, CancellationToken ct)
    {
        // 2MB 버퍼 - 4K 화면의 JPEG 데이터 수용
        var buffer = ArrayPool<byte>.Shared.Rent(2 * 1024 * 1024);
        try
        {
            var result = new StringBuilder();

            while (true)
            {
                var response = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (response.MessageType == WebSocketMessageType.Close)
                    break;
                result.Append(Encoding.UTF8.GetString(buffer, 0, response.Count));
                if (response.EndOfMessage) break;
            }

            return result.ToString();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void CleanupWebSocket()
    {
        try { _webSocket?.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _webSocket?.Dispose(); } catch { }
        _webSocket = null;
    }

    /// <summary>
    /// Chrome 디버그 모드 실행 가이드 메시지
    /// </summary>
    public static string GetChromeDebugGuide()
    {
        return """
            Chrome을 DRM 캡처용으로 실행하려면:

            1. Chrome 완전 종료 (트레이 아이콘도 종료)
            2. 명령 프롬프트에서 다음 실행:

               "C:\Program Files\Google\Chrome\Application\chrome.exe" --remote-debugging-port=9222

            3. DRM 콘텐츠가 있는 사이트 접속
            4. 캡처 실행

            또는 프로그램에서 자동 실행 기능을 사용하세요.
            """;
    }

    /// <summary>
    /// 비동기 Dispose (권장)
    /// </summary>
    public async Task DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopCaptureAsync();
        _httpClient.Dispose();
    }

    /// <summary>
    /// 동기 Dispose
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        
        DisposeAsync().GetAwaiter().GetResult();
    }
}

/// <summary>
/// Chrome 프레임 이벤트 인자
/// </summary>
public class ChromeFrameEventArgs : EventArgs
{
    public long FrameNumber { get; init; }
    public TimeSpan Timestamp { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Stride { get; init; }
    public byte[] FrameData { get; init; } = Array.Empty<byte>();
}

/// <summary>
/// Chrome 캡처 오류 이벤트 인자
/// </summary>
public class ChromeCaptureErrorEventArgs : EventArgs
{
    public string Message { get; init; } = string.Empty;
}
