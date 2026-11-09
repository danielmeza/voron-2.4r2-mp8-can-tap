using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Voron.Joystick.Input;
using Voron.Moonraker;

namespace Voron.Joystick
{
    public static class JoystickExtensions
    {
        /// <summary>
        /// Registers joystick jog control. Requires a <c>Func&lt;GpioController&gt;</c> in the container,
        /// which the host provides so the GPIO driver choice stays a host concern.
        /// </summary>
        public static IServiceCollection AddJoystick(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<JoystickOptions>(configuration.GetSection(JoystickOptions.SectionName));
            services.AddMoonraker(configuration);

            services.AddSingleton<JoystickInputSourceFactory>();
            services.AddHostedService<JoystickWorker>();

            return services;
        }
    }
}
