using System.Text;

using Microsoft.Extensions.Logging;

namespace Voron.Joystick.Input
{
    /// <summary>
    /// Potentiometer stick read through an external ADC. Proportional in every axis: how far you
    /// push is how fast it goes. The Z channel can be a twist axis or a spring-return lever — the
    /// only requirement is that it self-centres, or the toolhead would creep forever.
    /// </summary>
    internal sealed class AnalogJoystickSource : IJoystickInputSource
    {
        private readonly IAnalogInput _adc;
        private readonly JoystickOptions _options;
        private readonly AnalogJoystickOptions _analog;
        private readonly GpioButtonBank? _buttons;
        private readonly double[] _filtered = new double[3];
        private readonly bool[] _primed = new bool[3];

        private bool _zModeActive;

        public AnalogJoystickSource(
            IAnalogInput adc,
            JoystickOptions options,
            GpioButtonBank? buttons)
        {
            _adc = adc;
            _options = options;
            _analog = options.Analog;
            _buttons = buttons;
        }

        public string Description
        {
            get
            {
                var axes = new List<string>();
                if (_analog.X.IsEnabled)
                {
                    axes.Add($"X=ch{_analog.X.Channel}");
                }

                if (_analog.Y.IsEnabled)
                {
                    axes.Add($"Y=ch{_analog.Y.Channel}");
                }

                if (_options.ZMode == ZAxisMode.Analog && _analog.Z.IsEnabled)
                {
                    axes.Add($"Z=ch{_analog.Z.Channel}");
                }

                var buttons = _buttons is { HasAny: true } ? $", buttons [{_buttons.DescribePins()}]" : string.Empty;
                return $"{_adc.Description}, {string.Join(" ", axes)}{buttons}";
            }
        }

        public void Open()
        {
            if (!_analog.X.IsEnabled && !_analog.Y.IsEnabled)
            {
                throw new InvalidOperationException(
                    "The analog joystick source has no calibrated axes. Run the service with --calibrate and fill in "
                    + "Joystick:Analog:X/Y (Min, Center, Max).");
            }
        }

        public JogInput Read(double elapsedSeconds)
        {
            _buttons?.Sample();

            if (_options.ZMode == ZAxisMode.ToggleWithY && _buttons is not null && _buttons.WasPressed(JoyButton.ModeToggle))
            {
                _zModeActive = !_zModeActive;
            }

            var x = ReadAxis(0, _analog.X);
            var y = ReadAxis(1, _analog.Y);
            var z = 0d;

            switch (_options.ZMode)
            {
                case ZAxisMode.Analog:
                    z = ReadAxis(2, _analog.Z);
                    break;

                case ZAxisMode.Buttons when _buttons is not null:
                    z = ButtonDirection(_buttons, JoyButton.ZPlus, JoyButton.ZMinus);
                    break;

                case ZAxisMode.ToggleWithY when _zModeActive:
                    z = y;
                    x = 0d;
                    y = 0d;
                    break;
            }

            var precision = _buttons?.IsPressed(JoyButton.Precision) ?? false;
            return new JogInput(x, y, z, precision);
        }

        public string DescribeRaw()
        {
            var builder = new StringBuilder();
            AppendChannel(builder, "X", _analog.X);
            AppendChannel(builder, "Y", _analog.Y);
            AppendChannel(builder, "Z", _analog.Z);

            if (_buttons is { HasAny: true })
            {
                _buttons.Sample();
                builder.Append(" | ").Append(_buttons.DescribeRaw());
            }

            return builder.ToString();
        }

        private void AppendChannel(StringBuilder builder, string label, AnalogAxisOptions axis)
        {
            if (axis.Channel < 0)
            {
                return;
            }

            var raw = _adc.Read(axis.Channel);
            builder.Append(label)
                .Append("(ch")
                .Append(axis.Channel)
                .Append(")=")
                .Append(raw.ToString().PadLeft(6))
                .Append(" -> ")
                .Append(AxisCurve.Normalize(raw, axis).ToString("+0.000;-0.000; 0.000"))
                .Append("  ");
        }

        private double ReadAxis(int slot, AnalogAxisOptions axis)
        {
            if (!axis.IsEnabled)
            {
                return 0d;
            }

            var raw = (double)_adc.Read(axis.Channel);

            var alpha = Math.Clamp(_analog.SmoothingFactor, 0.01d, 1d);
            if (!_primed[slot])
            {
                _filtered[slot] = raw;
                _primed[slot] = true;
            }
            else
            {
                _filtered[slot] += alpha * (raw - _filtered[slot]);
            }

            return AxisCurve.Normalize(_filtered[slot], axis);
        }

        private static double ButtonDirection(GpioButtonBank buttons, JoyButton positive, JoyButton negative)
        {
            var up = buttons.IsPressed(positive);
            var down = buttons.IsPressed(negative);
            if (up == down)
            {
                return 0d;
            }

            return up ? 1d : -1d;
        }

        public void Dispose()
        {
            _buttons?.Dispose();
            _adc.Dispose();
        }
    }
}
