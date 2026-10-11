using System.Data;
using System.Text.Json;
using Lst.Platform.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Lst.Modules.Shipments.Application;

/// <summary>
/// Durum değiştiren gönderi komutları için ortak transaction ve aggregate kilidi (ADR-014).
/// xmin tek başına dokunulmayan satırı kilitlemez; bu yüzden her komut gönderi satırını <c>SELECT … FOR UPDATE</c> ile kilitler ve
/// tekrar kontrolü + olay INSERT'ü aynı kilit altında yapar. Kilit beklemesi lock_timeout ile sınırlıdır.
/// </summary>
internal static class ShipmentCommandSupport
{
    public const string LockTimeoutSql = "SET LOCAL lock_timeout = '5s'";

    public static async Task<IDbContextTransaction> BeginAsync(LogisticsDbContext db, CancellationToken cancellationToken)
    {
        db.Database.AutoSavepointsEnabled = false;
        var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await db.Database.ExecuteSqlRawAsync(LockTimeoutSql, cancellationToken);
        return transaction;
    }

    /// <summary>Gönderi satırını transaction sonuna kadar kilitler. Satır yoksa false.</summary>
    public static async Task<bool> LockShipmentAsync(LogisticsDbContext db, Guid shipmentId, CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        await using var command = new NpgsqlCommand("SELECT 1 FROM shipments.shipments WHERE id = @id FOR UPDATE", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = shipmentId });
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <summary>
    /// Sıkı JSON gövde okuma (tanımsız/yinelenen alan, string sayı reddedilir); en fazla <paramref name="maxBytes"/>.
    /// Hata durumunda değer yankılanmaz, yalnız JSON yolu bildirilir.
    /// </summary>
    public static async Task<(T? Value, IResult? Error)> ReadStrictJsonAsync<T>(HttpRequest request, int maxBytes, CancellationToken cancellationToken)
        where T : class
    {
        if (!JsonContent.IsJson(request.ContentType))
        {
            return (null, Lst.Platform.Http.Problems.Create(StatusCodes.Status415UnsupportedMediaType, "unsupported-media-type",
                "İçerik tipi desteklenmiyor", "İstek gövdesi application/json olmalı."));
        }

        var body = await JsonContent.ReadBodyAsync(request, maxBytes, cancellationToken);
        if (body is null)
        {
            return (null, Lst.Platform.Http.Problems.Create(StatusCodes.Status413PayloadTooLarge, "payload-too-large",
                "İstek gövdesi çok büyük", $"İstek gövdesi en fazla {maxBytes / 1024} KB olabilir."));
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(body, ShipmentsJson.StrictRequest);
            return value is null
                ? (null, Lst.Platform.Http.Problems.Validation(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["$"] = ["Gövde zorunludur."] }))
                : (value, null);
        }
        catch (JsonException ex)
        {
            return (null, Lst.Platform.Http.Problems.Validation(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [string.IsNullOrEmpty(ex.Path) ? "$" : ex.Path] =
                    ["Gövde geçerli JSON değil, tanımsız veya yinelenen alan ya da yanlış tipte değer içeriyor."],
            }));
        }
    }
}

internal static class JsonContent
{
    public static bool IsJson(string? contentType) =>
        Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
        && mediaType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
        && (!mediaType.Charset.HasValue || mediaType.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase));

    public static async Task<byte[]?> ReadBodyAsync(HttpRequest request, int limit, CancellationToken cancellationToken)
    {
        if (request.ContentLength > limit)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
