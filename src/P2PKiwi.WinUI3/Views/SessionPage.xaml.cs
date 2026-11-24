using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using P2PKiwi.WinUI3.Services;
using P2PKiwi.WinUI3.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;

namespace P2PKiwi.WinUI3.Views;

public sealed partial class SessionPage : Page
{
    private readonly SessionViewModel _vm = new(SessionManager.Shared);
    private readonly Dictionary<string, (Polygon Cursor, TextBlock Label)> _cursors = new();

    private readonly CameraPreviewService _cameraPreview = new();
    private bool _cameraOn;
    private DateTime _lastCursorSent = DateTime.MinValue;
    // PropertyChanged 的 handler 存成字段，OnNavigatedFrom 才能取消订阅（匿名 lambda 取消不掉）。
    private readonly PropertyChangedEventHandler _vmPropertyChangedHandler;

    // 远端视频：WriteableBitmap 只在尺寸变化时重建；帧先攒着，UI 线程一次只画最新的一帧。
    private WriteableBitmap? _remoteBitmap;
    private byte[]? _pendingFrame;
    private int _pendingWidth;
    private int _pendingHeight;
    private bool _blitPending;

    public SessionPage()
    {
        InitializeComponent();
        ParticipantList.ItemsSource = _vm.Participants;
        ChatList.ItemsSource = _vm.Messages;
        _vm.RemoteCursorMoved += OnRemoteCursorMoved;
        _vm.RemoteVideoFrame += OnRemoteVideoFrame;
        _vmPropertyChangedHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(SessionViewModel.IsMicMuted)) UpdateMicButton();
            else if (e.PropertyName == nameof(SessionViewModel.IsRecording)) UpdateRecordButton();
            else if (e.PropertyName == nameof(SessionViewModel.AnswerLink)) UpdateAnswerPanel();
        };
        _vm.PropertyChanged += _vmPropertyChangedHandler;
    }

    protected override async void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            if (e.Parameter is string link)
            {
                await _vm.JoinAsync(link);
            }
            else if (!await _vm.LoadCurrentAsync())
            {
                // 没有当前会话：给一句引导，而不是一张空白页。
                _vm.AddSystemMessage("还没有会话：先去主页点「开始分享」发起投屏，或粘贴邀请链接加入。");
            }
        }
        catch (Exception ex)
        {
            // 链接损坏、只有 8 位房间码等解析失败都在这里接住：聊天区写明原因，给回主页按钮，不弹全局报错框。
            _vm.AddSystemMessage($"加入失败：{ex.Message}");
            BackHomeButton.Visibility = Visibility.Visible;
        }
        UpdateMicButton();
        UpdateRecordButton();
        UpdateAnswerPanel();
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // 离开页面时取消订阅：VM 事件与共享传输的订阅都要断开，否则旧页面被事件链拽住泄漏。
        _vm.RemoteCursorMoved -= OnRemoteCursorMoved;
        _vm.RemoteVideoFrame -= OnRemoteVideoFrame;
        _vm.PropertyChanged -= _vmPropertyChangedHandler;
        _vm.UnwireRtc();
    }

    /// <summary>本地光标发给对方（相对坐标 0..1），50ms 节流；未接通时 VM 里不发。</summary>
    private void RemoteVideo_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastCursorSent).TotalMilliseconds < 50) return;
        _lastCursorSent = now;
        var w = RemoteVideo.ActualWidth;
        var h = RemoteVideo.ActualHeight;
        if (w <= 0 || h <= 0) return;
        var point = e.GetCurrentPoint(RemoteVideo);
        var x = Math.Clamp(point.Position.X / w, 0, 1);
        var y = Math.Clamp(point.Position.Y / h, 0, 1);
        _ = _vm.SendCursorAsync(x, y);
    }

    private void BackHome_Click(object sender, RoutedEventArgs e)
    {
        App.MainWindow?.NavigateToHome();
    }

    private void OnRemoteCursorMoved(string peerId, string name, double x, double y)
    {
        // 远端光标：按相对坐标（0..1）换算到画面尺寸，画一个带名字的箭头。
        // 原版 native/overlay-sidecar 做的是系统级光标叠加；WinUI 版先做应用内叠加。
        DispatcherQueue.TryEnqueue(() =>
        {
            var w = RemoteVideo.ActualWidth; var h = RemoteVideo.ActualHeight;
            if (!_cursors.TryGetValue(peerId, out var entry))
            {
                var cursor = new Polygon
                {
                    Points = new PointCollection { new(0, 0), new(0, 16), new(4, 12), new(7, 18), new(9, 16), new(6, 11), new(11, 11) },
                    // 光标颜色取设置里的前景色（默认品牌紫）。
                    Fill = new SolidColorBrush(ParseColor(AppSettings.Current.ForegroundColor, Color.FromArgb(255, 0x67, 0x50, 0xA4))),
                    Stroke = new SolidColorBrush(Microsoft.UI.Colors.White), StrokeThickness = 1,
                };
                var label = new TextBlock { Text = name, FontSize = 11, Padding = new Thickness(4, 1, 4, 1) };
                CursorLayer.Children.Add(cursor);
                CursorLayer.Children.Add(label);
                _cursors[peerId] = (cursor, label);
                entry = (cursor, label);
            }
            Canvas.SetLeft(entry.Cursor, x * w);
            Canvas.SetTop(entry.Cursor, y * h);
            Canvas.SetLeft(entry.Label, x * w + 12);
            Canvas.SetTop(entry.Label, y * h + 12);
        });
    }

    private static Color ParseColor(string hex, Color fallback)
    {
        try
        {
            var h = hex.Trim().TrimStart('#');
            if (h.Length != 6) return fallback;
            return Color.FromArgb(255,
                Convert.ToByte(h.Substring(0, 2), 16),
                Convert.ToByte(h.Substring(2, 2), 16),
                Convert.ToByte(h.Substring(4, 2), 16));
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>远端视频帧（BGRA8）：画到 WriteableBitmap；只在尺寸变化时重建位图。</summary>
    private void OnRemoteVideoFrame(byte[] bgra, int width, int height)
    {
        _pendingFrame = bgra;
        _pendingWidth = width;
        _pendingHeight = height;
        if (_blitPending) return;
        _blitPending = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _blitPending = false;
            var frame = _pendingFrame;
            if (frame == null) return;
            int w = _pendingWidth, h = _pendingHeight;
            if (w <= 0 || h <= 0) return;
            if (_remoteBitmap == null || _remoteBitmap.PixelWidth != w || _remoteBitmap.PixelHeight != h)
            {
                _remoteBitmap = new WriteableBitmap(w, h);
                RemoteVideoImage.Source = _remoteBitmap;
                RemoteVideoPlaceholder.Visibility = Visibility.Collapsed;
            }
            using var stream = _remoteBitmap.PixelBuffer.AsStream();
            stream.Seek(0, SeekOrigin.Begin);
            stream.Write(frame, 0, Math.Min(frame.Length, w * h * 4));
            _remoteBitmap.Invalidate();
        });
    }

    private void UpdateMicButton()
    {
        MicButton.Label = _vm.IsMicMuted ? "取消静音" : "静音";
    }

    private void UpdateRecordButton()
    {
        RecordButton.Label = _vm.IsRecording ? "停止录制" : "录制";
    }

    /// <summary>加入方：显示 answer 链接面板；接通后（AnswerLink 被清空）自动隐藏。</summary>
    private void UpdateAnswerPanel()
    {
        var link = _vm.AnswerLink;
        AnswerPanel.Visibility = string.IsNullOrWhiteSpace(link) ? Visibility.Collapsed : Visibility.Visible;
        if (!string.IsNullOrWhiteSpace(link)) AnswerLinkBox.Text = link;
    }

    private void CopyAnswer_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AnswerLinkBox.Text)) return;
        var package = new DataPackage();
        package.SetText(AnswerLinkBox.Text);
        Clipboard.SetContent(package);
    }

    private async void MicButton_Click(object sender, RoutedEventArgs e)
    {
        try { await _vm.ToggleMicAsync(); }
        catch (Exception ex) { _vm.AddSystemMessage($"静音切换失败：{ex.Message}"); }
    }

    private async void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        try { await _vm.ToggleRecordingAsync(); }
        catch (Exception ex) { _vm.AddSystemMessage($"录制失败：{ex.Message}"); }
    }

    private void ChatButton_Click(object sender, RoutedEventArgs e) => ChatInput.Focus(FocusState.Programmatic);
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => App.MainWindow?.ToggleFullscreen();
    private async void Leave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _cameraPreview.StopAsync();
            await _vm.LeaveAsync();
        }
        catch (Exception ex)
        {
            _vm.AddSystemMessage($"离开会话失败：{ex.Message}");
        }
        App.MainWindow?.NavigateToHome();
    }

    /// <summary>本地摄像头小窗：开 = MediaFrameReader 预览（WinUI 3 没有 CaptureElement），关 = 停预览。失败时只留占位文字，不弹错。</summary>
    private async void CameraButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cameraOn)
        {
            _cameraOn = false;
            await _cameraPreview.StopAsync();
            CameraPreviewImage.Source = null;
            CameraPlaceholder.Text = "摄像头未开启";
            CameraPlaceholder.Visibility = Visibility.Visible;
            CameraPiP.Visibility = Visibility.Collapsed;
            return;
        }
        var ok = await _cameraPreview.StartAsync(DispatcherQueue, AppSettings.Current.CameraDevice);
        if (ok && _cameraPreview.Source is not null)
        {
            _cameraOn = true;
            CameraPreviewImage.Source = _cameraPreview.Source;
            CameraPlaceholder.Visibility = Visibility.Collapsed;
            CameraPiP.Visibility = Visibility.Visible;
        }
        else
        {
            CameraPlaceholder.Text = "摄像头不可用";
            CameraPiP.Visibility = Visibility.Visible;
        }
    }
    private void SendChat_Click(object sender, RoutedEventArgs e) { _vm.SendChat(ChatInput.Text); ChatInput.Text = ""; }
    private void ChatInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { SendChat_Click(sender, e); e.Handled = true; }
    }
}
