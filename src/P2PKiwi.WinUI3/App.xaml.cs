using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace P2PKiwi.WinUI3;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        // 全局兜底：未处理异常不再直接闪退，弹窗显示错误信息。
        UnhandledException += OnUnhandledException;
    }

    private async void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        try
        {
            var root = MainWindow?.Content?.XamlRoot;
            if (root is null) return;
            var dialog = new ContentDialog
            {
                Title = "出错了",
                Content = $"应用遇到了未处理的错误：\n{e.Message}\n\n请把这段错误信息发给开发者。",
                CloseButtonText = "知道了",
                XamlRoot = root,
            };
            await dialog.ShowAsync();
        }
        catch
        {
            // 弹窗本身失败时不再抛，避免二次崩溃。
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
