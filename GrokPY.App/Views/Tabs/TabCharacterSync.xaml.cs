using GrokPY.Services;
using GrokPY.Services.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace GrokPY.App.Views.Tabs;

public partial class TabCharacterSync :  System.Windows.Controls.UserControl
{
    private readonly CharacterSyncService _syncService;
    private readonly SettingsManager      _settings;
    private string? _lastOutputDir;

    public TabCharacterSync()
    {
        InitializeComponent();
        _syncService = App.Services.GetRequiredService<CharacterSyncService>();
        _settings    = App.Services.GetRequiredService<SettingsManager>();
    }

    private void BtnBrowseVideo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Chọn video",
            Filter = "Video|*.mp4;*.webm;*.mov"
        };
        if (dlg.ShowDialog() == true)
            TxtVideoPath.Text = dlg.FileName;
    }

    private void BtnBrowseAudio_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Chọn audio",
            Filter = "Audio|*.mp3;*.wav;*.m4a;*.ogg"
        };
        if (dlg.ShowDialog() == true)
            TxtAudioPath.Text = dlg.FileName;
    }

    private async void BtnSync_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(TxtVideoPath.Text))
        { ShowStatus("⚠️ Chọn video đầu vào", error: true); return; }

        if (string.IsNullOrEmpty(TxtAudioPath.Text))
        { ShowStatus("⚠️ Chọn audio đầu vào", error: true); return; }

        SetBusy(true);
        LstOutput.Items.Clear();

        var cfg       = _settings.LoadSettings();
        var outputDir = string.IsNullOrEmpty(cfg.OutputDirectory)
            ? null
            : Path.Combine(cfg.OutputDirectory, "Sync",
                DateTime.Now.ToString("yyyy-MM-dd"));
        _lastOutputDir = outputDir;

        var result = await _syncService.SyncAsync(
            TxtVideoPath.Text,
            TxtAudioPath.Text,
            outputDir: outputDir,
            onProgress: msg =>
                Dispatcher.Invoke(() => ShowStatus(msg)));

        Dispatcher.Invoke(() =>
        {
            SetBusy(false);
            if (result.Ok)
            {
                foreach (var f in result.SavedFiles)
                    LstOutput.Items.Add(Path.GetFileName(f));
                ShowStatus($"✅ Sync hoàn tất! {result.SavedFiles.Count} video");
                _lastOutputDir = result.SavedFiles.Count > 0
                    ? Path.GetDirectoryName(result.SavedFiles[0])
                    : outputDir;
            }
            else
            {
                ShowStatus($"❌ {result.Message}", error: true);
            }
        });
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e) => SetBusy(false);

    private void BtnOpenOutput_Click(object sender, RoutedEventArgs e)
    {
        var dir = _lastOutputDir
            ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (Directory.Exists(dir))
            Process.Start("explorer.exe", dir);
    }

    private void SetBusy(bool busy)
    {
        BtnSync.IsEnabled  = !busy;
        BtnStop.IsEnabled  = busy;
        PbProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
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
