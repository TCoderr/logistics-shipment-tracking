using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Lst.Platform.Idempotency;

public sealed record IdempotencyScope(string ActorScope, string Endpoint, string Key);

/// <summary>İlk yanıtın kayıpsız kopyası; gövde bir kez serialize edilmiş UTF-8 byte dizisidir.</summary>
public sealed record StoredResponse(int StatusCode, string ContentType, byte[] Body, string? Location, string? ETag);

public abstract record ClaimResult
{
    private ClaimResult()
    {
    }

    /// <summary>Bu transaction anahtarı sahiplendi; iş yapılıp <see cref="IdempotencyStore.CompleteAsync"/> çağrılmalı.</summary>
    public sealed record Claimed(Guid RecordId) : ClaimResult;

    /// <summary>Aynı kapsam + aynı istek hash'i ile tamamlanmış kayıt var.</summary>
    public sealed record Replay(StoredResponse Response) : ClaimResult;

    /// <summary>Aynı kapsam ve anahtar farklı istek (veya farklı hash sürümü) ile kullanılmış.</summary>
    public sealed record KeyReused : ClaimResult;

    /// <summary>Sınırlı deneme içinde kayıt sahiplenilemedi; istemci aynı anahtarla tekrar denemeli.</summary>
    public sealed record Busy : ClaimResult;
}

/// <summary>
/// Kalıcı idempotency claim protokolü (docs/05 §3, Karar değişikliği Dilim 1).
/// Çağıran açık bir READ COMMITTED transaction ve <c>SET LOCAL lock_timeout</c> sağlar.
/// Lock timeout (55P03) transaction'ı abort eder; çağıran rollback yapıp 409 üretir.
/// </summary>
public static class IdempotencyStore
{
    public const string TableName = "idempotency_records";
    public const string UniqueIndexName = "ux_idempotency_records_scope_key";
    private const int MaxClaimAttempts = 3;

    public static async Task<ClaimResult> ClaimAsync(
        DbContext db,
        IdempotencyScope scope,
        byte[] requestHash,
        short requestHashVersion,
        DateTimeOffset nowUtc,
        TimeSpan retention,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(requestHash);

        for (var attempt = 0; attempt < MaxClaimAttempts; attempt++)
        {
            var recordId = Guid.CreateVersion7(nowUtc);

            // Aynı anahtarı commit edilmemiş başka bir transaction eklediyse bu INSERT onun bitmesini bekler
            // (lock_timeout ile sınırlı). O commit ederse çakışma olur ve satır dönmez; rollback ederse bizim satırımız eklenir.
            if (await TryInsertAsync(db, recordId, scope, requestHash, requestHashVersion, nowUtc, nowUtc + retention, cancellationToken))
            {
                return new ClaimResult.Claimed(recordId);
            }

            // Çakışan kayıt commit edilmiş. READ COMMITTED'da yeni statement yeni snapshot alır; satırı kilitleyerek oku.
            var existing = await ReadForUpdateAsync(db, scope, cancellationToken);
            if (existing is null)
            {
                // Satır, biz beklerken eşzamanlı bir reclaim tarafından silindi; INSERT'ü yeni snapshot ile tekrar dene.
                continue;
            }

            if (existing.ExpiresAt <= nowUtc)
            {
                // Süresi dolmuş kayıt: satır kilidi bizde; sil ve aynı transaction'da yeniden claim et.
                await DeleteAsync(db, existing.Id, cancellationToken);
                continue;
            }

            if (existing.RequestHashVersion != requestHashVersion
                || !CryptographicOperations.FixedTimeEquals(existing.RequestHash, requestHash))
            {
                return new ClaimResult.KeyReused();
            }

            if (existing.Response is null)
            {
                // Ertelenmiş trigger nedeniyle tamamlanmamış kayıt commit edilemez; savunma amaçlı.
                return new ClaimResult.Busy();
            }

            return new ClaimResult.Replay(existing.Response);
        }

        return new ClaimResult.Busy();
    }

    public static async Task CompleteAsync(
        DbContext db,
        Guid recordId,
        StoredResponse response,
        Guid? resourceId,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(response);

        await using var command = CreateCommand(db,
            $"""
            UPDATE platform.{TableName}
               SET response_status_code = @status,
                   response_content_type = @content_type,
                   response_body = @body,
                   response_location = @location,
                   response_etag = @etag,
                   resource_id = @resource_id,
                   completed_at = @completed_at
             WHERE id = @id
            """);
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Smallint) { Value = checked((short)response.StatusCode) });
        command.Parameters.Add(new NpgsqlParameter("content_type", NpgsqlDbType.Varchar) { Value = response.ContentType });
        command.Parameters.Add(new NpgsqlParameter("body", NpgsqlDbType.Bytea) { Value = response.Body });
        command.Parameters.Add(new NpgsqlParameter("location", NpgsqlDbType.Varchar) { Value = (object?)response.Location ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("etag", NpgsqlDbType.Varchar) { Value = (object?)response.ETag ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("resource_id", NpgsqlDbType.Uuid) { Value = (object?)resourceId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("completed_at", NpgsqlDbType.TimestampTz) { Value = completedAtUtc });
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = recordId });

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            throw new InvalidOperationException("Idempotency kaydı tamamlanamadı: claim satırı bulunamadı.");
        }
    }

    private static async Task<bool> TryInsertAsync(
        DbContext db,
        Guid recordId,
        IdempotencyScope scope,
        byte[] requestHash,
        short requestHashVersion,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(db,
            $"""
            INSERT INTO platform.{TableName}
                (id, actor_scope, endpoint, idempotency_key, request_hash, request_hash_version, created_at, expires_at)
            VALUES (@id, @actor_scope, @endpoint, @key, @hash, @hash_version, @created_at, @expires_at)
            ON CONFLICT (actor_scope, endpoint, idempotency_key) DO NOTHING
            RETURNING id
            """);
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = recordId });
        AddScopeParameters(command, scope);
        command.Parameters.Add(new NpgsqlParameter("hash", NpgsqlDbType.Bytea) { Value = requestHash });
        command.Parameters.Add(new NpgsqlParameter("hash_version", NpgsqlDbType.Smallint) { Value = requestHashVersion });
        command.Parameters.Add(new NpgsqlParameter("created_at", NpgsqlDbType.TimestampTz) { Value = createdAt });
        command.Parameters.Add(new NpgsqlParameter("expires_at", NpgsqlDbType.TimestampTz) { Value = expiresAt });

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is Guid;
    }

    private static async Task<ExistingRecord?> ReadForUpdateAsync(DbContext db, IdempotencyScope scope, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(db,
            $"""
            SELECT id, request_hash, request_hash_version, expires_at,
                   response_status_code, response_content_type, response_body, response_location, response_etag
              FROM platform.{TableName}
             WHERE actor_scope = @actor_scope AND endpoint = @endpoint AND idempotency_key = @key
               FOR UPDATE
            """);
        AddScopeParameters(command, scope);

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        StoredResponse? response = null;
        if (!await reader.IsDBNullAsync(4, cancellationToken))
        {
            response = new StoredResponse(
                reader.GetInt16(4),
                reader.GetString(5),
                reader.GetFieldValue<byte[]>(6),
                await reader.IsDBNullAsync(7, cancellationToken) ? null : reader.GetString(7),
                await reader.IsDBNullAsync(8, cancellationToken) ? null : reader.GetString(8));
        }

        return new ExistingRecord(
            reader.GetGuid(0),
            reader.GetFieldValue<byte[]>(1),
            reader.GetInt16(2),
            reader.GetFieldValue<DateTimeOffset>(3),
            response);
    }

    private static async Task DeleteAsync(DbContext db, Guid recordId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(db, $"DELETE FROM platform.{TableName} WHERE id = @id");
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = recordId });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddScopeParameters(NpgsqlCommand command, IdempotencyScope scope)
    {
        command.Parameters.Add(new NpgsqlParameter("actor_scope", NpgsqlDbType.Varchar) { Value = scope.ActorScope });
        command.Parameters.Add(new NpgsqlParameter("endpoint", NpgsqlDbType.Varchar) { Value = scope.Endpoint });
        command.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Varchar) { Value = scope.Key });
    }

    private static NpgsqlCommand CreateCommand(DbContext db, string sql)
    {
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Idempotency işlemleri açık bir transaction gerektirir.");
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
#pragma warning disable CA2100 // SQL metni sabittir; değerler yalnız parametre ile verilir.
        return new NpgsqlCommand(sql, connection, (NpgsqlTransaction)transaction.GetDbTransaction());
#pragma warning restore CA2100
    }

    private sealed record ExistingRecord(
        Guid Id,
        byte[] RequestHash,
        short RequestHashVersion,
        DateTimeOffset ExpiresAt,
        StoredResponse? Response);
}
