using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Devices;

namespace P2PKiwi.WinUI3.Services;

/// <summary>
/// 本地摄像头预览。WinUI 3 没有 CaptureElement（那是 UWP 的控件），所以改走
/// MediaFrameReader + SoftwareBitmapSource：每帧转一份 BGRA8 副本，UI 线程画到 Image 上。
/// </summary>
public sealed class CameraPreviewService
{
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private SoftwareBitmapSource? _source;
    private DispatcherQueue? _dispatcher;

    public SoftwareBitmapSource? Source => _source;

    public async Task<bool> StartAsync(DispatcherQueue dispatcher, string? deviceName = null)
    {
        _dispatcher = dispatcher;
        try
        {
            var init = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video,
            };
            if (!string.IsNullOrWhiteSpace(deviceName) && deviceName != "系统默认")
            {
                var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetVideoCaptureSelector());
                var match = devices.FirstOrDefault(d => d.Name == deviceName);
                if (match is not null) init.VideoDeviceId = match.Id;
            }
            _capture = new MediaCapture();
            await _capture.InitializeAsync(init);
            var frameSource = _capture.FrameSources.Values
                .FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
            if (frameSource is null) return false;
            _reader = await _capture.CreateFrameReaderAsync(frameSource);
            _reader.FrameArrived += OnFrameArrived;
            await _reader.StartAsync();
            _source = new SoftwareBitmapSource();
            return true;
        }
        catch
        {
            await StopAsync();
            return false;
        }
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using var frame = sender.TryAcquireLatestFrame();
        var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (bitmap is null) return;
        // 永远转一份副本：原帧随 frame 释放而失效，副本在画完后由 UI 线程释放。
        var copy = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8);
        var source = _source;
        var dispatcher = _dispatcher;
        if (source is null || dispatcher is null)
        {
            copy.Dispose();
            return;
        }
        _ = dispatcher.TryEnqueue(async () =>
        {
            try { await source.SetBitmapAsync(copy); } catch { } finally { copy.Dispose(); }
        });
    }

    public async Task StopAsync()
    {
        if (_reader is not null)
        {
            _reader.FrameArrived -= OnFrameArrived;
            try { await _reader.StopAsync(); } catch { }
            _reader.Dispose();
            _reader = null;
        }
        _capture?.Dispose();
        _capture = null;
        _source = null;
    }
}
