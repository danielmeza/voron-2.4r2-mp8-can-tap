using System.Diagnostics;
using System.Globalization;

using Microsoft.Extensions.Logging;

using Voron.Joystick.Input;
using Voron.Moonraker;

namespace Voron.Joystick
{
    /// <summary>
    /// Turns a stream of stick samples into toolhead motion.
    ///
    /// Klipper has no jog command, so a held direction becomes a stream of short absolute G1 moves.
    /// Consecutive collinear segments coast straight through Klipper's lookahead, which is what makes
    /// the motion continuous rather than a series of visible steps. Two things keep it honest:
    ///
    ///   * the target is integrated here, never read back mid-jog, so rounding cannot accumulate;
    ///   * only <see cref="JogMotionOptions.LookaheadSeconds"/> of motion is ever queued, because a
    ///     move handed to Klipper cannot be recalled — queued time is exactly the delay between
    ///     letting go of the stick and the toolhead stopping.
    /// </summary>
    internal sealed class JogController(
        PrinterLink printer,
        JoystickOptions options,
        ILogger<JogController> log)
    {
        private const string StateName = "voron_joystick_jog";

        private readonly Stopwatch _clock = Stopwatch.StartNew();

        /// <summary>Length of one emitted move, matched to the sample rate.</summary>
        private readonly double _segmentSeconds = 1d / Math.Clamp(options.SampleRateHz, 5, 500);

        private bool _sessionActive;
        private Vec3 _target;
        private Vec3 _lastSent;
        private double _pendingSeconds;
        private double _queuedUntilSeconds;
        private double _idleSeconds;
        private bool _awaitingRecenter;
        private bool _warnedNotHomed;
        private bool _warnedPrinting;
        private bool _warnedNoLimits;

        /// <summary>True while the toolhead is under joystick control.</summary>
        public bool IsJogging => _sessionActive;

        /// <summary>Handles one sample. Called once per loop tick; may block while the printer homes.</summary>
        public async Task ProcessAsync(JogInput input, double elapsedSeconds, CancellationToken cancellationToken)
        {
            var snapshot = printer.Snapshot;

            if (!printer.IsOnline)
            {
                ResetSession();
                return;
            }

            // Never while printing. print_stats.state is the right signal here: idle_timeout.state
            // reads "Printing" for any gcode activity, including the jog moves we issue ourselves.
            if (snapshot.IsPrinting)
            {
                ResetSession();

                if (input.IsNeutral)
                {
                    _warnedPrinting = false;
                }
                else if (!_warnedPrinting)
                {
                    _warnedPrinting = true;
                    log.LogWarning("Joystick ignored: a print is {State}.", snapshot.PrintState);
                }

                return;
            }

            if (!snapshot.HasLimits)
            {
                ResetSession();
                if (!_warnedNoLimits)
                {
                    _warnedNoLimits = true;
                    log.LogWarning("Joystick idle: Klipper has not reported axis limits yet.");
                }

                return;
            }

            _warnedNoLimits = false;

            if (!snapshot.IsHomed)
            {
                ResetSession();
                await HandleUnhomedAsync(input, cancellationToken).ConfigureAwait(false);
                return;
            }

            _warnedNotHomed = false;

            if (_awaitingRecenter)
            {
                if (input.IsNeutral)
                {
                    _awaitingRecenter = false;
                    log.LogInformation("Stick centred; joystick jog is live.");
                }

                return;
            }

            if (input.IsNeutral)
            {
                if (!_sessionActive)
                {
                    return;
                }

                _idleSeconds += elapsedSeconds;
                if (_idleSeconds >= options.Motion.SessionIdleSeconds)
                {
                    try
                    {
                        await EndSessionAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // The session is already closed locally; a failed restore must not stop
                        // the loop, or one dropped websocket would end jogging until a restart.
                        log.LogWarning(ex, "Could not restore gcode state after jogging.");
                    }
                }

                return;
            }

            _idleSeconds = 0d;

            try
            {
                if (!_sessionActive)
                {
                    await BeginSessionAsync(snapshot, cancellationToken).ConfigureAwait(false);
                }

                await StepAsync(input, snapshot, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Klipper refused a move, or the link dropped mid-session. Drop the session so the
                // next deflection re-reads the real position instead of continuing from a stale target.
                log.LogWarning(ex, "Jog move failed; ending the jog session.");
                ResetSession();
            }
        }

        /// <summary>Stops jogging and, if a session is open, hands gcode state back to the printer.</summary>
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_sessionActive)
            {
                try
                {
                    await EndSessionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log.LogDebug(ex, "Ignoring error while closing the jog session during shutdown.");
                }
            }

            ResetSession();
        }

        private async Task HandleUnhomedAsync(JogInput input, CancellationToken cancellationToken)
        {
            if (input.IsNeutral)
            {
                _warnedNotHomed = false;
                return;
            }

            if (!options.Safety.AutoHome)
            {
                if (!_warnedNotHomed)
                {
                    _warnedNotHomed = true;
                    log.LogWarning("Joystick ignored: the printer is not homed and auto-home is off.");
                }

                return;
            }

            log.LogInformation("Joystick moved while unhomed; running {Gcode}.", options.Safety.HomeGcode);

            try
            {
                await printer.RunGcodeAsync(
                    options.Safety.HomeGcode,
                    cancellationToken,
                    TimeSpan.FromSeconds(options.Safety.HomeTimeoutSeconds)).ConfigureAwait(false);

                _awaitingRecenter = options.Safety.RequireRecenterAfterHome;
                log.LogInformation(
                    _awaitingRecenter
                        ? "Homing finished. Centre the stick to start jogging."
                        : "Homing finished.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Auto-home failed.");

                // Back off so a stuck switch cannot spam G28 at the sample rate.
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task BeginSessionAsync(PrinterSnapshot snapshot, CancellationToken cancellationToken)
        {
            var position = await printer.QueryGcodePositionAsync(cancellationToken).ConfigureAwait(false)
                ?? snapshot.GcodePosition;

            _target = position;
            _lastSent = position;
            _pendingSeconds = 0d;
            _queuedUntilSeconds = _clock.Elapsed.TotalSeconds;

            // Absolute positioning, with the printer's own state saved so a jog cannot leave the
            // machine in relative mode. RESTORE_GCODE_STATE defaults to MOVE=0, so it never moves.
            await printer.RunGcodeAsync($"SAVE_GCODE_STATE NAME={StateName}\nG90", cancellationToken)
                .ConfigureAwait(false);

            _sessionActive = true;
            log.LogInformation("Jog session started at {Position}.", position);
        }

        private async Task EndSessionAsync(CancellationToken cancellationToken)
        {
            _sessionActive = false;
            _idleSeconds = 0d;
            _pendingSeconds = 0d;

            await printer.RunGcodeAsync($"RESTORE_GCODE_STATE NAME={StateName}", cancellationToken)
                .ConfigureAwait(false);

            log.LogInformation("Jog session ended at {Position}.", _lastSent);
        }

        private async Task StepAsync(
            JogInput input,
            PrinterSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            var motion = options.Motion;
            var scale = input.Precision ? Math.Clamp(motion.PrecisionFactor, 0.01d, 1d) : 1d;

            // A stick pushed into a corner would otherwise ask for sqrt(2) times the configured speed.
            var planar = input.PlanarMagnitude;
            var (x, y) = planar > 1d ? (input.X / planar, input.Y / planar) : (input.X, input.Y);

            var velocity = new Vec3(
                x * motion.MaxSpeedXY * scale,
                y * motion.MaxSpeedXY * scale,
                input.Z * motion.MaxSpeedZ * scale);

            if (velocity.Length <= 0d)
            {
                return;
            }

            var now = _clock.Elapsed.TotalSeconds;
            if (_queuedUntilSeconds < now)
            {
                // The queue drained — a hiccup, or the first move of a session. Resync so the lead
                // is measured from now rather than from an ever-growing deficit.
                _queuedUntilSeconds = now;
            }

            // Emit whole segments until Klipper is holding LookaheadSeconds of motion. On the first
            // tick of a session that means several moves at once, which is what fills the lookahead
            // and keeps the toolhead from decelerating at the end of every segment.
            var segment = _segmentSeconds;
            var queuedEnd = _queuedUntilSeconds + _pendingSeconds;
            var budget = Math.Max(2, (int)Math.Ceiling(motion.LookaheadSeconds / segment) + 2);

            while (queuedEnd - now <= motion.LookaheadSeconds && budget-- > 0)
            {
                var candidate = Clamp(_target + (velocity * segment), snapshot);
                if ((candidate - _target).Length <= 0d)
                {
                    // Pinned against a soft limit; no motion time to account for.
                    break;
                }

                _target = candidate;
                _pendingSeconds += segment;
                queuedEnd += segment;

                var distance = (_target - _lastSent).Length;
                if (distance < motion.MinSegmentMm)
                {
                    // Creeping too slowly to be worth a move of its own; roll it into the next one.
                    continue;
                }

                var script = string.Format(
                    CultureInfo.InvariantCulture,
                    "G1 X{0:0.###} Y{1:0.###} Z{2:0.###} F{3:0.#}",
                    _target.X,
                    _target.Y,
                    _target.Z,
                    distance / _pendingSeconds * 60d);

                await printer.RunGcodeAsync(script, cancellationToken).ConfigureAwait(false);

                _queuedUntilSeconds += _pendingSeconds;
                _pendingSeconds = 0d;
                _lastSent = _target;
            }
        }

        private Vec3 Clamp(Vec3 target, PrinterSnapshot snapshot)
        {
            var margin = Math.Max(0d, options.Safety.EdgeMarginMm);
            var min = snapshot.GcodeAxisMinimum;
            var max = snapshot.GcodeAxisMaximum;

            var floorZ = Math.Max(min.Z + margin, options.Safety.MinZ);
            var ceilingZ = options.Safety.MaxZ is { } configured
                ? Math.Min(max.Z - margin, configured)
                : max.Z - margin;

            return new Vec3(
                ClampAxis(target.X, min.X + margin, max.X - margin),
                ClampAxis(target.Y, min.Y + margin, max.Y - margin),
                ClampAxis(target.Z, floorZ, ceilingZ));
        }

        private static double ClampAxis(double value, double low, double high) =>
            high <= low ? low : Math.Clamp(value, low, high);

        private void ResetSession()
        {
            _sessionActive = false;
            _pendingSeconds = 0d;
            _idleSeconds = 0d;
        }
    }
}
