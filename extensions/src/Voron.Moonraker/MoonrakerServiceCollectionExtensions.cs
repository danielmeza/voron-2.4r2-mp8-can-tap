using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Voron.Moonraker
{
    public static class MoonrakerServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the shared printer link. Safe to call more than once — every extension that needs
        /// Klipper access can ask for it, and they all share the one websocket.
        /// </summary>
        public static IServiceCollection AddMoonraker(this IServiceCollection services, IConfiguration configuration)
        {
            if (services.Any(descriptor => descriptor.ServiceType == typeof(PrinterLink)))
            {
                return services;
            }

            services.Configure<MoonrakerOptions>(configuration.GetSection(MoonrakerOptions.SectionName));

            services.AddSingleton<PrinterLink>();
            services.AddHostedService(provider => provider.GetRequiredService<PrinterLink>());

            return services;
        }
    }
}
