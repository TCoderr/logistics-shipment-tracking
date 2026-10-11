using Npgsql;

namespace Lst.Platform.Persistence;

/// <summary>Yalnız beklenen SQLSTATE değerleri eşleştirilir; diğer hatalar yeniden fırlatılır.</summary>
public static class PostgresErrors
{
    public const string UniqueViolation = "23505";
    public const string LockNotAvailable = "55P03";

    public static PostgresException? Find(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException pg)
            {
                return pg;
            }
        }

        return null;
    }

    public static bool IsLockTimeout(Exception exception) =>
        Find(exception)?.SqlState == LockNotAvailable;

    public static bool IsUniqueViolation(Exception exception, string constraintName)
    {
        var pg = Find(exception);
        return pg is not null
            && pg.SqlState == UniqueViolation
            && string.Equals(pg.ConstraintName, constraintName, StringComparison.Ordinal);
    }
}
