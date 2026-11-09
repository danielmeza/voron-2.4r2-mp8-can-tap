using System.Globalization;
using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Voron.Joystick.Input;
using Voron.Moonraker;

namespace Voron.Joystick
{
    /// <summary>
    /// Wiring self-tests. These talk to the hardware only — no printer connection, no motion — so
    /// they are safe to run while Klipper is up.
    /// </summary>
    public static class JoystickDiagnostics
    {
        /// <summary>Streams raw pin states or ADC counts so wiring can be checked before anything moves.</summary>
        public static async Task<int> ProbeAsync(IServiceProvider services, CancellationToken cancellationToken)
        {
            var factory = services.GetRequiredService<JoystickInputSourceFactory>();

            IJoystickInputSource? source;
            try
            {
                source = factory.Create();
                if (source is null)
                {
                    Console.Error.WriteLine("Joystick:Source is None — nothing to probe.");
                    return 1;
                }

                source.Open();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not open the joystick hardware: {ex.Message}");
                return 1;
            }

            using (source)
            {
                Console.WriteLine(source.Description);
                Console.WriteLine("Move the stick and press every button. Ctrl+C to stop.");
                Console.WriteLine();

                var width = 0;
                while (!cancellationToken.IsCancellationRequested)
                {
                    string line;
                    try
                    {
                        line = source.DescribeRaw();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine();
                        Console.Error.WriteLine($"Read failed: {ex.Message}");
                        return 1;
                    }

                    width = Math.Max(width, line.Length);
                    Console.Write('\r');
                    Console.Write(line.PadRight(width));

                    await SafeDelayAsync(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                }

                Console.WriteLine();
            }

            return 0;
        }

        /// <summary>
        /// Records the travel of each ADC channel and prints an appsettings block to paste back.
        /// Start with the stick released: the reading at rest is taken as the centre.
        /// </summary>
        public static async Task<int> CalibrateAsync(IServiceProvider services, CancellationToken cancellationToken)
        {
            var factory = services.GetRequiredService<JoystickInputSourceFactory>();

            IAnalogInput adc;
            try
            {
                adc = factory.CreateAnalogInput();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not open the ADC: {ex.Message}");
                Console.Error.WriteLine("Calibration only applies to Joystick:Source=Analog. For switch sticks use --probe.");
                return 1;
            }

            using (adc)
            {
                Console.WriteLine(adc.Description);
                Console.WriteLine("Leave the stick centred for a moment, then sweep every axis to both extremes.");
                Console.WriteLine("Ctrl+C when done — the calibration block is printed on exit.");
                Console.WriteLine();

                var channels = adc.ChannelCount;
                var centre = new double[channels];
                var min = new int[channels];
                var max = new int[channels];
                var current = new int[channels];

                for (var channel = 0; channel < channels; channel++)
                {
                    var sample = adc.Read(channel);
                    centre[channel] = sample;
                    min[channel] = sample;
                    max[channel] = sample;
                }

                var settled = 0;
                while (!cancellationToken.IsCancellationRequested)
                {
                    for (var channel = 0; channel < channels; channel++)
                    {
                        var sample = adc.Read(channel);
                        current[channel] = sample;
                        min[channel] = Math.Min(min[channel], sample);
                        max[channel] = Math.Max(max[channel], sample);

                        // The first second, with the stick untouched, defines the centre.
                        if (settled < 10)
                        {
                            centre[channel] += (sample - centre[channel]) * 0.25;
                        }
                    }

                    settled++;
                    Render(current, min, max, centre);
                    await SafeDelayAsync(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                }

                PrintSuggestion(channels, min, max, centre);
            }

            return 0;
        }

        /// <summary>
        /// Drives the jog loop with a fixed stick deflection, bypassing the hardware entirely.
        /// The toolhead really moves: this is how you confirm direction mapping and speed before
        /// trusting a stick you have just wired.
        /// </summary>
        public static async Task<int> SimulateAsync(
            IServiceProvider services,
            string axis,
            double seconds,
            CancellationToken cancellationToken)
        {
            var input = axis.ToLowerInvariant() switch
            {
                "x+" => new JogInput(1d, 0d, 0d, false),
                "x-" => new JogInput(-1d, 0d, 0d, false),
                "y+" => new JogInput(0d, 1d, 0d, false),
                "y-" => new JogInput(0d, -1d, 0d, false),
                "z+" => new JogInput(0d, 0d, 1d, false),
                "z-" => new JogInput(0d, 0d, -1d, false),
                _ => JogInput.Neutral,
            };

            if (input.IsNeutral)
            {
                Console.Error.WriteLine("Usage: --simulate <x+|x-|y+|y-|z+|z-> <seconds>");
                return 1;
            }

            var options = services.GetRequiredService<IOptions<JoystickOptions>>().Value;
            var loggerFactory = services.GetRequiredService<ILoggerFactory>();
            var printer = services.GetRequiredService<PrinterLink>();

            Console.WriteLine($"Jogging {axis} for {seconds:0.##}s. The toolhead will move. Ctrl+C to stop.");

            await printer.StartAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!await WaitForPrinterAsync(printer, cancellationToken).ConfigureAwait(false))
                {
                    Console.Error.WriteLine("The printer link did not come up.");
                    return 1;
                }

                var controller = new JogController(printer, options, loggerFactory.CreateLogger<JogController>());
                var period = TimeSpan.FromSeconds(1d / Math.Clamp(options.SampleRateHz, 5, 500));
                var elapsed = period.TotalSeconds;

                using var timer = new PeriodicTimer(period);
                var ticks = (int)Math.Max(1, seconds / elapsed);

                for (var tick = 0; tick < ticks && !cancellationToken.IsCancellationRequested; tick++)
                {
                    await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
                    await controller.ProcessAsync(input, elapsed, cancellationToken).ConfigureAwait(false);
                }

                // Return to centre so the session closes the way it would in normal use.
                var settleTicks = (int)((options.Motion.SessionIdleSeconds + 0.5d) / elapsed);
                for (var tick = 0; tick < settleTicks && !cancellationToken.IsCancellationRequested; tick++)
                {
                    await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
                    await controller.ProcessAsync(JogInput.Neutral, elapsed, cancellationToken).ConfigureAwait(false);
                }

                await controller.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Interrupted.");
            }
            finally
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await printer.StopAsync(shutdown.Token).ConfigureAwait(false);
            }

            return 0;
        }

        private static async Task<bool> WaitForPrinterAsync(PrinterLink printer, CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < 60 && !cancellationToken.IsCancellationRequested; attempt++)
            {
                if (printer.IsOnline)
                {
                    return true;
                }

                await SafeDelayAsync(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }

            return printer.IsOnline;
        }

        private static void Render(int[] current, int[] min, int[] max, double[] centre)
        {
            var builder = new StringBuilder();
            builder.AppendLine("channel   current    centre       min       max     travel");
            for (var channel = 0; channel < current.Length; channel++)
            {
                builder.Append("    ch").Append(channel).Append("  ")
                    .Append(current[channel].ToString(CultureInfo.InvariantCulture).PadLeft(8))
                    .Append("  ").Append(((int)centre[channel]).ToString(CultureInfo.InvariantCulture).PadLeft(8))
                    .Append("  ").Append(min[channel].ToString(CultureInfo.InvariantCulture).PadLeft(8))
                    .Append("  ").Append(max[channel].ToString(CultureInfo.InvariantCulture).PadLeft(8))
                    .Append("  ").Append((max[channel] - min[channel]).ToString(CultureInfo.InvariantCulture).PadLeft(9))
                    .AppendLine();
            }

            if (!Console.IsOutputRedirected)
            {
                // Redraw the table in place instead of scrolling the terminal.
                Console.SetCursorPosition(0, Math.Max(0, Console.CursorTop - (current.Length + 1)));
            }

            Console.Write(builder.ToString());
        }

        private static void PrintSuggestion(int channels, int[] min, int[] max, double[] centre)
        {
            var names = new[] { "X", "Y", "Z" };

            Console.WriteLine();
            Console.WriteLine("Paste into Joystick:Analog (adjust the channel numbers to match your wiring):");
            Console.WriteLine();

            for (var index = 0; index < names.Length && index < channels; index++)
            {
                var travel = max[index] - min[index];
                if (travel < 100)
                {
                    Console.WriteLine($"// ch{index} barely moved ({travel} counts) — not wired, or the axis was not swept.");
                    continue;
                }

                Console.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "\"{0}\": {{ \"Channel\": {1}, \"Min\": {2}, \"Center\": {3}, \"Max\": {4}, \"Deadband\": 0.08, \"Exponent\": 2.0, \"Invert\": false }},",
                    names[index],
                    index,
                    min[index],
                    (int)centre[index],
                    max[index]));
            }

            Console.WriteLine();
            Console.WriteLine("If an axis drives the toolhead the wrong way, set \"Invert\": true for it.");
        }

        private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C: fall through so the caller can print its summary.
            }
        }
    }
}
