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
            // 幂等清理旧实例：连调两次时旧的 _capture/_reader 不能直接被覆盖掉。
            await StopAsync();
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
            if (frameSource is null)
            {
                // 没找到 Color 帧源：刚建好的 MediaCapture 先释放再返回，否则摄像头被占住。
                await StopAsync();
                return false;
            }
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
        try
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
        catch
        {
            // 某一帧格式不支持等错误只丢这一帧，不让回调线程的异常崩掉预览。
        }
    }

    public async Task StopAsync()
    {
        if (_reader is not null)
        {
            _reader.FrameArrived -= OnFrameArrived;
            try { await _reader.StopAsync(); } catch { }
            try { _reader.Dispose(); } catch { }
            _reader = null;
        }
        try { _capture?.Dispose(); } catch { }
        _capture = null;
        _source = null;
    }
}
