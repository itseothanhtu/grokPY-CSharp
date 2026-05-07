using System.IO;
using System.Windows;
using GrokPY.Services;
using GrokPY.Services.Chrome;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace GrokPY.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Cấu hình Serilog
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "GrokPY", "logs", "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        // Đăng ký services (Dependency Injection)
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        Log.Information("GrokPY khởi động");
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Logging — dùng SerilogLoggerFactory thay vì AddSerilog extension
        services.AddSingleton<ILoggerFactory>(_ =>
            new SerilogLoggerFactory(Log.Logger, dispose: false));
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        // Core services
        services.AddSingleton<SettingsManager>();
        services.AddSingleton<ChromeProcessManager>();
        services.AddTransient<StealthChrome>();

        // ViewModels
        services.AddTransient<ViewModels.MainViewModel>();
        services.AddTransient<ViewModels.SettingsViewModel>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("GrokPY thoát");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
