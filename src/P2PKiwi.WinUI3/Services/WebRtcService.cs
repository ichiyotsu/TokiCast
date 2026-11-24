using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FFmpeg.AutoGen;
using Microsoft.Graphics.Canvas;
using NAudio.Wave;
using P2PKiwi.WinUI3.Models;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;
using SIPSorceryMedia.Windows;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using WinRT.Interop;

namespace P2PKiwi.WinUI3.Services;

/// <summary>点对点连接状态（SIPSorcery 状态的简化映射，UI 只看这个）。</summary>
public enum PeerConnectionState
{
    New,
    Connecting,
    Connected,
    Disconnected,
    Failed,
    Closed,
}

/// <summary>
/// 邀请链接 SDP 交换（无中心信令）：offer/answer 经 deflate 压缩 + base64url
/// 放进链接 fragment（#o= / #a=），fragment 不会发到服务器。
/// Phase 0 实测：offer 约 1 KB，压缩后 payload 约 650 字符。
/// </summary>
public static class SdpLink
{
    public static string Encode(string sdp)
    {
        var bytes = Encoding.UTF8.GetBytes(sdp);
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            ds.Write(bytes, 0, bytes.Length);
        return Convert.ToBase64String(ms.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string Decode(string payload)
    {
        var b64 = payload.Replace('-', '+').Replace('_', '/');
        switch (b64.Length % 4)
        {
            case 2: b64 += "=="; break;
            case 3: b64 += "="; break;
        }
        var bytes = Convert.FromBase64String(b64);
        using var ms = new MemoryStream(bytes);
        using var ds = new DeflateStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        ds.CopyTo(outMs);
        return Encoding.UTF8.GetString(outMs.ToArray());
    }

    public static string BuildOfferLink(string roomId, string sdp) => $"https://p2p.kiwi/session/{roomId}#o={Encode(sdp)}";
    public static string BuildAnswerLink(string roomId, string sdp) => $"https://p2p.kiwi/session/{roomId}#a={Encode(sdp)}";

    /// <summary>解析邀请链接，返回 (房间码, o=offer / a=answer, SDP 明文)。只有 8 位码、没有 fragment 时抛错。</summary>
    public static (string RoomId, string Kind, string Sdp) Parse(string link)
    {
        var trimmed = link.Trim();
        var hash = trimmed.IndexOf('#');
        if (hash < 0)
            throw new InvalidOperationException("这条链接里没有连接信息（只有房间码）。请贴主持人发的完整邀请链接。");
        var fragment = trimmed[(hash + 1)..];
        var eq = fragment.IndexOf('=');
        if (eq < 0)
            throw new InvalidOperationException("这条链接格式不对，请重新复制主持人发的完整邀请链接。");
        var kind = fragment[..eq];
        if (kind != "o" && kind != "a")
            throw new InvalidOperationException("这条链接格式不对（应为 #o= 或 #a= 开头）。");
        string roomId = "";
        var sessionIdx = trimmed.IndexOf("session/", StringComparison.OrdinalIgnoreCase);
        if (sessionIdx >= 0)
        {
            var rest = trimmed[(sessionIdx + "session/".Length)..];
            var end = rest.IndexOfAny(new[] { '#', '/', '?', ' ' });
            roomId = end >= 0 ? rest[..end] : rest;
        }
        return (roomId, kind, Decode(fragment[(eq + 1)..]));
    }
}

/// <summary>
/// SIPSorcery 真 P2P 实现（1 对 1；架构按 mesh 留了 peer 映射的口子，0.1.3 先跑 1 对 1）。
/// 音频 Opus 48kHz（WindowsAudioEndPoint + NAudio）；视频 H.264（FFmpeg 编解码，
/// 屏幕源见 ScreenCaptureVideoSource）；聊天/光标走 RTCDataChannel；录制为编码帧直写 .h264。
/// 注意：SIPSorcery 没有 MLS/SFrame——媒体只有 DTLS-SRTP 传输加密，E2EE 开关只存值、不接线、不亮徽标。
/// </summary>
public sealed class SIPSorceryWebRtcService : IWebRtcService
{
    private static readonly AudioFormat OpusFormat = new(AudioCodecsEnum.OPUS, 111, 48000, 2, "minptime=10;useinbandfec=1");
    private static readonly VideoFormat H264Format = new(VideoCodecsEnum.H264, 100, 90000, "packetization-mode=1;profile-level-id=42e01f;level-asymmetry-allowed=1");
    // 线上消息用 camelCase（{"kind":"chat",...}），与接收端解析一致。
    private static readonly JsonSerializerOptions CamelCaseJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly AudioEncoder _audioEncoder = new();
    private readonly List<RTCIceServer> _iceServers;
    private RTCPeerConnection? _pc;
    private WindowsAudioEndPoint? _audio;
    private FFmpegVideoEndPoint? _videoSink;
    private ScreenCaptureVideoSource? _screenSource;
    private RTCDataChannel? _chatChannel;
    private RTCDataChannel? _cursorChannel;
    private FileStream? _recordingStream;
    private readonly object _recordingLock = new();
    private string? _roomId;
    private bool _micMuted;
    private bool _closed;

    public bool IsConnected { get; private set; }
    public bool IsMicMuted => _micMuted;
    public bool IsRecording => _recordingStream != null;

    public event Action<ChatMessage>? ChatMessageReceived;
    public event Action<string, string, double, double>? RemoteCursorReceived;
    public event Action<byte[], int, int>? RemoteVideoFrameReceived;
    public event Action<double>? LocalSpeakingLevelChanged;
    public event Action<double>? RemoteSpeakingLevelChanged;
    public event Action<PeerConnectionState>? ConnectionStateChanged;
    public event Action<string>? ErrorOccurred;

    public SIPSorceryWebRtcService()
    {
        _iceServers = IceServersFromSettings();
    }

    /// <summary>主持方：建连接 + offer，返回含 offer 的邀请链接（已自动等 ICE 收集完成，非 trickle）。</summary>
    public async Task<string> CreateOfferAsync(string roomId, GraphicsCaptureItem? captureItem)
    {
        _roomId = roomId;
        CreatePeerConnection(isHost: true);
        if (captureItem != null)
        {
            try
            {
                await StartScreenShareAsync(captureItem);
            }
            catch (Exception ex)
            {
                // 屏幕分享起不来（常见原因是 FFmpeg DLL 没带齐）不阻塞建连：语音与聊天仍可用。
                ErrorOccurred?.Invoke($"屏幕分享启动失败：{ex.Message}。语音与聊天仍可用。");
            }
        }
        var offer = _pc!.createOffer(null);
        await _pc.setLocalDescription(offer);
        await WaitForIceGatheringAsync();
        var sdp = _pc.localDescription.sdp.ToString();
        return SdpLink.BuildOfferLink(roomId, sdp);
    }

    /// <summary>加入方：用邀请链接里的 offer 建 answer，返回（房间码，含 answer 的回传链接，发回主持人）。</summary>
    public async Task<(string RoomId, string AnswerLink)> CreateAnswerAsync(string offerLink)
    {
        var (roomId, kind, sdp) = SdpLink.Parse(offerLink);
        if (kind != "o")
            throw new InvalidOperationException("这不是一条邀请链接（缺少 #o= 的 offer 部分）。");
        _roomId = roomId;
        CreatePeerConnection(isHost: false);
        var result = _pc!.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = sdp });
        if (result != SetDescriptionResultEnum.OK)
            throw new InvalidOperationException($"对方的邀请链接无法使用（{result}），请让主持人重新发一条。");
        var answer = _pc.createAnswer(null);
        await _pc.setLocalDescription(answer);
        await WaitForIceGatheringAsync();
        var answerSdp = _pc.localDescription.sdp.ToString();
        return (roomId, SdpLink.BuildAnswerLink(roomId, answerSdp));
    }

    /// <summary>主持方：粘贴加入方发回的 answer 链接，完成连接。</summary>
    public Task SetRemoteAnswerAsync(string answerLink)
    {
        if (_pc == null)
            throw new InvalidOperationException("没有正在等待的分享，先点「开始分享」。");
        var (_, kind, sdp) = SdpLink.Parse(answerLink);
        if (kind != "a")
            throw new InvalidOperationException("这不是一条 answer 链接（缺少 #a= 的部分）。");
        var result = _pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp });
        if (result != SetDescriptionResultEnum.OK)
            throw new InvalidOperationException($"这条 answer 链接无法使用（{result}），请让对方重新加入生成一条新的。");
        return Task.CompletedTask;
    }

    public Task SendChatAsync(string text)
    {
        if (_chatChannel?.readyState == RTCDataChannelState.open)
        {
            var json = JsonSerializer.Serialize(new WireChatMessage("chat", AppSettings.Current.UserName, text, DateTime.Now), CamelCaseJson);
            _chatChannel.send(json);
        }
        return Task.CompletedTask;
    }

    public Task SendCursorAsync(double x, double y)
    {
        if (_cursorChannel?.readyState == RTCDataChannelState.open)
        {
            var json = JsonSerializer.Serialize(new WireCursorMessage("cursor", AppSettings.Current.UserName, x, y), CamelCaseJson);
            _cursorChannel.send(json);
        }
        return Task.CompletedTask;
    }

    public Task SendControlAsync(byte[] frame)
    {
        // Phase 1 未做：远程控制要先走对方点「允许」的授权流程，现在只留接口，调用方不要发。
        return Task.CompletedTask;
    }

    public async Task SetMicMutedAsync(bool muted)
    {
        _micMuted = muted;
        if (_audio == null) return;
        if (muted) await _audio.PauseAudio();
        else await _audio.ResumeAudio();
    }

    /// <summary>录制远端屏幕：编码帧直写 .h264 Annex B 基础流（VLC 可播），不重编码。</summary>
    public Task StartRecordingAsync(string filePath)
    {
        lock (_recordingLock)
        {
            if (_recordingStream != null) return Task.CompletedTask;
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            _recordingStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        }
        return Task.CompletedTask;
    }

    public Task StopRecordingAsync()
    {
        lock (_recordingLock)
        {
            _recordingStream?.Flush();
            _recordingStream?.Dispose();
            _recordingStream = null;
        }
        return Task.CompletedTask;
    }

    public async Task CloseAsync()
    {
        if (_closed) return;
        _closed = true;
        try { await StopRecordingAsync(); } catch { }
        try { if (_screenSource != null) await _screenSource.CloseVideo(); } catch { }
        _screenSource = null;
        try
        {
            if (_audio != null)
            {
                await _audio.CloseAudio();
                await _audio.CloseAudioSink();
            }
        }
        catch { }
        try
        {
            if (_videoSink != null)
            {
                await _videoSink.CloseVideoSink();
                _videoSink.Dispose();
            }
        }
        catch { }
        try { _pc?.close(); } catch { }
        _pc = null;
        _chatChannel = null;
        _cursorChannel = null;
        // 音频端点若实现 IDisposable，句柄随连接一起释放（WindowsAudioEndPoint 8.0.14 未在文档里写明是否需要，as 转换失败时为 null，不影响）。
        try { (_audio as IDisposable)?.Dispose(); } catch { }
        _audio = null;
        try { _audioEncoder.Dispose(); } catch { }
        IsConnected = false;
    }

    // ---- 连接搭建 ----

    private void CreatePeerConnection(bool isHost)
    {
        _pc = new RTCPeerConnection(new RTCConfiguration { iceServers = _iceServers });

        _pc.onconnectionstatechange += state =>
        {
            var mapped = state switch
            {
                RTCPeerConnectionState.connected => PeerConnectionState.Connected,
                RTCPeerConnectionState.connecting => PeerConnectionState.Connecting,
                RTCPeerConnectionState.disconnected => PeerConnectionState.Disconnected,
                RTCPeerConnectionState.failed => PeerConnectionState.Failed,
                RTCPeerConnectionState.closed => PeerConnectionState.Closed,
                _ => PeerConnectionState.New,
            };
            IsConnected = state == RTCPeerConnectionState.connected;
            ConnectionStateChanged?.Invoke(mapped);
            if (state == RTCPeerConnectionState.failed)
                ErrorOccurred?.Invoke("点对点连接失败：双方网络可能都打不通（对称 NAT 需要 TURN，见设置里的 STUN/TURN 说明）。");
        };
        _pc.ondatachannel += channel =>
        {
            if (channel.label == "chat") { _chatChannel = channel; WireChatChannel(channel); }
            else if (channel.label == "cursor") { _cursorChannel = channel; WireCursorChannel(channel); }
        };
        _pc.OnVideoFormatsNegotiated += formats =>
        {
            if (formats.Count > 0) _screenSource?.SetVideoSourceFormat(formats[0]);
        };

        // 音轨双向；视频轨：主持方只发，加入方只收。
        _pc.addTrack(new MediaStreamTrack(new List<AudioFormat> { OpusFormat }, MediaStreamStatusEnum.SendRecv));
        _pc.addTrack(new MediaStreamTrack(new List<VideoFormat> { H264Format },
            isHost ? MediaStreamStatusEnum.SendOnly : MediaStreamStatusEnum.RecvOnly));

        SetupAudio();
        SetupVideoReceive();

        if (isHost)
        {
            // 主持方先建 channel（SDP 里要有 m=application 段）；加入方从 ondatachannel 收。
            _ = CreateChannelsAsync();
        }
    }

    private async Task CreateChannelsAsync()
    {
        try
        {
            _chatChannel = await _pc!.createDataChannel("chat", new RTCDataChannelInit { ordered = true });
            WireChatChannel(_chatChannel);
            _cursorChannel = await _pc.createDataChannel("cursor", new RTCDataChannelInit { ordered = false, maxRetransmits = 0 });
            WireCursorChannel(_cursorChannel);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"DataChannel 创建失败：{ex.Message}");
        }
    }

    private void SetupAudio()
    {
        var settings = AppSettings.Current;
        // 扬声器用系统默认（-1）：设置页只存了一个麦克风名，不按它找输出设备。
        var outIndex = -1;
        var inIndex = FindInputDeviceIndex(settings.MicrophoneDevice);
        _audio = new WindowsAudioEndPoint(_audioEncoder, outIndex, inIndex, disableSource: false, disableSink: false);
        _audio.OnAudioSourceError += msg => ErrorOccurred?.Invoke($"麦克风：{msg}");
        _audio.OnAudioSinkError += msg => ErrorOccurred?.Invoke($"扬声器：{msg}");
        _audio.SetAudioSourceFormat(OpusFormat);
        _audio.SetAudioSinkFormat(OpusFormat);

        // 发：编码帧 -> 对端。注意：OnAudioSourceEncodedSample 传的 duration 是编码字节数，
        // 不是 RTP 时间戳单位——这里用 OnAudioSourceEncodedFrameReady 的 DurationMilliSeconds 换算（48kHz 下 20ms=960）。
        _audio.OnAudioSourceEncodedFrameReady += frame =>
        {
            if (_pc == null || _closed) return;
            _pc.SendAudio((uint)(frame.DurationMilliSeconds * 48), frame.EncodedAudio);
            var pcm = _audioEncoder.DecodeAudio(frame.EncodedAudio, frame.AudioFormat);
            LocalSpeakingLevelChanged?.Invoke(Rms16(pcm));
        };

        // 收：编码帧直进扬声器 sink（Phase 0 验证过的路径 A），同时解码算远端电平。
        _pc!.OnAudioFrameReceived += frame =>
        {
            _audio?.GotEncodedMediaFrame(frame);
            var pcm = _audioEncoder.DecodeAudio(frame.EncodedAudio, frame.AudioFormat);
            RemoteSpeakingLevelChanged?.Invoke(Rms16(pcm));
        };

        // 启动采集 + 播放；连接时麦克风是否打开按设置（默认开）。
        _ = StartAudioAsync();
    }

    private async Task StartAudioAsync()
    {
        if (_audio == null) return;
        try
        {
            await _audio.StartAudio();
            await _audio.StartAudioSink();
            _micMuted = !AppSettings.Current.MicOnConnect;
            if (_micMuted) await _audio.PauseAudio();
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"音频设备启动失败：{ex.Message}");
        }
    }

    private void SetupVideoReceive()
    {
        _videoSink = new FFmpegVideoEndPoint(new Dictionary<string, string>());
        _pc!.OnVideoFrameReceived += (remote, timestamp, payload, format) =>
        {
            _videoSink?.GotVideoFrame(remote, timestamp, payload, format);
            if (format.Codec == VideoCodecsEnum.H264)
            {
                lock (_recordingLock)
                {
                    // 编码帧已是 Annex B 访问单元，直接追加写文件。
                    _recordingStream?.Write(payload, 0, payload.Length);
                }
            }
        };
        // 8.0.23 只触发 Faster 事件（RawImage），不触发 byte[] 那个——用 Faster + GetBuffer()。
        _videoSink.OnVideoSinkDecodedSampleFaster += raw =>
        {
            var buffer = raw.GetBuffer();
            if (buffer == null || buffer.Length == 0) return;
            var bgra = ConvertToBgra(buffer, raw.Width, raw.Height, raw.Stride, raw.PixelFormat);
            if (bgra != null) RemoteVideoFrameReceived?.Invoke(bgra, raw.Width, raw.Height);
        };
        _ = StartVideoSinkAsync();
    }

    private async Task StartVideoSinkAsync()
    {
        try
        {
            if (_videoSink != null) await _videoSink.StartVideoSink();
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"视频解码器启动失败：{ex.Message}");
        }
    }

    private async Task StartScreenShareAsync(GraphicsCaptureItem item)
    {
        var encoder = CreateVideoEncoder();
        _screenSource = new ScreenCaptureVideoSource(encoder, item);
        _screenSource.OnVideoSourceEncodedSample += _pc!.SendVideo;
        await _screenSource.StartVideo();
    }

    private static FFmpegVideoEncoder CreateVideoEncoder()
    {
        EnsureFFmpegRootPath();
        var encoder = new FFmpegVideoEncoder(new Dictionary<string, string>(), AVHWDeviceType.AV_HWDEVICE_TYPE_NONE);
        // 码率按设置走（Mbps → bps；SetBitrate 只存字段并复位编码器，真正起效在首次编码初始化时）。
        encoder.SetBitrate(Math.Clamp(AppSettings.Current.BitrateMbps, 1, 50) * 1_000_000, null, null, null);
        return encoder;
    }

    /// <summary>FFmpeg 7.x 共享 DLL 随安装包放在 exe 旁边时，显式指给 FFmpeg.AutoGen；否则走默认搜索。</summary>
    private static void EnsureFFmpegRootPath()
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            if (File.Exists(Path.Combine(dir, "avcodec-61.dll")))
                ffmpeg.RootPath = dir;
        }
        catch { }
    }

    private async Task WaitForIceGatheringAsync()
    {
        var pc = _pc!;
        if (pc.iceGatheringState == RTCIceGatheringState.complete) return;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(RTCIceGatheringState state)
        {
            if (state == RTCIceGatheringState.complete) tcs.TrySetResult(true);
        }
        pc.onicegatheringstatechange += Handler;
        try
        {
            // 超时兜底 5 秒：只用已收集到的候选；局域网/同 NAT 下 host 候选一般 1 秒内就够。
            await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            pc.onicegatheringstatechange -= Handler;
        }
    }

    private void WireChatChannel(RTCDataChannel channel)
    {
        channel.onmessage += (_, protocol, data) =>
        {
            if (protocol != DataChannelPayloadProtocols.WebRTC_String) return;
            try
            {
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(data));
                var root = doc.RootElement;
                if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != "chat") return;
                var sender = root.TryGetProperty("sender", out var s) ? s.GetString() ?? "对方" : "对方";
                var text = root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                DateTime at = DateTime.Now;
                if (root.TryGetProperty("at", out var a) && DateTime.TryParse(a.GetString(), out var parsed)) at = parsed;
                ChatMessageReceived?.Invoke(new ChatMessage(sender, text, at));
            }
            catch
            {
                // 不是聊天 JSON，忽略。
            }
        };
    }

    private void WireCursorChannel(RTCDataChannel channel)
    {
        channel.onmessage += (_, protocol, data) =>
        {
            if (protocol != DataChannelPayloadProtocols.WebRTC_String) return;
            try
            {
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(data));
                var root = doc.RootElement;
                var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "对方" : "对方";
                var x = root.TryGetProperty("x", out var xv) ? xv.GetDouble() : 0;
                var y = root.TryGetProperty("y", out var yv) ? yv.GetDouble() : 0;
                RemoteCursorReceived?.Invoke("peer", name, x, y);
            }
            catch { }
        };
    }

    private static double Rms16(short[] pcm)
    {
        if (pcm.Length == 0) return 0;
        double sum = 0;
        foreach (var s in pcm)
        {
            double v = s / 32768.0;
            sum += v * v;
        }
        return Math.Sqrt(sum / pcm.Length);
    }

    /// <summary>解码后的 RGB24/BGR24 转 WriteableBitmap 要的 BGRA8（按真实 stride 逐行转）。</summary>
    private static byte[]? ConvertToBgra(byte[] buffer, int width, int height, int stride, VideoPixelFormatsEnum format)
    {
        if (format != VideoPixelFormatsEnum.Rgb && format != VideoPixelFormatsEnum.Bgr) return null;
        if (width <= 0 || height <= 0 || stride < width * 3) return null;
        var bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int srcRow = y * stride;
            int dstRow = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int s = srcRow + x * 3;
                int d = dstRow + x * 4;
                if (s + 2 >= buffer.Length) return null;
                if (format == VideoPixelFormatsEnum.Rgb)
                {
                    bgra[d] = buffer[s + 2];
                    bgra[d + 1] = buffer[s + 1];
                    bgra[d + 2] = buffer[s];
                }
                else
                {
                    bgra[d] = buffer[s];
                    bgra[d + 1] = buffer[s + 1];
                    bgra[d + 2] = buffer[s + 2];
                }
                bgra[d + 3] = 255;
            }
        }
        return bgra;
    }

    private static int FindInputDeviceIndex(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "系统默认") return -1;
        try
        {
            for (int i = 0; i < WaveIn.DeviceCount; i++)
                if (WaveIn.GetCapabilities(i).ProductName.Contains(name, StringComparison.OrdinalIgnoreCase)) return i;
        }
        catch { }
        return -1;
    }

    /// <summary>设置里的 STUN/TURN 转 ICE 服务器。先认裸 URL（stun:/turn:/turns: 开头），再试 JSON；都失败回退默认 STUN。</summary>
    private static List<RTCIceServer> IceServersFromSettings()
    {
        var servers = ParseIceServers(AppSettings.Current.StunTurnJson);
        if (servers.Count == 0)
            servers.Add(new RTCIceServer { urls = "stun:stun.l.google.com:19302" });
        return servers;
    }

    /// <summary>解析 STUN/TURN 配置：裸 URL 每行一个，或 JSON 对象/数组；解析不出任何服务器时返回空列表。</summary>
    public static List<RTCIceServer> ParseIceServers(string? json)
    {
        var servers = new List<RTCIceServer>();
        if (string.IsNullOrWhiteSpace(json)) return servers;
        var lines = json.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (IsBareIceUrl(trimmed))
            {
                servers.Add(new RTCIceServer { urls = trimmed });
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                AddIceServers(doc.RootElement, servers);
            }
            catch
            {
                // 单行不是 JSON：整段可能是多行 JSON 对象/数组，下面整段再试一次。
            }
        }
        if (servers.Count == 0 && !IsBareIceUrl(json.Trim()))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                AddIceServers(doc.RootElement, servers);
            }
            catch { }
        }
        return servers;
    }

    private static bool IsBareIceUrl(string text)
        => text.StartsWith("stun:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("turn:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("turns:", StringComparison.OrdinalIgnoreCase);

    /// <summary>设置页用：输入的 STUN/TURN 解析不出任何服务器时返回 true（会用默认 STUN），供设置页提示而非静默回退。</summary>
    public static bool IceServersNeedFallback(string? json)
        => !string.IsNullOrWhiteSpace(json) && ParseIceServers(json).Count == 0;

    private static void AddIceServers(JsonElement element, List<RTCIceServer> servers)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) AddIceServers(item, servers);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;
        if (!element.TryGetProperty("urls", out var urlsEl)) return;
        var username = element.TryGetProperty("username", out var u) ? u.GetString() : null;
        var credential = element.TryGetProperty("credential", out var c) ? c.GetString() : null;
        if (urlsEl.ValueKind == JsonValueKind.String)
        {
            var url = urlsEl.GetString();
            if (!string.IsNullOrWhiteSpace(url))
                servers.Add(new RTCIceServer { urls = url, username = username, credential = credential });
        }
        else if (urlsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var urlEl in urlsEl.EnumerateArray())
            {
                var url = urlEl.GetString();
                if (!string.IsNullOrWhiteSpace(url))
                    servers.Add(new RTCIceServer { urls = url, username = username, credential = credential });
            }
        }
    }

    private sealed record WireChatMessage(string Kind, string Sender, string Text, DateTime At);
    private sealed record WireCursorMessage(string Kind, string Name, double X, double Y);
}

/// <summary>
/// 屏幕采集源：Windows.Graphics.Capture 包装成 IVideoSource。
/// D3D 设备用 Win2D 的 CanvasDevice（共享设备，WinUI 3 采集的标准做法，不用自己 P/Invoke 建 D3D 设备——
/// WinRT 里没有公开的 Direct3D11Helper.CreateDirect3DDevice，不要自己造）。
/// 帧经 CanvasBitmap 拷到 CPU（BGRA8 紧排）后由 FFmpeg 编 H.264；节流间隔与编码分辨率按设置走
/// （设置 → 帧率、分辨率；改完设置下次开始分享时生效）。
/// </summary>
public sealed class ScreenCaptureVideoSource : IVideoSource
{
    private readonly IVideoEncoder _encoder;
    private readonly GraphicsCaptureItem _item;
    private CanvasDevice? _device;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private bool _running;
    private DateTime _lastFrameTime = DateTime.MinValue;
    private DateTime _lastErrorTime = DateTime.MinValue;
    // 帧率按设置走（默认 30fps → 约 33ms；节流间隔 = 1000 / 帧率）。
    private static int FrameIntervalMs => 1000 / Math.Clamp(AppSettings.Current.FrameRate, 1, 60);
    // 编码目标分辨率（开始分享时按设置解析一次；解析失败用采集原尺寸）。
    private int _targetWidth;
    private int _targetHeight;
    private VideoFormat _format = new(VideoCodecsEnum.H264, 100, 90000, "packetization-mode=1;profile-level-id=42e01f;level-asymmetry-allowed=1");

    public ScreenCaptureVideoSource(IVideoEncoder encoder, GraphicsCaptureItem item)
    {
        _encoder = encoder;
        _item = item;
    }

    public event EncodedSampleDelegate? OnVideoSourceEncodedSample;
    public event RawVideoSampleDelegate? OnVideoSourceRawSample;
    public event RawVideoSampleFasterDelegate? OnVideoSourceRawSampleFaster;
    public event SourceErrorDelegate? OnVideoSourceError;

    public List<VideoFormat> GetVideoSourceFormats() => new() { _format };
    public void SetVideoSourceFormat(VideoFormat videoFormat) => _format = videoFormat;
    public void RestrictFormats(Func<VideoFormat, bool> filter) { }
    public Task PauseVideo() { _running = false; return Task.CompletedTask; }
    public Task ResumeVideo() { _running = true; return Task.CompletedTask; }

    public Task StartVideo()
    {
        if (_running) return Task.CompletedTask;
        // 共享设备：不要 Dispose（别处也在用）。
        _device = CanvasDevice.GetSharedDevice();
        var size = _item.Size;
        (_targetWidth, _targetHeight) = TargetResolution(size.Width, size.Height);
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
        _framePool.FrameArrived += OnFrameArrived;
        _session = _framePool.CreateCaptureSession(_item);
        _session.StartCapture();
        _running = true;
        return Task.CompletedTask;
    }

    /// <summary>设置里的分辨率（如 "1920 × 1080"）转编码尺寸；解析失败用采集原尺寸。</summary>
    private static (int Width, int Height) TargetResolution(int captureWidth, int captureHeight)
    {
        var parts = AppSettings.Current.Resolution.Split(new[] { '×', 'x', 'X' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2
            && int.TryParse(parts[0].Trim(), out var w) && int.TryParse(parts[1].Trim(), out var h)
            && w > 0 && h > 0 && w <= 7680 && h <= 4320)
            return (w, h);
        return (captureWidth, captureHeight);
    }

    public Task CloseVideo()
    {
        _running = false;
        try { _session?.Dispose(); } catch { }
        try { if (_framePool != null) { _framePool.FrameArrived -= OnFrameArrived; _framePool.Dispose(); } } catch { }
        _session = null;
        _framePool = null;
        _device = null;
        try { (_encoder as IDisposable)?.Dispose(); } catch { }
        return Task.CompletedTask;
    }

    public void ExternalVideoSourceRawSample(uint durationMilliseconds, int width, int height, byte[] sample, VideoPixelFormatsEnum pixelFormat) { }
    public void ExternalVideoSourceRawSampleFaster(uint durationMilliseconds, RawImage rawImage) { }
    public void ForceKeyFrame() => _encoder.ForceKeyFrame();
    public bool HasEncodedVideoSubscribers() => OnVideoSourceEncodedSample != null;
    public bool IsVideoSourcePaused() => !_running;

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (!_running) return;
        var now = DateTime.UtcNow;
        if ((now - _lastFrameTime).TotalMilliseconds < FrameIntervalMs) return;
        _lastFrameTime = now;
        Direct3D11CaptureFrame? frame = null;
        try
        {
            frame = sender.TryGetNextFrame();
            var device = _device;
            if (frame == null || device == null) return;
            var size = frame.ContentSize;
            if (size.Width <= 0 || size.Height <= 0) return;
            using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(device, frame.Surface);
            // 编码尺寸按设置走：与采集尺寸不同时用 Win2D 在 GPU 上缩放到目标尺寸。
            var width = size.Width;
            var height = size.Height;
            byte[] bgra;
            if (_targetWidth != size.Width || _targetHeight != size.Height)
            {
                using var target = new CanvasRenderTarget(device, _targetWidth, _targetHeight, 96);
                using (var ds = target.CreateDrawingSession())
                {
                    ds.DrawImage(bitmap, new Windows.Foundation.Rect(0, 0, _targetWidth, _targetHeight));
                }
                bgra = target.GetPixelBytes(); // BGRA8，紧排
                width = _targetWidth;
                height = _targetHeight;
            }
            else
            {
                bgra = bitmap.GetPixelBytes(); // BGRA8，紧排
            }
            if (bgra.Length != width * height * 4)
            {
                ReportErrorOnce("屏幕帧字节数与尺寸不符，已跳过。");
                return;
            }
            var bgr = new byte[width * height * 3];
            for (int i = 0, j = 0; i < bgra.Length; i += 4, j += 3)
            {
                bgr[j] = bgra[i];
                bgr[j + 1] = bgra[i + 1];
                bgr[j + 2] = bgra[i + 2];
            }
            OnVideoSourceRawSample?.Invoke((uint)FrameIntervalMs, width, height, bgr, VideoPixelFormatsEnum.Bgr);
            var encoded = _encoder.EncodeVideo(width, height, bgr, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.H264);
            if (encoded != null && encoded.Length > 0)
                // 90000Hz 时钟：帧间隔毫秒 × 90（如 33ms ≈ 3000）。
                OnVideoSourceEncodedSample?.Invoke((uint)(FrameIntervalMs * 90), encoded);
        }
        catch (Exception ex)
        {
            ReportErrorOnce($"屏幕采集帧处理失败：{ex.Message}");
        }
        finally
        {
            frame?.Dispose();
        }
    }

    private void ReportErrorOnce(string message)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastErrorTime).TotalSeconds < 1) return;
        _lastErrorTime = now;
        OnVideoSourceError?.Invoke(message);
    }

    /// <summary>让用户在系统选择器里选要分享的显示器/窗口（须在 UI 线程调，传主窗口句柄）。</summary>
    public static async Task<GraphicsCaptureItem?> PickCaptureItemAsync(IntPtr hwnd)
    {
        var picker = new GraphicsCapturePicker();
        // WinUI 3 桌面应用给 WinRT 选择器绑 HWND 用 WinRT.Interop 的 helper；
        // 自己 ComImport 一个 IInitializeWithWindow 再强转，CsWinRT 投影的对象转不过去，
        // 真机上报 Invalid cast / 不支持此接口（E_NOINTERFACE）。
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        return await picker.PickSingleItemAsync();
    }
}
