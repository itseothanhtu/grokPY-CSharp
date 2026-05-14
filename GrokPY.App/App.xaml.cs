using System.IO;
using System.Windows;
using GrokPY.Services;
using GrokPY.Services.Api;
using GrokPY.Services.Auth;
using GrokPY.Services.Chrome;
using GrokPY.Services.Media;
using GrokPY.Services.Workflow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace GrokPY.App;

public partial class App : System.Windows.Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        // Cấu hình Serilog
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GrokPY", "logs");
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(logDir, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        Log.Information("GrokPY khởi động");
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Logging
        services.AddSingleton<ILoggerFactory>(_ =>
            new SerilogLoggerFactory(Log.Logger, dispose: false));
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        // Core services
        services.AddSingleton<SettingsManager>();
        services.AddSingleton<LicenseManager>();
        services.AddSingleton<ChromeProcessManager>();
        services.AddSingleton<ChromeProfileManager>();
        services.AddTransient<StealthChrome>();

        // Auth services
        services.AddTransient<LoginService>();
        services.AddTransient<TokenExtractor>();
        services.AddTransient<StatsigDiscovery>();

        // API services
        services.AddSingleton<GoogleImageService>();
        services.AddSingleton<GoogleImageToImageService>();
        services.AddSingleton<VeoTextToVideoService>();
        services.AddSingleton<VeoImageToVideoService>();
        services.AddSingleton<GrokTextToVideoService>();
        services.AddSingleton<GrokImageToVideoService>();
        services.AddSingleton<CharacterSyncService>();
        services.AddSingleton<SoraUploadService>();
        services.AddSingleton<VideoMerger>();

        // Workflow
        services.AddSingleton<WorkflowControl>();
        services.AddTransient<WorkflowRunner>();
        services.AddTransient<IdeaToVideoWorkflow>();

        // ViewModels
        services.AddTransient<ViewModels.MainViewModel>();
        services.AddTransient<ViewModels.SettingsViewModel>();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        Log.Information("GrokPY thoát");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
