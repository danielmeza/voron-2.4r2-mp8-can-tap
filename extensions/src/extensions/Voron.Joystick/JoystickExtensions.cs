using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;

namespace Voron.Joystick
{
    public static class JoystickExtensions
    {
        public static IServiceCollection AddJoystick(this IServiceCollection services)
        {
            return services.AddHostedService<JoystickWorker>();
        }
    }
}
