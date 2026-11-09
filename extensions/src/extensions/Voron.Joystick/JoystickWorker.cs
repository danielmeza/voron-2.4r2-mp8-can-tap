using System.Diagnostics;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Voron.Joystick.Input;
using Voron.Moonraker;

namespace Voron.Joystick
{
    /// <summary>
    /// Samples the joystick at a fixed rate and feeds <see cref="JogController"/>. Nothing here knows
    /// what the hardware is; that is entirely behind <see cref="IJoystickInputSource"/>.
    /// </summary>
    internal sealed class JoystickWorker : BackgroundService
    {
        /// <summary>Longest tick the integrator will honour, so a stall cannot become a lurch.</summary>
        private const double MaxTickSeconds = 0.25;

        private const int MaxConsecutiveReadFailures = 5;

        private readonly JoystickInputSourceFactory _sourceFactory;
        private readonly PrinterLink _printer;
        private readonly JoystickOptions _options;
        private readonly ILogger<JoystickWorker> _log;
        private readonly ILoggerFactory _loggerFactory;

        public JoystickWorker(
            JoystickInputSourceFactory sourceFactory,
            PrinterLink printer,
            IOptions<JoystickOptions> options,
            ILogger<JoystickWorker> log,
            ILoggerFactory loggerFactory)
        {
            _sourceFactory = sourceFactory;
            _printer = printer;
            _options = options.Value;
            _log = log;
            _loggerFactory = loggerFactory;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.Enabled || _options.Source == JoystickSourceKind.None)
            {
                _log.LogInformation("Joystick jog is disabled (Source={Source}).", _options.Source);
                return;
            }

            IJoystickInputSource source;
            try
            {
                source = _sourceFactory.Create()
                    ?? throw new InvalidOperationException("No joystick source was created.");
                source.Open();
            }
            catch (Exception ex)
            {
                // Bad wiring or a missing bus must not take the whole service down with it.
                _log.LogError(ex, "Joystick hardware could not be opened; jog control is unavailable.");
                return;
            }

            _log.LogInformation("Joystick ready: {Description}", source.Description);

            var controller = new JogController(_printer, _options, _loggerFactory.CreateLogger<JogController>());
            var period = TimeSpan.FromSeconds(1d / Math.Clamp(_options.SampleRateHz, 5, 500));

            using var timer = new PeriodicTimer(period);
            var clock = Stopwatch.StartNew();
            var previous = clock.Elapsed.TotalSeconds;
            var failures = 0;
            var controllerFailures = 0;

            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    var now = clock.Elapsed.TotalSeconds;
                    var elapsed = Math.Min(now - previous, MaxTickSeconds);
                    previous = now;

                    if (elapsed <= 0d)
                    {
                        continue;
                    }

                    JogInput input;
                    try
                    {
                        input = source.Read(elapsed);
                        failures = 0;
                    }
                    catch (Exception ex)
                    {
                        if (++failures >= MaxConsecutiveReadFailures)
                        {
                            _log.LogError(ex, "Joystick hardware stopped responding; stopping jog control.");
                            break;
                        }

                        _log.LogWarning(ex, "Joystick read failed ({Count}/{Max}).", failures, MaxConsecutiveReadFailures);
                        continue;
                    }

                    try
                    {
                        await controller.ProcessAsync(input, elapsed, stoppingToken).ConfigureAwait(false);
                        controllerFailures = 0;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        if (++controllerFailures >= MaxConsecutiveReadFailures)
                        {
                            _log.LogError(ex, "The jog loop failed repeatedly; stopping jog control.");
                            break;
                        }

                        _log.LogWarning(ex, "Jog tick failed ({Count}/{Max}).", controllerFailures, MaxConsecutiveReadFailures);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
            finally
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await controller.StopAsync(shutdown.Token).ConfigureAwait(false);
                source.Dispose();
                _log.LogInformation("Joystick jog stopped.");
            }
        }
    }
}
