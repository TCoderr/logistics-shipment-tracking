using Microsoft.AspNetCore.DataProtection;

namespace Lst.Api.Hosting;

/// <summary>Web host ve bakım komutlarının ortak yapılandırması.</summary>
internal static class HostConfiguration
{
    /// <summary>Docker/Compose secret dizinleri. Dosya adı yapılandırma anahtarıdır ("__" bölüm ayırıcı).</summary>
    public static readonly string[] SecretDirectories = ["/run/secrets", "/run/lst-seed"];

    /// <summary>
    /// Secret'lar ortam değişkeni yerine salt-okunur dosyalardan okunur (docker inspect'te görünmez).
    /// Dizin yoksa (yerel `dotnet run`, testler) etkisizdir.
    /// </summary>
    public static void AddSecretFiles(IConfigurationBuilder configuration)
    {
        foreach (var directory in SecretDirectories)
        {
            configuration.AddKeyPerFile(directory, optional: true, reloadOnChange: false);
        }
    }

    /// <summary>
    /// DataProtection:KeysPath tanımlıysa anahtarlar kalıcı dizinde tutulur; API konteyneri yeniden oluşturulunca
    /// auth/antiforgery cookie'leri geçerli kalır. Tanımlı değilse platform varsayılanı kullanılır.
    /// </summary>
    public static void AddLstDataProtection(IServiceCollection services, IConfiguration configuration)
    {
        var builder = services.AddDataProtection().SetApplicationName("lst");
        var keysPath = configuration["DataProtection:KeysPath"];
        if (!string.IsNullOrWhiteSpace(keysPath))
        {
            builder.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        }
    }
}
