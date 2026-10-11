using System.Security.Cryptography;
using Lst.Api;
using Lst.Api.Hosting;
using Lst.IntegrationTests.Infrastructure;
using Lst.Modules.Identity;
using Lst.Modules.Operations;
using Lst.Modules.Shipments;
using Lst.Modules.Tracking;
using Lst.Platform.Identity;
using Lst.Platform.Operations;
using Lst.Platform.Persistence;
using Lst.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(LstDatabaseFixture))]

namespace Lst.IntegrationTests.Infrastructure;

public sealed record TestUser(string UserName, string Password, string Role);

/// <summary>
/// Bu koşuya özel PostgreSQL 18:
/// - Varsayılan: Testcontainers ile yeni konteyner (paylaşılan DB/konteynere dokunulmaz).
/// - Alternatif: LST_TEST_POSTGRES (sahip rol) + LST_TEST_RUNTIME_POSTGRES (çalışma zamanı rolü) +
///   LST_TEST_ALLOW_DESTRUCTIVE=true + LST_TEST_RUN_ID + lst_test_marker.run işareti; <see cref="TestDatabaseGuard"/> doğrular.
/// Migration'ı sahip rol uygular; uygulama yalnız db/runtime-role-grants.sql yetkilerine sahip ayrı rolle bağlanır.
/// Docker ve yerel hedef yoksa fixture HATA verir: testler atlanmaz, başarılı sayılmaz.
/// Bağlantı dizeleri ve parolalar hiçbir çıktıya yazılmaz.
/// </summary>
public sealed class LstDatabaseFixture : IAsyncLifetime
{
    public const string PostgresImage = "postgres:18.6";

    public static readonly IReadOnlyList<(string UserName, string Role, string[] Hubs)> UserSpecs =
    [
        ("ops.agent", Roles.OpsAgent, []),
        ("ops.agent2", Roles.OpsAgent, []),
        ("ops.manager", Roles.OpsManager, []),
        ("hub.ist", Roles.HubOperator, ["IST-01"]),
        ("hub.izm", Roles.HubOperator, ["IZM-01"]),
        ("ops.flow", Roles.OpsAgent, []),
        ("ops.csrf", Roles.OpsAgent, []),
        ("ops.revoked", Roles.OpsAgent, []),
        ("ops.auditfail", Roles.OpsAgent, []),
        ("ops.lock", Roles.OpsAgent, []),
        ("ops.expiry", Roles.OpsAgent, []),
        ("ops.expiry10", Roles.OpsAgent, []),
        ("ops.collide", Roles.OpsAgent, []),
        ("ops.logs", Roles.OpsAgent, []),
        ("ops.parallel", Roles.OpsAgent, []),
        ("ops.lockout", Roles.OpsAgent, []),
        ("hub.ank", Roles.HubOperator, ["ANK-02"]),
        ("hub.ist2", Roles.HubOperator, ["IST-01"]),
        ("ops.queue", Roles.OpsAgent, []),
        ("ops.wrongpw", Roles.OpsAgent, []),
        // Dilim 10: yetkisi test içinde değiştirilen/oturumu kapatılan kullanıcılar (diğer testleri etkilemesin).
        ("rt.hub", Roles.HubOperator, ["IZM-01"]),
        ("rt.ops", Roles.OpsAgent, []),
        ("rt.logout", Roles.OpsAgent, []),
        ("rt.expiry", Roles.OpsAgent, []),
    ];

    private readonly string _runId = "r" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
    private readonly Dictionary<string, TestUser> _users = new(StringComparer.Ordinal);
    private PostgreSqlContainer? _container;
    private ServiceProvider? _ownerServices;

    public string OwnerConnectionString { get; private set; } = string.Empty;

    public string RuntimeConnectionString { get; private set; } = string.Empty;

    public IReadOnlyDictionary<string, Guid> Hubs { get; private set; } = new Dictionary<string, Guid>();

    public TestUser User(string userName) => _users[userName];

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var localOwner = Environment.GetEnvironmentVariable("LST_TEST_POSTGRES");
        if (!string.IsNullOrWhiteSpace(localOwner))
        {
            var localRuntime = Environment.GetEnvironmentVariable("LST_TEST_RUNTIME_POSTGRES");
            if (string.IsNullOrWhiteSpace(localRuntime))
            {
                throw new UnsafeTestTargetException("LST_TEST_RUNTIME_POSTGRES (ayrı çalışma zamanı rolü) tanımlı olmalı.");
            }

            var settings = TestDatabaseGuard.FromEnvironment(Environment.GetEnvironmentVariable, localOwner);
            if (new NpgsqlConnectionStringBuilder(localRuntime).Database != settings.ConnectionDatabase)
            {
                throw new UnsafeTestTargetException("Çalışma zamanı bağlantısı aynı test veritabanını hedeflemeli.");
            }

            OwnerConnectionString = localOwner;
            RuntimeConnectionString = localRuntime;
            await TestDatabaseGuard.PrepareAsync(
                settings,
                new NpgsqlTestDatabaseProbe(localOwner),
                new Preparation(this, createRuntimeRole: false),
                cancellationToken);
            return;
        }

        try
        {
            _container = new PostgreSqlBuilder(PostgresImage)
                .WithDatabase("lst_test_" + _runId)
                .WithUsername("lst_owner")
                .WithPassword(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)))
                .Build();
            await _container.StartAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "Integration testleri ÇALIŞTIRILAMADI: Docker Engine'e erişilemedi (Testcontainers). " +
                "Docker Desktop'ı başlatın veya LST_TEST_POSTGRES + LST_TEST_RUNTIME_POSTGRES + LST_TEST_ALLOW_DESTRUCTIVE=true + " +
                "LST_TEST_RUN_ID ile işaretli ayrı bir test veritabanı sağlayın. Hata tipi: " + ex.GetType().Name,
                ex);
        }

        OwnerConnectionString = _container.GetConnectionString();
        await new Preparation(this, createRuntimeRole: true).PrepareAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownerServices is not null)
        {
            await _ownerServices.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public NpgsqlConnection OpenOwnerConnection() => OpenConnection(OwnerConnectionString);

    public NpgsqlConnection OpenRuntimeConnection() => OpenConnection(RuntimeConnectionString);

    public async Task RemoveRoleAsync(string userName, string role)
    {
        await using var scope = _ownerServices!.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<LstUser>>();
        var user = await userManager.FindByNameAsync(userName) ?? throw new InvalidOperationException("Kullanıcı yok.");
        var result = await userManager.RemoveFromRoleAsync(user, role);
        Assert.True(result.Succeeded);
    }

    public async Task<Guid> UserIdAsync(string userName)
    {
        await using var scope = _ownerServices!.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<LstUser>>();
        return (await userManager.FindByNameAsync(userName))!.Id;
    }

    private static NpgsqlConnection OpenConnection(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        return connection;
    }

    private sealed class Preparation(LstDatabaseFixture fixture, bool createRuntimeRole) : IDestructiveDatabaseActions
    {
        public async Task PrepareAsync(CancellationToken cancellationToken)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(TimeProvider.System);
            services.AddHttpContextAccessor();
            services.AddDbContext<LogisticsDbContext>(o => PersistenceRegistration.Configure(o, fixture.OwnerConnectionString));
            services.AddIdentityModule();
            services.AddShipmentsModule(new ConfigurationBuilder().Build());
            services.AddOperationsModule();
            services.AddTrackingPersistence();
            fixture._ownerServices = services.BuildServiceProvider();

            string runtimeRole;
            if (createRuntimeRole)
            {
                runtimeRole = "lst_rt_" + fixture._runId;
                var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
                await using var connection = fixture.OpenOwnerConnection();
#pragma warning disable CA2100 // Rol adı ve parola bu koşuda üretilen hex değerlerdir; dış girdi içermez.
                await using (var create = new NpgsqlCommand($"CREATE ROLE \"{runtimeRole}\" LOGIN PASSWORD '{password}'", connection))
#pragma warning restore CA2100
                {
                    await create.ExecuteNonQueryAsync(cancellationToken);
                }

                fixture.RuntimeConnectionString = new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString)
                {
                    Username = runtimeRole,
                    Password = password,
                }.ConnectionString;
            }
            else
            {
                runtimeRole = new NpgsqlConnectionStringBuilder(fixture.RuntimeConnectionString).Username
                    ?? throw new UnsafeTestTargetException("Çalışma zamanı bağlantısında kullanıcı adı yok.");
            }

            // Üretimdeki `--migrate` komutuyla aynı kod: migration + gömülü runtime-role-grants.sql.
            await using (var scope = fixture._ownerServices.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
                await DatabaseMaintenance.MigrateAndGrantAsync(db, runtimeRole, cancellationToken);
            }

            await using (var scope = fixture._ownerServices.CreateAsyncScope())
            {
                var sp = scope.ServiceProvider;
                var db = sp.GetRequiredService<LogisticsDbContext>();
                var now = DateTimeOffset.UtcNow;
                fixture.Hubs = await HubSeeding.EnsureAsync(db, HubSeeding.Defaults, now, cancellationToken);
                await IdentitySeeding.EnsureRolesAsync(sp.GetRequiredService<RoleManager<LstRole>>());
                var userManager = sp.GetRequiredService<UserManager<LstUser>>();
                foreach (var (userName, role, hubCodes) in UserSpecs)
                {
                    // Sentetik, koşuya özel parola; hiçbir çıktıya yazılmaz.
                    var password = "Lst!" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12)) + "aA9";
                    await IdentitySeeding.EnsureUserAsync(
                        userManager, db, userName, userName, role, password,
                        hubCodes.Select(c => fixture.Hubs[c]).ToList(), now, cancellationToken);
                    fixture._users[userName] = new TestUser(userName, password, role);
                }
            }
        }
    }
}
