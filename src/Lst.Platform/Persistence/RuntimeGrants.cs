using System.Text.RegularExpressions;

namespace Lst.Platform.Persistence;

/// <summary>
/// Çalışma zamanı DB rolü yetkileri (db/runtime-role-grants.sql, gömülü kaynak). Migration'ı uygulayan sahip rol bu betiği
/// çalıştırır; uygulama yalnız bu yetkilere sahip ayrı rolle bağlanır (audit için INSERT/SELECT).
/// </summary>
public static partial class RuntimeGrants
{
    private const string ResourceName = "runtime-role-grants.sql";

    public static string Sql(string runtimeRole)
    {
        if (!RoleNameRegex().IsMatch(runtimeRole))
        {
            throw new ArgumentException("Rol adı küçük harf, rakam ve '_' içermeli (en fazla 63 karakter).", nameof(runtimeRole));
        }

        using var stream = typeof(RuntimeGrants).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("runtime-role-grants.sql gömülü kaynağı bulunamadı.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("{{runtime_role}}", $"\"{runtimeRole}\"", StringComparison.Ordinal);
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex RoleNameRegex();
}
