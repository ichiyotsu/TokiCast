# TokiCast — WinUI 3 版

TokiCast 是 p2p.kiwi（[p2p.kiwi](https://p2p.kiwi/)，GitHub：`dont-be-evil-company/p2p.kiwi`，MIT，
v3.10.0）的 WinUI 3 / Windows App SDK 复刻骨架。

原版是 Electron + Svelte 的跨平台 P2P 投屏 / 会议工具：无账号、无中心云，
屏幕、语音、聊天、远程控制（鼠标 / 键盘）都走 WebRTC mesh 直连；E2EE 由
MLS（RFC 9420，成员与密钥管理）+ SFrame（逐帧加密）+ DTLS-SRTP（传输加密）
三层组成；STUN / 可选 TURN 只用来交换 ICE 连通信息，不做聊天或媒体密钥的
信令服务器。接收方用浏览器打开邀请链接即可观看，发送方用桌面端采集。

这一版把同一套产品做成原生 Windows 应用：WinUI 3 + .NET 8 + Windows App SDK，
设计按 WinUI 3 原生控件 + TokiUI Windows 变体（Inter / 思源黑体、品牌种子色
`#6750A4`、圆角与亚克力 / Mica）走。

> 注意：这台 Linux VM 编不出 WinUI 3（需要 Windows + Visual Studio）。
> 这里给的是可直接在 Windows 上打开的完整工程骨架 + 已接通的 UI / 状态机，
> 采集、WebRTC、远程输入三处标了 `TODO(Windows)` 接线点，见下文。

## 产品对应关系

| p2p.kiwi 原版 | WinUI 3 版 |
|---|---|
| 发起投屏（选显示器 / 窗口） | `HomePage` → 选源 → `SessionPage`，`GraphicsCapturePicker` |
| 邀请链接 `p2p.kiwi/session/<id>` | `SessionManager.CreateSessionAsync()` 生成 `https://p2p.kiwi/session/<id>`，一键复制 |
| 加入会话（浏览器端观看） | `HomePage` 粘贴链接 / 输入房间码加入 |
| 屏幕 + 语音 + 聊天 | `SessionPage`：画面区 + 右侧聊天 / 成员，`MediaCapture` 语音 |
| 远程控制（鼠标 / 键盘）+ 多光标 | `RemoteInputService`（`SendInput`）+ 画面上的远端光标层，需对方明确授权 |
| E2EE：MLS + SFrame + DTLS-SRTP | `E2eeService`：接口与密钥轮换骨架；MLS/SFrame 需接原生库，见 `Services/E2eeService.cs` |
| 无账号、无云 | 不存账号；房间只在内存里，关闭即销毁；只用 STUN/TURN 做 ICE |

## 工程结构

```
P2PKiwi.WinUI3.sln
src/P2PKiwi.WinUI3/
  P2PKiwi.WinUI3.csproj        # net8.0-windows10.0.19041.0 / WinAppSDK 1.6
  Package.appxmanifest
  App.xaml / App.xaml.cs
  MainWindow.xaml(.cs)         # NavigationView：主页 / 会话 / 设置
  Views/
    HomePage.xaml(.cs)         # 发起、加入、最近会话、选源
    SessionPage.xaml(.cs)      # 投屏画面 + 远端光标层 + 聊天 + 控制条
    SettingsPage.xaml(.cs)     # 画质 / 音频 / STUN-TURN / 隐私
  ViewModels/
    HomeViewModel.cs  SessionViewModel.cs  SettingsViewModel.cs
  Services/
    ISessionManager.cs / SessionManager.cs   # 房间、邀请链接、成员
    IScreenCaptureService.cs                 # Windows.Graphics.Capture
    IWebRtcService.cs                        # WebRTC 抽象（见下）
    IRemoteInputService.cs                   # SendInput 鼠标键盘注入
    ISignalingService.cs                     # 只交换 SDP/ICE，不传媒体密钥
    E2eeService.cs                           # MLS/SFrame 骨架 + 校验提醒
  Models/  Participant.cs  ChatMessage.cs  SessionInfo.cs
  Themes/  TokiTokens.xaml                   # TokiUI token：颜色 / 圆角 / 字号
```

## 三个接线点（在 Windows 上补完即能跑通）

1. **画面采集 — 已写好**：`ScreenCaptureService` 用 `Windows.Graphics.Capture`
  （`GraphicsCapturePicker` + `GraphicsCaptureSession` + `Direct3D11CaptureFramePool`），
   帧回调交给 WebRTC 的视频源。需要 `graphicsCapture` capability（manifest 已加）。
2. **WebRTC — 接口已定，实现待选**：`IWebRtcService` 只定义了
   `CreatePeerConnection / AddScreenTrack / SendCursor / SendChat` 等方法，
   不绑定具体库。建议二选一：
   - 纯 .NET 快速验证：SIPSorcery（`SIPSorceryMedia` + `RTCPeerConnection`），
     不用编 C++，适合先跑通信令 + 投屏；
   - 生产：Google WebRTC Native（M89+ 的 `webrtc.dll`）经 P/Invoke / C++/WinRT
     封装，VP8/VP9/H.264 编码与硬件加速最好，SFrame 才能逐帧接进去。
   不要用已归档的 MixedReality-WebRTC。
3. **远程输入 — 已写好注入，缺授权 UI**：`RemoteInputService` 用
   `user32!SendInput` 做鼠标移动 / 点击 / 滚轮 / 按键。对方必须在会话里
   点“允许远程控制”才会生效；收到控制帧先校验发送方在成员表里且有权限，
   否则丢弃。切勿做成默认开启。
4. **E2EE — 别只信 DTLS-SRTP**：原版 README 明确提醒，WebRTC 报 DTLS-SRTP
   不等于端到端加密。真正的 E2EE 是 MLS 管密钥 + SFrame 加密每一帧。
   `E2eeService` 里留了 `DeriveFrameKey / EncryptFrame / RotateKeys` 的
   骨架与“未接通时 UI 必须显示未加密”的强制规则：没接 SFrame 之前，
   会话页顶栏不许显示 E2EE 徽标。

## Win10 适配（UI 不变，只改运行层）

- 最低版本：Windows 10 1809（build 17763），`TargetPlatformMinVersion 10.0.17763.0`，
  `TargetFramework net8.0-windows10.0.19041.0`，Windows App SDK 1.6 本身就支持 1809+。
  不用改 UI，XAML / NavigationView / 圆角 / 配色原样。
- 材质回退（`Services/WindowsCompat.cs`，运行时判断，不写死）：
  Win11 → Mica；Win10 → Acrylic；Acrylic 不支持 → 纯色底。只换 `SystemBackdrop`，
  不动任何页面布局。
- 标题栏：`ExtendsContentIntoTitleBar` 在 Win10 1809+ 即支持，保留；若将来在更老
  系统上跑，把它关掉即可，页面不受影响。
- 画面采集 `Windows.Graphics.Capture` 需要 1809+，正好等于最低版本，不用降级方案。
- Win10 上唯一看得见的差别：窗口是直角（Win11 的圆角是系统画的，不是应用画的），
  Mica 换成 Acrylic 后底色稍有不同；其他全部一样。

## 图标

App 图标用 Keyline Icons 的 `cast-two-tone`（MIT 协议，24×24，`currentColor`，
屏幕框 40% 透明 + 两道投屏弧线 + 圆点），原文件见 `Assets/cast-two-tone.svg`
（经 Iconify `keyline-icons/cast-two-tone` 核对，yesicon 同款链接被 Cloudflare 挡住，
改走 Iconify API 取的同一份 SVG）。
`Assets/AppIcon.svg` 是它的应用版：白字画在 Toki 品牌紫 `#6750A4` 圆角底上，
由 `tools-icon/render.js`（@resvg/resvg-js）渲染出 `Square150x150Logo.png` /
`Square44x44Logo.png` / `StoreLogo.png` / `TokiCast.ico`（16/32/48/256）。
`Package.appxmanifest` 与 `.csproj` 已指向新图标。

## 在 Windows 上跑

1. Windows 10 1809+ / Windows 11，装 Visual Studio 2022 + “Windows 应用开发”
   工作负载（Windows App SDK 1.6）。
2. 打开 `P2PKiwi.WinUI3.sln`，还原 NuGet，选 x64 Debug，F5。
3. 首次发起投屏时 Windows 会弹采集授权；远程控制需对方在会话里授权。
4. STUN 默认 `stun:stun.l.google.com:19302`，TURN 在设置页填自己的，
   不填就只走直连 + STUN，NAT 打不通时会提示改用 TURN。

## 原型

同目录外另有一个可交互的 WinUI 3 视觉原型（网页版，见对话里的卡片）：
主页、发起 / 加入、会话投屏、聊天、设置五块都能点，用模拟画面演示
多光标、远程控制开关、E2EE 徽标与邀请链接流程。原型里的画面是模拟的，
真采集要走上面这套工程。

## 0.1.0（首个版本，骨架版）

- Windows 上的 WinUI 3 界面：主页（发起 / 加入）、会话页（投屏画面 + 多光标层 + 聊天 + 控制条）、设置页。
- Win10 1809+ 适配：Mica 自动回退 Acrylic / 纯色，UI 不变。
- 图标：Keyline `cast-two-tone`，白字 on #6750A4。
- 实话：这一版是骨架——WebRTC、画面采集、远程输入、MLS/SFrame 还是接口 / 占位，真投屏要等接线完成后才通。发 0.1.0 是为了先把工程、图标和 CI 发布链路立起来。
- 发布：打 tag `v0.1.0` 触发 `.github/workflows/release.yml`，在 windows-latest 上编
    Release x64，产物是 Inno Setup 安装器 `TokiCast-0.1.0.exe`，标题只写 `0.1.0`。
  （最初挂的是 `.msixbundle`，同日按 Windows 发 exe 的规矩换成了 exe 安装器。）
  ## 0.1.1（闪退修复）
  

  - 修复：点“开始分享”或左边栏“会话”后闪退——会话页加载时 `MediaPlayerElement` 与 `x:Bind` 按钮会触发未处理异常，现已去掉这两个加载时构造（远端画面改占位，“允许”按钮改在代码里创建）；开始 / 加入失败改为弹窗提示；另加全局异常兜底，出错时弹窗显示错误信息，不再直接退出。
  - 左边栏：宽度按 Tokilive 的 WinUI 设计，展开 200 / 收起 48，`PaneDisplayMode` 改 Auto。
  - E2EE 徽标：MLS 密钥未建立前不再显示“已启用”。
  - 发布：Windows exe 安装包 `TokiCast-0.1.1.exe`（Inno Setup）。
  ## 0.1.2（会话页图标闪退 + 设置页偏移）
  - 修复：点“开始分享”进会话页仍闪退——会话页底部按钮用了不存在的 `Chat` 图标名，
    `IconElement` 转换失败，现改成 `Message`（聊天气泡）；全局兜底已把这次错误弹窗报出来。
    - 设置页：删掉与左边栏头重复的“设置”大标题；内容改为左对齐固定，宽窗口下不再漂到右侧；
      展开项里的下拉框与滑杆改为拉满宽度，不再挤在卡片右侧。
      - 发布：Windows exe 安装包 `TokiCast-0.1.2.exe`（Inno Setup）。
  
## 许可与来源

p2p.kiwi 原版 MIT 协议（`dont-be-evil-company/p2p.kiwi`）。本复刻只参考其
公开的产品行为与 README 描述的架构，不复制其源码；E2EE 架构说明来自原版
README（MLS + SFrame + DTLS-SRTP）。
