using P2PKiwi.WinUI3.Models;

namespace P2PKiwi.WinUI3.Services;

/// <summary>房间管理：创建 / 加入 / 允许加入 / 离开。房间只在内存里，关闭即销毁，不存账号。</summary>
public interface ISessionManager
{
    SessionInfo? Current { get; }
    IWebRtcService? Rtc { get; }
    Task<SessionInfo> CreateSessionAsync(SessionOptions options);
    Task<SessionInfo> JoinSessionAsync(string inviteLinkOrCode);
    Task CompleteSessionAsync(string answerLink);
    Task LeaveCurrentAsync();
}

public sealed class SessionManager : ISessionManager
{
    /// <summary>全应用共用一个房间管理：主页创建的房间，会话页看到的是同一个，否则成员表与聊天各自为空。</summary>
    public static SessionManager Shared { get; } = new();

    private SessionInfo? _current;
    public SessionInfo? Current => _current;
    public IWebRtcService? Rtc { get; private set; }

    public async Task<SessionInfo> CreateSessionAsync(SessionOptions options)
    {
        await CloseCurrentTransportAsync();
        // 房间 ID：短码即可；真正握手靠 SDP，不靠 roomId。
        var roomId = Convert.ToHexString(Guid.NewGuid().ToByteArray())[..8].ToLowerInvariant();
        var name = AppSettings.Current.UserName;
        // 建 offer（等 ICE 收集完成，约 1~5 秒）再生成邀请链接；失败直接抛错，不造假会话。
        var rtc = new SIPSorceryWebRtcService();
        var link = await rtc.CreateOfferAsync(roomId, options.CaptureItem);
        Rtc = rtc;
        _current = new SessionInfo(roomId, link,
            new[] { new Participant("me", $"{name}（主持人）", IsHost: true, CanControl: true) }, IsHost: true);
        return _current;
    }

    public async Task<SessionInfo> JoinSessionAsync(string inviteLinkOrCode)
    {
        await CloseCurrentTransportAsync();
        var name = AppSettings.Current.UserName;
        // 用邀请链接里的 offer 建 answer；只有 8 位码时 SdpLink.Parse 会抛错（需要完整链接）。
        var rtc = new SIPSorceryWebRtcService();
        var answerLink = await rtc.CreateAnswerAsync(inviteLinkOrCode.Trim());
        var (roomId, _, _) = SdpLink.Parse(inviteLinkOrCode.Trim());
        Rtc = rtc;
        _current = new SessionInfo(roomId, inviteLinkOrCode,
            new[]
            {
                new Participant("host", "主持人", IsHost: true, CanControl: false),
                new Participant("me", name, IsHost: false, CanControl: false),
            }, IsHost: false, AnswerLink: answerLink);
        return _current;
    }

    /// <summary>主持方：粘贴加入方发回的 answer 链接，完成连接。</summary>
    public async Task CompleteSessionAsync(string answerLink)
    {
        if (Rtc == null || _current == null)
            throw new InvalidOperationException("没有正在等待的分享，先点「开始分享」。");
        await Rtc.SetRemoteAnswerAsync(answerLink);
    }

    public async Task LeaveCurrentAsync()
    {
        await CloseCurrentTransportAsync();
        _current = null;
    }

    private async Task CloseCurrentTransportAsync()
    {
        if (Rtc != null)
        {
            try { await Rtc.CloseAsync(); } catch { }
            Rtc = null;
        }
    }
}

/// <summary>画面采集（Windows.Graphics.Capture）。接线见 README。</summary>
public interface IScreenCaptureService
{
    Task StartAsync(object item, Action<object> onFrame);
    Task StopAsync();
}

public sealed class ScreenCaptureService : IScreenCaptureService
{
    // TODO(Windows)：GraphicsCapturePicker.PickSingleItemAsync()
    //  + GraphicsCaptureSession + Direct3D11CaptureFramePool，
    // 帧经 IDirect3DDevice 送给 WebRTC 视频源；不要用 GDI BitBlt 截屏。
    public Task StartAsync(object item, Action<object> onFrame) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
}

/// <summary>
/// WebRTC 传输：SIPSorcery 实现（见 WebRtcService.cs）。1 对 1；offer/answer 走邀请链接交换，无中心信令。
/// 媒体只有 DTLS-SRTP 传输加密——SIPSorcery 没有 MLS/SFrame，E2EE 开关只存值、不接线、不亮徽标。
/// </summary>
public interface IWebRtcService
{
    bool IsConnected { get; }
    bool IsMicMuted { get; }
    bool IsRecording { get; }
    /// <summary>主持方：建 offer，返回含 offer 的邀请链接。</summary>
    Task<string> CreateOfferAsync(string roomId, Windows.Graphics.Capture.GraphicsCaptureItem? captureItem);
    /// <summary>加入方：用邀请链接建 answer，返回发回主持人的 answer 链接。</summary>
    Task<string> CreateAnswerAsync(string offerLink);
    /// <summary>主持方：粘贴 answer 链接，完成连接。</summary>
    Task SetRemoteAnswerAsync(string answerLink);
    Task SendChatAsync(string text);            // 可靠 DataChannel
    Task SendCursorAsync(double x, double y);   // 不可靠 DataChannel，相对坐标 0..1
    Task SendControlAsync(byte[] frame);        // 远程控制帧，需对方授权后才发（Phase 1 未接线）
    Task SetMicMutedAsync(bool muted);
    Task StartRecordingAsync(string filePath);
    Task StopRecordingAsync();
    Task CloseAsync();
    event Action<ChatMessage>? ChatMessageReceived;
    event Action<string, string, double, double>? RemoteCursorReceived;
    /// <summary>远端视频解码帧：BGRA8 字节 + 宽 + 高（供 WriteableBitmap 显示）。</summary>
    event Action<byte[], int, int>? RemoteVideoFrameReceived;
    /// <summary>说话电平 RMS 0..1（本机：解码后的编码帧；远端：收到的编码帧）。</summary>
    event Action<double>? LocalSpeakingLevelChanged;
    event Action<double>? RemoteSpeakingLevelChanged;
    event Action<PeerConnectionState>? ConnectionStateChanged;
    event Action<string>? ErrorOccurred;
}

/// <summary>信令：只交换 SDP offer/answer 与 ICE candidate，不传聊天内容与媒体密钥。</summary>
public interface ISignalingService
{
    Task<string> CreateOfferAsync(string roomId);
    Task SendAnswerAsync(string roomId, string sdp);
}

/// <summary>远程输入：user32 SendInput。必须先有对方授权，且发送方在成员表里。</summary>
public interface IRemoteInputService
{
    void MoveMouse(double x, double y); // 相对坐标 → 绝对坐标
    void Click(bool right = false);
    void Scroll(int delta);
    void KeyPress(ushort virtualKey);
}

public sealed class RemoteInputService : IRemoteInputService
{
    // TODO(Windows)：P/Invoke user32!SendInput，INPUT_MOUSE / INPUT_KEYBOARD。
    // 调用前必须由 SessionViewModel 校验：sender 在成员表里 && AllowRemoteControl。
    public void MoveMouse(double x, double y) { }
    public void Click(bool right = false) { }
    public void Scroll(int delta) { }
    public void KeyPress(ushort virtualKey) { }
}

/// <summary>
/// E2EE 骨架：MLS 管成员密钥，SFrame 加密每一帧。
/// 强制规则：MLS 密钥未建立前，UI 不许显示 E2EE 徽标；DTLS-SRTP 不等于 E2EE。
/// 真实现需接原生 MLS（RFC 9420，如 mls-rs / OpenMLS）与 SFrame 库，
/// 不要自己写密钥协商。
/// </summary>
public sealed class E2eeService
{
    public bool IsEstablished { get; private set; }
    public byte[] DeriveFrameKey(byte[] mlsSecret, ulong frameCounter) => throw new NotImplementedException("接 MLS/SFrame 原生库");
    public byte[] EncryptFrame(byte[] frame, ulong counter) => throw new NotImplementedException("接 SFrame");
    public void RotateKeys() => throw new NotImplementedException("成员变化时 MLS 轮换密钥");
}
