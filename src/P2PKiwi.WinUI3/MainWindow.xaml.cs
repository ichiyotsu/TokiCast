using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using P2PKiwi.WinUI3.Views;

namespace P2PKiwi.WinUI3;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // UI 不变：只做 Win10 能运行的适配，布局 / 配色 / 导航都不改。
        // Win11 用 Mica，Win10（1809+）Mica 不可用，自动回退 Acrylic，
        // 再低则用纯色底；标题栏扩展只在支持的系统上开。
        Title = "TokiCast";
        var appWindow = AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(1120, 720));
        WindowsCompat.ApplyWindowBackdrop(this);
        if (WindowsCompat.SupportsCustomTitleBar)
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(null); // 用 NavigationView 自带标题区
        }

        ContentFrame.Navigate(typeof(HomePage));
        NavView.SelectedItem = NavView.MenuItems[0];
        NavView.Header = "主页";
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        switch (item.Tag as string)
        {
            case "home":
                ContentFrame.Navigate(typeof(HomePage));
                NavView.Header = "主页";
                break;
            case "session":
                ContentFrame.Navigate(typeof(SessionPage));
                NavView.Header = "会话";
                break;
        }
        if (args.IsSettingsSelected)
        {
            ContentFrame.Navigate(typeof(SettingsPage));
            NavView.Header = "设置";
        }
    }
}
