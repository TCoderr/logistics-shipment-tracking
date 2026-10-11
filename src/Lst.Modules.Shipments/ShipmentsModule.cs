using Lst.Modules.Shipments.Application;
using Lst.Modules.Shipments.Domain;
using Lst.Modules.Shipments.Infrastructure;
using Lst.Platform.Persistence;
using Lst.Platform.Retention;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Lst.Modules.Shipments;

public static class ShipmentsModule
{
    public static IServiceCollection AddShipmentsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IModelContributor, ShipmentsModelContributor>();
        services.AddSingleton<IInterceptor, ShipmentOutboxInterceptor>();

        services.AddOptions<SlaOptions>()
            .Bind(configuration.GetSection(SlaOptions.SectionName))
            .Validate(o =>
            {
                try
                {
                    o.ToPolicy();
                    return true;
                }
#pragma warning disable CA1031 // Geçersiz konfigürasyon başlangıçta doğrulama hatasına çevrilir.
                catch (Exception)
#pragma warning restore CA1031
                {
                    return false;
                }
            }, "Sla konfigürasyonu geçersiz (TimeZoneId, CutoffLocalTime HH:mm, Holidays yyyy-MM-dd).")
            .ValidateOnStart();

        services.AddSingleton(sp => new PromisedDeliveryCalculator(sp.GetRequiredService<IOptions<SlaOptions>>().Value.ToPolicy()));
        services.AddSingleton<IShipmentCodeGenerator, RandomShipmentCodeGenerator>();
        services.AddScoped<CreateShipmentHandler>();
        services.AddScoped<GetShipmentDetailHandler>();
        services.AddScoped<ListShipmentsHandler>();
        services.AddScoped<RecordHubScanHandler>();
        services.AddScoped<ShipmentEventPublisher>();
        services.AddScoped<ShipmentOperatorCommands>();
        services.AddScoped<IShipmentOperations, ShipmentOperations>();
        services.AddScoped<RecordDeliveryAttemptHandler>();
        services.AddScoped<IShipmentCarrierEvents, ShipmentCarrierEvents>();
        services.AddScoped<PublicTrackingHandler>();
        services.AddScoped<RotatePublicTrackingCodeHandler>();
        services.AddScoped<IRetentionTask, ShipmentPiiRetentionTask>();
        return services;
    }
}
