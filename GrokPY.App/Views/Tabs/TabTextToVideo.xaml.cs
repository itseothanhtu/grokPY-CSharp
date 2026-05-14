using GrokPY.Core.Models;
using GrokPY.Services;
using GrokPY.Services.Api;
using GrokPY.Services.Workflow;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace GrokPY.App.Views.Tabs;

public partial class TabTextToVideo :  System.Windows.Controls.UserControl
{
    private readonly VeoTextToVideoService  _veoService;
    private readonly GrokTextToVideoService _grokService;
    private readonly WorkflowControl        _control;
    private readonly SettingsManager        _settings;

    private string? _lastOutputDir;

    public TabTextToVideo()
    {
        InitializeComponent();
        _veoService  = App.Services.GetRequiredService<VeoTextToVideoService>();
        _grokService = App.Services.GetRequiredService<GrokTextToVideoService>();
        _control     = App.Services.GetRequiredService<WorkflowControl>();
        _settings    = App.Services.GetRequiredService<SettingsManager>();
    }

    // ─── Generate ─────────────────────────────────────────────

    private async void BtnGenerate_Click(object sender, RoutedEventArgs e)
    {
        var prompt = TxtPrompt.Text.Trim();
        if (string.IsNullOrEmpty(prompt))
        {
            ShowStatus("⚠️ Vui lòng nhập prompt", error: true);
            return;
        }

        SetBusy(true);
        LstOutput.Items.Clear();

        // Lấy config
        var aspectRatio = CmbAspectRatio.SelectedIndex == 1
            ? VideoAspectRatio.Landscape
            : VideoAspectRatio.Portrait;

        var duration = int.TryParse(
            (CmbDuration.SelectedItem as ComboBoxItem)?.Content?.ToString(), out var d) ? d : 6;

        var cfg       = _settings.LoadSettings();
        var outputDir = string.IsNullOrEmpty(cfg.OutputDirectory)
            ? null : Path.Combine(cfg.OutputDirectory, "Videos",
                DateTime.Now.ToString("yyyy-MM-dd"));

        _lastOutputDir = outputDir;

        // Batch mode?
        if (ChkBatch.IsChecked == true)
        {
            await RunBatchAsync(prompt, aspectRatio, duration, outputDir);
            return;
        }

        // Single
        await RunSingleAsync(prompt, aspectRatio, duration, outputDir);
        SetBusy(false);
    }

    private async Task RunSingleAsync(
        string prompt, string aspectRatio,
        int duration, string? outputDir)
    {
        try
        {
            VideoGenResult result;

            if (RbGrok.IsChecked == true)
            {
                result = await _grokService.GenerateAsync(
                    prompt, aspectRatio,
                    outputDir: outputDir,
                    onProgress: msg => Dispatcher.Invoke(() =>
                    {
                        ShowStatus(msg);
                        PbProgress.Visibility = Visibility.Visible;
                    }));
            }
            else
            {
                result = await _veoService.GenerateAsync(
                    prompt, aspectRatio, duration,
                    outputDir: outputDir,
                    onProgress: msg => Dispatcher.Invoke(() =>
                    {
                        ShowStatus(msg);
                        PbProgress.Visibility = Visibility.Visible;
                    }));
            }

            Dispatcher.Invoke(() =>
            {
                if (result.Ok)
                {
                    foreach (var f in result.SavedFiles)
                        LstOutput.Items.Add(Path.GetFileName(f));
                    ShowStatus($"✅ Xong! {result.SavedFiles.Count} video");
                    _lastOutputDir = Path.GetDirectoryName(result.SavedFiles[0]);
                }
                else
                {
                    ShowStatus($"❌ {result.Message}", error: true);
                }
                PbProgress.Visibility = Visibility.Collapsed;
                SetBusy(false);
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                ShowStatus($"❌ Lỗi: {ex.Message}", error: true);
                SetBusy(false);
            });
        }
    }

    private async Task RunBatchAsync(
        string allPrompts, string aspectRatio,
        int duration, string? outputDir)
    {
        var prompts = allPrompts
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        ShowStatus($"📋 Batch mode: {prompts.Count} prompts");

        var runner = App.Services.GetRequiredService<WorkflowRunner>();
        runner.OnProgress += p =>
            Dispatcher.Invoke(() =>
            {
                ShowStatus(p.Message);
                PbProgress.Value      = p.Percent;
                PbProgress.Visibility = Visibility.Visible;
            });

        runner.OnTaskCompleted += r =>
            Dispatcher.Invoke(() =>
                LstOutput.Items.Add(r.Ok ? $"✅ {r.TaskName[..Math.Min(30,r.TaskName.Length)]}" :
                    $"❌ {r.TaskName[..Math.Min(30,r.TaskName.Length)]}"));

        runner.OnCompleted += s =>
            Dispatcher.Invoke(() =>
            {
                ShowStatus($"✅ Batch xong: {s.StatusText}");
                PbProgress.Visibility = Visibility.Collapsed;
                SetBusy(false);
            });

        var engine = RbGrok.IsChecked == true ? "grok" : "veo";
        await runner.RunTextToVideoBatchAsync(
            prompts, engine, aspectRatio, duration, outputDir);
    }

    // ─── Stop ─────────────────────────────────────────────────

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        _control.Stop();
        ShowStatus("⏹️ Đã dừng");
        SetBusy(false);
    }

    // ─── Open folder ──────────────────────────────────────────

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
        BtnGenerate.IsEnabled = !busy;
        BtnStop.IsEnabled     = busy;
        if (!busy) PbProgress.Visibility = Visibility.Collapsed;
    }

    private void ShowStatus(string msg, bool error = false)
    {
        TxtStatus.Text       = msg;
        TxtStatus.Foreground = error
            ? new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(255, 107, 107))
            : new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0, 184, 148));
    }
}
