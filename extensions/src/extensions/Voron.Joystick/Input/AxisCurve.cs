namespace Voron.Joystick.Input
{
    /// <summary>Turns raw ADC counts into a -1..1 command using the axis calibration.</summary>
    internal static class AxisCurve
    {
        public static double Normalize(double raw, AnalogAxisOptions axis)
        {
            double value;
            if (raw >= axis.Center)
            {
                var span = axis.Max - axis.Center;
                value = span <= 0 ? 0d : (raw - axis.Center) / span;
            }
            else
            {
                var span = axis.Center - axis.Min;
                value = span <= 0 ? 0d : (raw - axis.Center) / span;
            }

            value = Math.Clamp(value, -1d, 1d);
            if (axis.Invert)
            {
                value = -value;
            }

            var deadband = Math.Clamp(axis.Deadband, 0d, 0.95d);
            var magnitude = Math.Abs(value);
            if (magnitude <= deadband)
            {
                return 0d;
            }

            // Rescale so the first millimetre of travel past the deadband starts from zero speed
            // rather than jumping straight to the deadband fraction.
            magnitude = (magnitude - deadband) / (1d - deadband);

            var exponent = axis.Exponent <= 0d ? 1d : axis.Exponent;
            magnitude = Math.Pow(magnitude, exponent);

            return Math.Sign(value) * magnitude;
        }
    }
}
