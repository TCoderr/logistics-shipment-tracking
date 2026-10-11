using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lst.IntegrationTests.Infrastructure;

/// <summary>
/// Uygulamayı çalışma zamanı DB rolüyle ayağa kaldırır. İstemci BaseAddress'i HTTPS'tir; __Host- / Secure cookie'ler
/// ve antiforgery akışı gerçek cookie container ile çalışır.
/// </summary>
public sealed class LstApiFactory(
    LstDatabaseFixture database,
    TimeProvider? timeProvider = null,
    Action<IServiceCollection>? configureServices = null,
    string environment = "Testing",
    IReadOnlyDictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
{
    public CapturingLoggerProvider Logs { get; } = new();

    public HttpClient CreateHttpsClient() =>
        CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("ConnectionStrings:Lst", database.RuntimeConnectionString);
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.UseSetting("Seed:Enabled", "false");
        // Arka plan worker'ları testlerde kapalıdır; inbox işleme testten açıkça ve yalnız testin kendi makbuzlarıyla tetiklenir.
        builder.UseSetting("Workers:Enabled", "false");
        builder.UseSetting("Carriers:Secrets:SIMEX:Values:0", TestCarrier.Secret);
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddProvider(Logs);
        });
        builder.ConfigureTestServices(services =>
        {
            if (timeProvider is not null)
            {
                services.AddSingleton(timeProvider);
            }

            configureServices?.Invoke(services);
        });
    }
}

/// <summary>Bu test koşusuna özel, rastgele SIMEX webhook secret'ı (repoda/çıktıda yer almaz).</summary>
public static class TestCarrier
{
    public const string Code = "SIMEX";

    public static readonly string Secret = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
}

/// <summary>Tüm log çıktısını (mesaj, state değerleri, exception) toplar; PII sızıntı testleri bunu tarar.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyList<string> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(";", pairs.Select(p => $"{p.Key}={p.Value}"))
                : string.Empty;
            entries.Enqueue($"{logLevel}|{category}|{formatter(state, exception)}|{values}|{exception}");
        }
    }
}
