using System.Device.Gpio;
using System.Globalization;

using Iot.Device.Gpio.Drivers;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Voron.Joystick;

namespace Voron.Extensions.Service
{
    /// <summary>
    /// Host for the printer extensions. Runs as a systemd unit on the CB1 next to Klipper and
    /// Moonraker — see extensions/deploy/install.sh and
    /// https://swimburger.net/blog/dotnet/how-to-run-a-dotnet-core-console-app-as-a-service-using-systemd-on-linux
    /// </summary>
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            IHost host = Host.CreateDefaultBuilder(args)
                // Resolve appsettings.json next to the binary rather than from the launch directory,
                // so systemd and an interactive shell behave the same.
                .UseContentRoot(AppContext.BaseDirectory)
                .UseSystemd()
                .ConfigureServices((context, services) =>
                {
                    services.AddSingleton(_ => CreateGpioController(context.Configuration));

                    // Handed to the extensions so the controller is only built when a pin is opened;
                    // that keeps the host startable on hardware without a sunxi SoC.
                    services.AddSingleton<Func<GpioController>>(
                        provider => provider.GetRequiredService<GpioController>);

                    services.AddJoystick(context.Configuration);
                })
                .Build();

            if (HasFlag(args, "--probe"))
            {
                return await JoystickDiagnostics.ProbeAsync(host.Services, CreateConsoleToken()).ConfigureAwait(false);
            }

            if (HasFlag(args, "--calibrate"))
            {
                return await JoystickDiagnostics.CalibrateAsync(host.Services, CreateConsoleToken()).ConfigureAwait(false);
            }

            var simulate = Array.FindIndex(args, arg => string.Equals(arg, "--simulate", StringComparison.OrdinalIgnoreCase));
            if (simulate >= 0)
            {
                var axis = simulate + 1 < args.Length ? args[simulate + 1] : string.Empty;
                var seconds = simulate + 2 < args.Length
                    && double.TryParse(args[simulate + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                        ? parsed
                        : 1.0;

                return await JoystickDiagnostics
                    .SimulateAsync(host.Services, axis, seconds, CreateConsoleToken())
                    .ConfigureAwait(false);
            }

            await host.RunAsync().ConfigureAwait(false);
            return 0;
        }

        private static GpioController CreateGpioController(IConfiguration configuration)
        {
            var driver = configuration["Gpio:Driver"];

            // The CB1 is an Allwinner H616 (sun50iw9p1). Logical numbering means pin numbers match
            // the (bank - 'A') * 32 + index scheme used by the CB1 manual and by cb1.cfg.
            return string.Equals(driver, "Default", StringComparison.OrdinalIgnoreCase)
                ? new GpioController(PinNumberingScheme.Logical)
                : new GpioController(PinNumberingScheme.Logical, new Sun50iw9p1Driver());
        }

        private static bool HasFlag(string[] args, string flag) =>
            args.Any(arg => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase));

        private static CancellationToken CreateConsoleToken()
        {
            var source = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                source.Cancel();
            };

            return source.Token;
        }
    }
}
