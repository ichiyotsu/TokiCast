using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation.Metadata;

namespace P2PKiwi.WinUI3;

/// <summary>
/// Win10 兼容层：UI 不变，只换底层实现。
/// Win11（22000+）：Mica；Win10 1809+：Mica 不可用，回退 Acrylic，
/// 若 Acrylic 也不支持则用纯色。布局、配色、圆角、导航一律不动。
/// </summary>
public static class WindowsCompat
{
    /// <summary>是否 Win11（build &gt;= 22000）。</summary>
    public static bool IsWindows11 =>
        ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 14);

    // 自定义标题栏与画面采集在最低版本 Win10 1809 上即已支持，不再单独检测，调用处直接按支持处理。

    public static void ApplyWindowBackdrop(Window window)
    {
        // 必须在运行时判断，不能在 XAML 里写死 Mica，否则 Win10 上
        // MicaController 会失败。顺序：Mica → Acrylic → 纯色。
        if (IsWindows11 && MicaController.IsSupported())
        {
            window.SystemBackdrop = new MicaBackdrop();
            return;
        }
        if (DesktopAcrylicController.IsSupported())
        {
            window.SystemBackdrop = new DesktopAcrylicBackdrop();
            return;
        }
        // 最后兜底：不设 SystemBackdrop，用 XAML 默认纯色底，UI 结构不变。
        window.SystemBackdrop = null;
    }
}
