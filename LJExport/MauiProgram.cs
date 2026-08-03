using Microsoft.Extensions.Logging;
using Serilog;

namespace LJExport
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var logDirectory = Path.Combine(FileSystem.AppDataDirectory, "Logs");
            Directory.CreateDirectory(logDirectory);
            var logFilePath = Path.Combine(logDirectory, $"LJExport-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffffffZ}.log");
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .Enrich.FromLogContext()
                .WriteTo.Console()
                .WriteTo.File(logFilePath, shared: true)
                .CreateLogger();
            Log.Information("Serilog initialized. File log: {LogFilePath}", logFilePath);

            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

            builder.Services.AddSingleton(_ =>
            {
                var client = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(60)
                };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("LJExport/1.0");
                return client;
            });
            builder.Services.AddSingleton(services => new Services.LiveJournalClient(services.GetRequiredService<HttpClient>()));
            builder.Services.AddSingleton(services => new Services.JournalExportService(services.GetRequiredService<HttpClient>()));
            builder.Services.AddSingleton(services => new Services.ScrapbookClient(services.GetRequiredService<HttpClient>()));
            builder.Services.AddTransient<MainPage>();
            builder.Services.AddTransient<AppShell>();

#if DEBUG
    		builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
