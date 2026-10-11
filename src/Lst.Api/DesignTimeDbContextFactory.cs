using Lst.Modules.Identity;
using Lst.Modules.Operations;
using Lst.Modules.Shipments;
using Lst.Modules.Tracking;
using Lst.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Lst.Api;

/// <summary>
/// `dotnet ef migrations add/script` için. Bağlantı açılmaz; yer tutucu dize gizli bilgi içermez.
/// Model katkıları uygulamayla aynı modül kayıtlarından alınır.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LogisticsDbContext>
{
    public LogisticsDbContext CreateDbContext(string[] args)
    {
        var services = new ServiceCollection();
        services.AddIdentityModule();
        services.AddShipmentsModule(new ConfigurationBuilder().Build());
        services.AddOperationsModule();
        services.AddTrackingPersistence();
        using var provider = services.BuildServiceProvider();
        var contributors = provider.GetServices<IModelContributor>().ToList();

        var options = new DbContextOptionsBuilder<LogisticsDbContext>();
        PersistenceRegistration.Configure(options, "Host=design-time-only;Database=lst_design");
        return new LogisticsDbContext(options.Options, contributors);
    }
}
