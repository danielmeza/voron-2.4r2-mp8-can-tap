using System.Device.Gpio;

using Microsoft.Extensions.Logging;

namespace Voron.Joystick.Input
{
    /// <summary>
    /// Switch-per-direction sticks — a Sanwa JLF-TP-8YT and the like. The four microswitches are
    /// on/off, so speed comes from a ramp instead: a tap creeps, a held direction winds up to full
    /// speed. That keeps fine positioning possible without giving up fast traverses.
    /// </summary>
    internal sealed class DigitalJoystickSource : IJoystickInputSource
    {
        private readonly GpioController _gpio;
        private readonly JoystickOptions _options;
        private readonly DigitalJoystickOptions _digital;
        private readonly ILogger _log;

        private GpioButtonBank? _buttons;
        private double _planarHeldSeconds;
        private double _zHeldSeconds;
        private int _planarSignature;
        private int _zSignature;
        private bool _zModeActive;

        public DigitalJoystickSource(GpioController gpio, JoystickOptions options, ILogger log)
        {
            _gpio = gpio;
            _options = options;
            _digital = options.Digital;
            _log = log;
        }

        public string Description =>
            $"digital switches [{_buttons?.DescribePins() ?? "not open"}], "
            + $"ramp {_digital.RampStartFraction:0.##}->1.0 over {_digital.RampSeconds:0.##}s";

        public void Open()
        {
            _buttons = new GpioButtonBank(
                _gpio,
                _digital.Pins,
                _digital.ActiveLow,
                _digital.UsePullUp,
                _digital.DebounceSamples,
                _log);

            if (!_buttons.HasAny)
            {
                throw new InvalidOperationException(
                    "The digital joystick source has no pins configured. Set Joystick:Digital:Pins in appsettings.json.");
            }
        }

        public JogInput Read(double elapsedSeconds)
        {
            var buttons = _buttons ?? throw new InvalidOperationException("Open() was not called.");
            buttons.Sample();

            if (_options.ZMode == ZAxisMode.ToggleWithY && buttons.WasPressed(JoyButton.ModeToggle))
            {
                _zModeActive = !_zModeActive;
                _log.LogInformation("Joystick mode switched to {Mode}.", _zModeActive ? "Z" : "XY");
            }

            var x = Direction(buttons, JoyButton.XPlus, JoyButton.XMinus);
            var y = Direction(buttons, JoyButton.YPlus, JoyButton.YMinus);
            var z = 0;

            switch (_options.ZMode)
            {
                case ZAxisMode.Buttons:
                    z = Direction(buttons, JoyButton.ZPlus, JoyButton.ZMinus);
                    break;

                case ZAxisMode.ToggleWithY when _zModeActive:
                    z = y;
                    x = 0;
                    y = 0;
                    break;
            }

            var planarSignature = ((x + 1) * 3) + (y + 1);
            _planarHeldSeconds = planarSignature == _planarSignature && planarSignature != 4
                ? _planarHeldSeconds + elapsedSeconds
                : 0d;
            _planarSignature = planarSignature;

            _zHeldSeconds = z == _zSignature && z != 0 ? _zHeldSeconds + elapsedSeconds : 0d;
            _zSignature = z;

            var planarScale = RampFactor(_planarHeldSeconds);
            var zScale = RampFactor(_zHeldSeconds);

            return new JogInput(
                x * planarScale,
                y * planarScale,
                z * zScale,
                buttons.IsPressed(JoyButton.Precision));
        }

        public string DescribeRaw()
        {
            var buttons = _buttons;
            if (buttons is null)
            {
                return "not open";
            }

            buttons.Sample();
            return buttons.DescribeRaw();
        }

        private double RampFactor(double heldSeconds)
        {
            if (_digital.RampSeconds <= 0d)
            {
                return 1d;
            }

            var start = Math.Clamp(_digital.RampStartFraction, 0.01d, 1d);
            var progress = Math.Clamp(heldSeconds / _digital.RampSeconds, 0d, 1d);
            return start + ((1d - start) * progress);
        }

        private static int Direction(GpioButtonBank buttons, JoyButton positive, JoyButton negative)
        {
            var forward = buttons.IsPressed(positive);
            var back = buttons.IsPressed(negative);

            // Both switches closed means a wiring fault or a stick bottomed against its gate; stand still.
            if (forward == back)
            {
                return 0;
            }

            return forward ? 1 : -1;
        }

        public void Dispose() => _buttons?.Dispose();
    }
}
