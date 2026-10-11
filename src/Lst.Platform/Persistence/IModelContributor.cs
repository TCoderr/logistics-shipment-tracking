using Microsoft.EntityFrameworkCore;

namespace Lst.Platform.Persistence;

/// <summary>
/// Modüllerin kendi entity konfigürasyonlarını ortak <see cref="LogisticsDbContext"/> modeline eklediği giriş noktası.
/// Platform modüllere derleme zamanında bağımlı olmaz; modüller bu arayüzü DI ile kaydeder.
/// </summary>
public interface IModelContributor
{
    void Configure(ModelBuilder modelBuilder);
}
