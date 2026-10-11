using System.Net;
using Lst.Testing;

namespace Lst.UnitTests;

/// <summary>Yanlış hedefte yıkıcı işlemin (migration/seed/grant) çağrılmadığı spy ile kanıtlanır; gerçek DB'ye bağlanılmaz.</summary>
public sealed class TestDatabaseGuardTests
{
    private const string RunId = "run20261009a";
    private const string Database = "lst_test_run20261009a";

    private sealed class SpyActions : IDestructiveDatabaseActions
    {
        public int Calls { get; private set; }

        public Task PrepareAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProbe(ProbeResult result) : ITestDatabaseProbe
    {
        public int Calls { get; private set; }

        public Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private static readonly ProbeResult GoodProbe = new(Database, IPAddress.Loopback, RunId);

    private static TestTargetSettings Settings(bool allow = true, string? runId = RunId, string? database = Database) =>
        new(allow, runId, database);

    private static async Task<(SpyActions Spy, FakeProbe Probe, Exception? Error)> Run(TestTargetSettings settings, ProbeResult probeResult)
    {
        var spy = new SpyActions();
        var probe = new FakeProbe(probeResult);
        var error = await Record.ExceptionAsync(() => TestDatabaseGuard.PrepareAsync(settings, probe, spy, TestContext.Current.CancellationToken));
        return (spy, probe, error);
    }

    [Fact]
    public async Task Missing_destructive_opt_in_is_rejected_before_probing()
    {
        var (spy, probe, error) = await Run(Settings(allow: false), GoodProbe);
        Assert.IsType<UnsafeTestTargetException>(error);
        Assert.Equal(0, probe.Calls);
        Assert.Equal(0, spy.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("SHORT")]
    [InlineData("has-dash-123")]
    public async Task Invalid_run_id_is_rejected(string? runId)
    {
        var (spy, _, error) = await Run(Settings(runId: runId), GoodProbe);
        Assert.IsType<UnsafeTestTargetException>(error);
        Assert.Equal(0, spy.Calls);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("lst_test_shared")]
    [InlineData("lst_app")]
    public async Task Connection_database_must_be_run_specific(string database)
    {
        var (spy, probe, error) = await Run(Settings(database: database), GoodProbe);
        Assert.IsType<UnsafeTestTargetException>(error);
        Assert.Equal(0, probe.Calls);
        Assert.Equal(0, spy.Calls);
    }

    [Fact]
    public async Task Actual_current_database_mismatch_is_rejected()
    {
        var (spy, _, error) = await Run(Settings(), GoodProbe with { CurrentDatabase = "lst_app" });
        Assert.IsType<UnsafeTestTargetException>(error);
        Assert.Equal(0, spy.Calls);
    }

    [Fact]
    public async Task Missing_or_foreign_marker_is_rejected()
    {
        var (spy1, _, error1) = await Run(Settings(), GoodProbe with { MarkerRunId = null });
        var (spy2, _, error2) = await Run(Settings(), GoodProbe with { MarkerRunId = "otherrun12345" });
        Assert.IsType<UnsafeTestTargetException>(error1);
        Assert.IsType<UnsafeTestTargetException>(error2);
        Assert.Equal(0, spy1.Calls + spy2.Calls);
    }

    [Fact]
    public async Task Non_loopback_server_is_rejected()
    {
        var (spy, _, error) = await Run(Settings(), GoodProbe with { ServerAddress = IPAddress.Parse("10.0.0.15") });
        Assert.IsType<UnsafeTestTargetException>(error);
        Assert.Equal(0, spy.Calls);
    }

    [Fact]
    public async Task Fully_verified_target_runs_actions_once()
    {
        var (spy, probe, error) = await Run(Settings(), GoodProbe);
        Assert.Null(error);
        Assert.Equal(1, probe.Calls);
        Assert.Equal(1, spy.Calls);
    }

    [Fact]
    public void Settings_are_read_from_environment_and_connection_string()
    {
        var env = new Dictionary<string, string>
        {
            [TestDatabaseGuard.AllowDestructiveVariable] = "true",
            [TestDatabaseGuard.RunIdVariable] = RunId,
        };
        var settings = TestDatabaseGuard.FromEnvironment(
            k => env.GetValueOrDefault(k), $"Host=127.0.0.1;Database={Database};Username=u");
        Assert.Equal(Settings(), settings);
    }
}
