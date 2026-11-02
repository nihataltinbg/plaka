using OpenCvSharp;
using OpenCvSharp.Extensions;

namespace PlakaTakip;

/// <summary>
/// IP kamera (RTSP/HTTP) veya web kamerasından (kaynak "0", "1"...) sürekli görüntü okur.
/// Bağlantı koparsa otomatik yeniden bağlanır.
/// </summary>
public sealed class CameraService : IDisposable
{
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _task;
    private Mat? _latest;

    /// <summary>Her karede tetiklenir (arka plan thread'inde). Bitmap'in sahibi alıcıdır.</summary>
    public event Action<Bitmap>? FrameReady;
    public event Action<string>? StatusChanged;

    public void Start(string source)
    {
        Stop();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _task = Task.Run(() => CaptureLoop(source, token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _task?.Wait(3000); } catch (AggregateException) { }
        _cts = null;
        _task = null;

        lock (_lock)
        {
            _latest?.Dispose();
            _latest = null;
        }
    }

    /// <summary>İşleme için en son kareyi verir (aynı kare iki kez verilmez). Sahibi çağıran taraftır.</summary>
    public Mat? TakeLatestFrame()
    {
        lock (_lock)
        {
            var frame = _latest;
            _latest = null;
            return frame;
        }
    }

    private void CaptureLoop(string source, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                StatusChanged?.Invoke("Kameraya bağlanılıyor...");
                using var capture = Open(source);

                if (capture is null)
                {
                    StatusChanged?.Invoke("Bağlanılamadı. 3 sn sonra tekrar denenecek...");
                    if (token.WaitHandle.WaitOne(3000)) return;
                    continue;
                }

                StatusChanged?.Invoke("Bağlandı - görüntü akıyor");
                using var frame = new Mat();
                var failures = 0;

                while (!token.IsCancellationRequested)
                {
                    if (!capture.Read(frame) || frame.Empty())
                    {
                        if (++failures > 40) break; // yeniden bağlan
                        Thread.Sleep(50);
                        continue;
                    }

                    failures = 0;

                    lock (_lock)
                    {
                        _latest ??= frame.Clone(); // işleme thread'i bir önceki kareyi aldıysa yenisini hazırla
                    }

                    FrameReady?.Invoke(BitmapConverter.ToBitmap(frame));
                }

                if (!token.IsCancellationRequested)
                    StatusChanged?.Invoke("Bağlantı koptu, yeniden bağlanılıyor...");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke("Kamera hatası: " + ex.Message);
                if (token.WaitHandle.WaitOne(3000)) return;
            }
        }
    }

    private static VideoCapture? Open(string source)
    {
        var capture = int.TryParse(source, out var index)
            ? new VideoCapture(index)
            : new VideoCapture(source, VideoCaptureAPIs.FFMPEG);

        if (capture.IsOpened()) return capture;

        capture.Dispose();
        return null;
    }

    public void Dispose() => Stop();
}
