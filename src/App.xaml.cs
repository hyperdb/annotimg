using System.Windows;
using System.Windows.Threading;

namespace AnnotImg;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
    }

    static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 想定外のエラーで編集中の内容ごとアプリが落ちないようにする
        MessageBox.Show($"エラーが発生しました。\n{e.Exception.Message}", "AnnotImg", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
