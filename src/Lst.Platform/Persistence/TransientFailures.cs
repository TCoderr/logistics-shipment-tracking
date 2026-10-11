using System.Net.Sockets;
using Npgsql;

namespace Lst.Platform.Persistence;

/// <summary>
/// Worker'lar için hata sınıflandırması: transient hatalar (bağlantı/ağ, kaynak, kapanış, kilit zaman aşımı, serileştirme/deadlock)
/// deneme bütçesini tüketmez ve dead-letter üretmez; diğer tüm hatalar kalıcı sayılır.
/// </summary>
public static class TransientFailures
{
    public static bool IsTransient(Exception exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            switch (ex)
            {
                case PostgresException pg:
                    return pg.SqlState.StartsWith("08", StringComparison.Ordinal)
                        || pg.SqlState.StartsWith("53", StringComparison.Ordinal)
                        || pg.SqlState.StartsWith("57P", StringComparison.Ordinal)
                        || pg.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected
                            or PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.QueryCanceled;
                case NpgsqlException npgsql when npgsql.IsTransient:
                case TimeoutException:
                case SocketException:
                case IOException:
                    return true;
            }
        }

        return false;
    }
}
