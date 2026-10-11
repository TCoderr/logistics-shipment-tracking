using Lst.Api.Hosting;
using Lst.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lst.Api;

/// <summary>
/// Yalnız Development ve `dotnet run` ile yerel çalıştırma: isteğe bağlı otomatik migration ve demo seed.
/// Compose yığınında bunun yerine tek seferlik `--migrate` komutu (sahip rol) kullanılır ve bu bayraklar kapalıdır.
/// </summary>
internal static class DevelopmentStartup
{
    public static async Task RunAsync(WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            return;
        }

        var configuration = app.Configuration;
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;

        if (configuration.GetValue<bool>("Database:MigrateOnStartup"))
        {
            await services.GetRequiredService<LogisticsDbContext>().Database.MigrateAsync();
        }

        if (configuration.GetValue<bool>("Seed:Enabled"))
        {
            await DemoSeeder.SeedAsync(services, configuration, services.GetRequiredService<ILogger<WebApplication>>(), CancellationToken.None);
        }
    }
}
