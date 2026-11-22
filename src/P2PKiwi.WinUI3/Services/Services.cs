using P2PKiwi.WinUI3.Models;

namespace P2PKiwi.WinUI3.Services;

/// <summary>房间管理：创建 / 加入 / 离开。房间只在内存里，关闭即销毁，不存账号。</summary>
public interface ISessionManager
{
    SessionInfo? Current { get; }
    Task<SessionInfo> CreateSessionAsync(SessionOptions options);
    Task<SessionInfo> JoinSessionAsync(string inviteLinkOrCode);
    Task LeaveCurrentAsync();
}

public sealed class SessionManager : ISessionManager
{
    /// <summary>全应用共用一个房间管理：主页创建的房间，会话页看到的是同一个，否则成员表与聊天各自为空。</summary>
    public static SessionManager Shared { get; } = new();

    private SessionInfo? _current;
    public SessionInfo? Current => _current;

    public Task<SessionInfo> CreateSessionAsync(SessionOptions options)
    {
        // 房间 ID：短码即可；原版用邀请 URL 带 SDP 做带外交换。
        var roomId = Convert.ToHexString(Guid.NewGuid().ToByteArray())[..8].ToLowerInvariant();
        var name = AppSettings.Current.UserName;
        _current = new SessionInfo(roomId, $"https://p2p.kiwi/session/{roomId}",
            new[] { new Participant("me", $"{name}（主持人）", IsHost: true, CanControl: true) });
        // TODO(Windows)：这里接 IWebRtcService.CreatePeerConnection + 信令交换 SDP/ICE。
        return Task.FromResult(_current);
    }

    public Task<SessionInfo> JoinSessionAsync(string inviteLinkOrCode)
    {
        var roomId = inviteLinkOrCode.Trim().Split('/').Last();
        var name = AppSettings.Current.UserName;
        _current = new SessionInfo(roomId, inviteLinkOrCode,
            new[]
            {
                new Participant("host", "主持人", IsHost: true, CanControl: false),
                new Participant("me", name, IsHost: false, CanControl: false),
            });
        return Task.FromResult(_current);
    }

    public Task LeaveCurrentAsync()
    {
        _current = null;
        return Task.CompletedTask;
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

/// <summary>WebRTC 抽象：不绑定具体库，见 README 的二选一。</summary>
public interface IWebRtcService
{
    Task CreatePeerConnectionAsync(string roomId, IReadOnlyList<string> iceServers);
    Task AddScreenTrackAsync(IScreenCaptureService capture);
    Task SendCursorAsync(double x, double y);   // 相对坐标 0..1，不可靠 DataChannel
    Task SendChatAsync(string text);            // 可靠 DataChannel
    Task SendControlAsync(byte[] frame);        // 远程控制帧，需对方授权后才发
    event Action<string, string, double, double> RemoteCursorReceived;
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
