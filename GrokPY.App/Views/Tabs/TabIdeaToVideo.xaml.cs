using GrokPY.Core.Models;
using GrokPY.Services;
using GrokPY.Services.Workflow;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GrokPY.App.Views.Tabs;

public partial class TabIdeaToVideo :  System.Windows.Controls.UserControl
{
    private readonly IdeaToVideoWorkflow _workflow;
    private readonly WorkflowControl     _control;
    private readonly SettingsManager     _settings;
    private string? _lastOutputDir;

    // Màu active cho step border
    private static readonly SolidColorBrush ActiveBrush =
        new(System.Windows.Media.Color.FromRgb(0, 184, 148));
    private static readonly SolidColorBrush InactiveBrush =
        new(System.Windows.Media.Color.FromArgb(0x11, 0xFF, 0xFF, 0xFF));

    public TabIdeaToVideo()
    {
        InitializeComponent();
        _workflow = App.Services.GetRequiredService<IdeaToVideoWorkflow>();
        _control  = App.Services.GetRequiredService<WorkflowControl>();
        _settings = App.Services.GetRequiredService<SettingsManager>();

        // Đăng ký events
        _workflow.OnProgress  += OnProgress;
        _workflow.OnCompleted += OnCompleted;
    }

    // ─── Run Pipeline ─────────────────────────────────────────

    private async void BtnRun_Click(object sender, RoutedEventArgs e)
    {
        var idea = TxtIdea.Text.Trim();
        if (string.IsNullOrEmpty(idea))
        {
            ShowStatus("⚠️ Nhập ý tưởng trước", error: true);
            return;
        }

        SetBusy(true);
        TxtLog.Text = string.Empty;
        ResetSteps();

        var sceneCount = int.TryParse(
            (CmbSceneCount.SelectedItem as ComboBoxItem)?.Content?.ToString(),
            out var sc) ? sc : 3;

        var engine = (CmbEngine.SelectedItem as ComboBoxItem)?.Content?.ToString()
            ?.ToLower().Contains("grok") == true ? "grok" : "veo";

        var aspectRatio = CmbAspect.SelectedIndex == 1
            ? VideoAspectRatio.Landscape
            : VideoAspectRatio.Portrait;

        var mergeVideos = ChkMerge.IsChecked == true;

        var cfg       = _settings.LoadSettings();
        var outputDir = string.IsNullOrEmpty(cfg.OutputDirectory)
            ? null
            : Path.Combine(cfg.OutputDirectory, "IdeaToVideo",
                DateTime.Now.ToString("yyyy-MM-dd_HHmmss"));

        _lastOutputDir = outputDir;

        // Chạy pipeline (non-blocking)
        _ = _workflow.RunAsync(
            idea, sceneCount, engine, aspectRatio,
            outputDir, mergeVideos);
    }

    // ─── Stop ─────────────────────────────────────────────────

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        _control.Stop();
        ShowStatus("⏹️ Đã dừng pipeline");
        SetBusy(false);
    }

    // ─── Event Handlers ───────────────────────────────────────

    private void OnProgress(WorkflowProgress p)
    {
        Dispatcher.Invoke(() =>
        {
            // Cập nhật step indicator
            HighlightStep(p.Current);

            PbProgress.Value      = p.Percent;
            PbProgress.Visibility = Visibility.Visible;

            ShowStatus(p.Message);
            AppendLog(p.Message);
        });
    }

    private void OnCompleted(IdeaToVideoResult result)
    {
        Dispatcher.Invoke(() =>
        {
            SetBusy(false);
            PbProgress.Visibility = Visibility.Collapsed;

            if (result.Ok)
            {
                HighlightStep(5); // all done
                ShowStatus($"✅ {result.Message}");
                AppendLog($"✅ XONG! {result.VideoPaths.Count} video đã tạo");
                if (!string.IsNullOrEmpty(result.MergedVideoPath))
                    AppendLog($"🎬 File ghép: {Path.GetFileName(result.MergedVideoPath)}");
                _lastOutputDir = result.OutputDir;
            }
            else
            {
                ShowStatus($"❌ {result.Message}", error: true);
                AppendLog($"❌ Thất bại: {result.Message}");
            }
        });
    }

    // ─── Step Indicator ───────────────────────────────────────

    private void HighlightStep(int step)
    {
        Step1Border.Background = step >= 1 ? ActiveBrush : InactiveBrush;
        Step2Border.Background = step >= 2 ? ActiveBrush : InactiveBrush;
        Step3Border.Background = step >= 3 ? ActiveBrush : InactiveBrush;
        Step4Border.Background = step >= 4 ? ActiveBrush : InactiveBrush;
    }

    private void ResetSteps()
    {
        Step1Border.Background =
        Step2Border.Background =
        Step3Border.Background =
        Step4Border.Background = InactiveBrush;
    }

    // ─── Open Folder ──────────────────────────────────────────

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = _lastOutputDir
            ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (Directory.Exists(dir))
            Process.Start("explorer.exe", dir);
    }

    // ─── Helpers ──────────────────────────────────────────────

    private void SetBusy(bool busy)
    {
        BtnRun.IsEnabled  = !busy;
        BtnStop.IsEnabled = busy;
    }

    private void ShowStatus(string msg, bool error = false)
    {
        TxtStatus.Text       = msg;
        TxtStatus.Foreground = error
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 107, 107))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 184, 148));
    }

    private void AppendLog(string msg)
    {
        TxtLog.Text += $"[{DateTime.Now:HH:mm:ss}] {msg}\n";
        LogScroller.ScrollToEnd();
    }
}
