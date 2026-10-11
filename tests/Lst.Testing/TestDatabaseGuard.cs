using System.Net;
using System.Text.RegularExpressions;
using Npgsql;

namespace Lst.Testing;

/// <summary>
/// Yerel (Testcontainers dışı) test veritabanı için güvenli hedef kontrolü (Dilim 1 promptu §9.5).
/// Migration/seed/grant gibi yıkıcı işlemler, aşağıdakilerin TAMAMI doğrulanmadan çalıştırılmaz:
/// 1) LST_TEST_ALLOW_DESTRUCTIVE=true, 2) LST_TEST_RUN_ID biçimi, 3) bağlantı dizesindeki DB adı = lst_test_&lt;runId&gt;,
/// 4) sunucudaki gerçek current_database() aynı ad, 5) sunucu loopback, 6) lst_test_marker.run tablosundaki run_id = runId.
/// </summary>
public static partial class TestDatabaseGuard
{
    public const string AllowDestructiveVariable = "LST_TEST_ALLOW_DESTRUCTIVE";
    public const string RunIdVariable = "LST_TEST_RUN_ID";
    public const string DatabasePrefix = "lst_test_";

    public static TestTargetSettings FromEnvironment(Func<string, string?> environment, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(environment);
        string? database;
        try
        {
            database = new NpgsqlConnectionStringBuilder(connectionString).Database;
        }
        catch (ArgumentException)
        {
            database = null;
        }

        return new TestTargetSettings(
            string.Equals(environment(AllowDestructiveVariable), "true", StringComparison.Ordinal),
            environment(RunIdVariable),
            database);
    }

    public static async Task PrepareAsync(
        TestTargetSettings settings,
        ITestDatabaseProbe probe,
        IDestructiveDatabaseActions actions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(actions);

        if (!settings.AllowDestructive)
        {
            throw new UnsafeTestTargetException($"{AllowDestructiveVariable}=true açıkça verilmeden test veritabanına yazılmaz.");
        }

        if (settings.RunId is null || !RunIdRegex().IsMatch(settings.RunId))
        {
            throw new UnsafeTestTargetException($"{RunIdVariable} 8–32 karakter, küçük harf/rakam olmalı.");
        }

        var expectedDatabase = DatabasePrefix + settings.RunId;
        if (!string.Equals(settings.ConnectionDatabase, expectedDatabase, StringComparison.Ordinal))
        {
            throw new UnsafeTestTargetException("Bağlantı dizesindeki veritabanı adı bu koşunun beklenen adıyla eşleşmiyor.");
        }

        var observed = await probe.ProbeAsync(cancellationToken);
        if (!string.Equals(observed.CurrentDatabase, expectedDatabase, StringComparison.Ordinal))
        {
            throw new UnsafeTestTargetException("Sunucudaki current_database() beklenen test veritabanı değil.");
        }

        if (observed.ServerAddress is { } address && !IPAddress.IsLoopback(address))
        {
            throw new UnsafeTestTargetException("Yerel test hedefi yalnız loopback sunucu olabilir.");
        }

        if (!string.Equals(observed.MarkerRunId, settings.RunId, StringComparison.Ordinal))
        {
            throw new UnsafeTestTargetException("lst_test_marker.run işareti yok veya bu koşuya ait değil.");
        }

        await actions.PrepareAsync(cancellationToken);
    }

    [GeneratedRegex("^[a-z0-9]{8,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex RunIdRegex();
}

public sealed record TestTargetSettings(bool AllowDestructive, string? RunId, string? ConnectionDatabase);

/// <param name="ServerAddress">Unix soketinde NULL.</param>
public sealed record ProbeResult(string CurrentDatabase, IPAddress? ServerAddress, string? MarkerRunId);

public interface ITestDatabaseProbe
{
    Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>Migration, rol yetkileri ve seed: yalnız guard başarılı olduktan sonra çağrılır.</summary>
public interface IDestructiveDatabaseActions
{
    Task PrepareAsync(CancellationToken cancellationToken);
}

public sealed class UnsafeTestTargetException(string message) : Exception(message);

/// <summary>Salt okunur sorgu; hiçbir yazma yapmaz.</summary>
public sealed class NpgsqlTestDatabaseProbe(string connectionString) : ITestDatabaseProbe
{
    public async Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        string database;
        IPAddress? address;
        await using (var command = new NpgsqlCommand("SELECT current_database(), inet_server_addr()", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            database = reader.GetString(0);
            address = await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetFieldValue<IPAddress>(1);
        }

        string? marker;
        try
        {
            await using var markerCommand = new NpgsqlCommand("SELECT run_id FROM lst_test_marker.run LIMIT 1", connection);
            marker = await markerCommand.ExecuteScalarAsync(cancellationToken) as string;
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "3F000")
        {
            marker = null;
        }

        return new ProbeResult(database, address, marker);
    }
}
