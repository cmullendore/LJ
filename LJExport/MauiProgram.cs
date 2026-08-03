using Microsoft.Extensions.Logging;

namespace LJExport
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
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
