using System.Text.Json;

namespace Voron.Moonraker
{
    /// <summary>
    /// Immutable view of the printer objects this service cares about. Moonraker only pushes the
    /// fields that changed, so updates are merged onto the previous snapshot.
    /// </summary>
    public sealed record PrinterSnapshot
    {
        public static readonly PrinterSnapshot Empty = new();

        /// <summary>True once klippy reports <c>ready</c> and the subscription is live.</summary>
        public bool KlippyReady { get; init; }

        /// <summary><c>print_stats.state</c>: standby, printing, paused, complete, cancelled or error.</summary>
        public string PrintState { get; init; } = "unknown";

        /// <summary><c>toolhead.homed_axes</c>, e.g. "xyz" or "" when nothing is homed.</summary>
        public string HomedAxes { get; init; } = string.Empty;

        /// <summary><c>gcode_move.gcode_position</c> — the coordinate space G1 commands address.</summary>
        public Vec3 GcodePosition { get; init; }

        /// <summary><c>toolhead.axis_minimum</c>, in toolhead coordinates.</summary>
        public Vec3 AxisMinimum { get; init; }

        /// <summary><c>toolhead.axis_maximum</c>, in toolhead coordinates.</summary>
        public Vec3 AxisMaximum { get; init; }

        /// <summary><c>gcode_move.homing_origin</c> — the offset between gcode and toolhead space.</summary>
        public Vec3 HomingOrigin { get; init; }

        /// <summary>
        /// A print is in progress or parked mid-print. Deliberately keyed off <c>print_stats.state</c>
        /// and not <c>idle_timeout.state</c>, which reads "Printing" for any gcode activity — including
        /// our own jog moves. See docs/adr/0005 for the same trap in the standby macros.
        /// </summary>
        public bool IsPrinting => PrintState is "printing" or "paused";

        public bool IsHomed =>
            HomedAxes.Contains('x', StringComparison.OrdinalIgnoreCase)
            && HomedAxes.Contains('y', StringComparison.OrdinalIgnoreCase)
            && HomedAxes.Contains('z', StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True once Klipper has reported real soft limits. Guards against treating an empty
        /// snapshot as a printer whose travel is zero in every direction.
        /// </summary>
        public bool HasLimits => AxisMaximum.X > AxisMinimum.X && AxisMaximum.Y > AxisMinimum.Y;

        /// <summary>Soft limits expressed in gcode coordinates, which is what jog targets use.</summary>
        public Vec3 GcodeAxisMinimum => AxisMinimum - HomingOrigin;

        public Vec3 GcodeAxisMaximum => AxisMaximum - HomingOrigin;

        /// <summary>Merges a Moonraker status object (full or partial) onto this snapshot.</summary>
        public PrinterSnapshot Merge(JsonElement status)
        {
            if (status.ValueKind != JsonValueKind.Object)
            {
                return this;
            }

            var result = this;

            if (status.TryGetProperty("toolhead", out var toolhead) && toolhead.ValueKind == JsonValueKind.Object)
            {
                if (toolhead.TryGetProperty("homed_axes", out var homed) && homed.ValueKind == JsonValueKind.String)
                {
                    result = result with { HomedAxes = homed.GetString() ?? string.Empty };
                }

                if (toolhead.TryGetProperty("axis_minimum", out var min))
                {
                    result = result with { AxisMinimum = Vec3.FromJsonArray(min) };
                }

                if (toolhead.TryGetProperty("axis_maximum", out var max))
                {
                    result = result with { AxisMaximum = Vec3.FromJsonArray(max) };
                }
            }

            if (status.TryGetProperty("gcode_move", out var gcodeMove) && gcodeMove.ValueKind == JsonValueKind.Object)
            {
                if (gcodeMove.TryGetProperty("gcode_position", out var position))
                {
                    result = result with { GcodePosition = Vec3.FromJsonArray(position) };
                }

                if (gcodeMove.TryGetProperty("homing_origin", out var origin))
                {
                    result = result with { HomingOrigin = Vec3.FromJsonArray(origin) };
                }
            }

            if (status.TryGetProperty("print_stats", out var printStats)
                && printStats.ValueKind == JsonValueKind.Object
                && printStats.TryGetProperty("state", out var state)
                && state.ValueKind == JsonValueKind.String)
            {
                result = result with { PrintState = state.GetString() ?? "unknown" };
            }

            return result;
        }
    }
}
