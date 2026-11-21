namespace P2PKiwi.WinUI3.Models;

public sealed record Participant(string Id, string Name, bool IsHost, bool CanControl);
public sealed record ChatMessage(string Sender, string Text, DateTime? At = null);
public sealed record RecentSession(string Title, string Subtitle);

public sealed record SessionOptions
{
    public required string SourceName { get; init; }
    public bool ShareSystemAudio { get; init; } = true;
    public bool AllowRemoteControl { get; init; }
}

public sealed record SessionInfo(string RoomId, string InviteLink, IReadOnlyList<Participant> Participants);
