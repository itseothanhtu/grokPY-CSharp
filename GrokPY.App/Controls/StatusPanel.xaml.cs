using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GrokPY.App.Controls;

public partial class StatusPanel : UserControl
{
    public StatusPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Thêm dòng log (thread-safe)
    /// </summary>
    public void AppendLog(string message)
    {
        Dispatcher.Invoke(() =>
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
            LogTextBlock.Text += line;

            // Auto scroll xuống cuối
            LogScroller.ScrollToEnd();
        });
    }

    /// <summary>
    /// Cập nhật trạng thái Chrome
    /// </summary>
    public void SetChromeStatus(bool connected, string? message = null)
    {
        Dispatcher.Invoke(() =>
        {
            ChromeStatusDot.Fill = connected
                ? new SolidColorBrush(Color.FromRgb(0, 184, 148))   // green
                : new SolidColorBrush(Color.FromRgb(255, 107, 107)); // red

            ChromeStatusText.Text = message ?? (connected ? "Chrome: Đã kết nối" : "Chrome: Chưa kết nối");
        });
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogTextBlock.Text = string.Empty;
    }
}
