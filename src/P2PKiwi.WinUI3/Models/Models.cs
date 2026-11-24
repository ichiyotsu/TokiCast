using Windows.Graphics.Capture;

namespace P2PKiwi.WinUI3.Models;

public sealed record Participant(string Id, string Name, bool IsHost) : System.ComponentModel.INotifyPropertyChanged
{
    public string RoleText => IsHost ? "主持人" : "成员";

    private bool _isSpeaking;
    /// <summary>说话指示：UI 上的绿点绑定这个（RMS 阈值 + 保持逻辑在 ViewModel）。</summary>
    public bool IsSpeaking
    {
        get => _isSpeaking;
        set
        {
            if (_isSpeaking == value) return;
            _isSpeaking = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSpeaking)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed record ChatMessage(string Sender, string Text, DateTime? At = null)
{
    public string TimeText => At?.ToString("HH:mm") ?? string.Empty;
}
public sealed record RecentSession(string Title, string Subtitle, string InviteLink = "");

public sealed record SessionOptions
{
    public bool ShareSystemAudio { get; init; } = true;
    public bool AllowRemoteControl { get; init; }
    /// <summary>用户在系统选择器里挑的显示器/窗口（取消则为 null，开始分享时不建会话）。</summary>
    public GraphicsCaptureItem? CaptureItem { get; init; }
}

public sealed record SessionInfo(string RoomId, string InviteLink, IReadOnlyList<Participant> Participants, bool IsHost = false, string? AnswerLink = null);
