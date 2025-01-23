using System.Device.Gpio;
using System.Device.Gpio.Drivers;

using Iot.Device.Gpio.Drivers;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Voron.Joystick;

namespace Voron.Extensions.Service
{
    internal class Program
    {
        // How to run systemd services https://swimburger.net/blog/dotnet/how-to-run-a-dotnet-core-console-app-as-a-service-using-systemd-on-linux
        static void Main(string[] args)
        {
            IHost host = Host.CreateDefaultBuilder(args)
             .UseSystemd()
             .ConfigureServices(services =>
             {
                 services.AddSingleton(() => new GpioController(PinNumberingScheme.Board, new Sun50iw9p1Driver()));

                 services.AddJoystick();
             })
             .Build();
        }
    }
}
