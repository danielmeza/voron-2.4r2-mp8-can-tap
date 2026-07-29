using System.Device.Gpio;
using System.Text;

using Microsoft.Extensions.Logging;

namespace Voron.Joystick.Input
{
    internal enum JoyButton
    {
        XPlus,
        XMinus,
        YPlus,
        YMinus,
        ZPlus,
        ZMinus,
        Precision,
        ModeToggle,
    }

    /// <summary>
    /// Opens the configured switch pins and turns them into debounced, active-low aware states.
    /// Shared by the digital stick and by the buttons that can sit alongside an analog stick.
    /// </summary>
    internal sealed class GpioButtonBank : IDisposable
    {
        private readonly GpioController _gpio;
        private readonly bool _activeLow;
        private readonly int _debounceSamples;
        private readonly ILogger _log;
        private readonly Dictionary<JoyButton, Entry> _entries = [];

        public GpioButtonBank(
            GpioController gpio,
            DigitalPinOptions pins,
            bool activeLow,
            bool usePullUp,
            int debounceSamples,
            ILogger log)
        {
            _gpio = gpio;
            _activeLow = activeLow;
            _debounceSamples = Math.Max(1, debounceSamples);
            _log = log;

            foreach (var (button, name) in Enumerate(pins))
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (!Cb1Pin.TryParse(name, out var pin, out var error))
                {
                    throw new InvalidOperationException($"Joystick pin '{button}': {error}");
                }

                var mode = usePullUp ? PinMode.InputPullUp : PinMode.Input;
                if (usePullUp && !_gpio.IsPinModeSupported(pin, PinMode.InputPullUp))
                {
                    _log.LogWarning(
                        "{Pin} does not support an internal pull-up; falling back to a plain input. Fit an external pull-up resistor.",
                        Cb1Pin.Describe(pin));
                    mode = PinMode.Input;
                }

                _gpio.OpenPin(pin, mode);
                _entries[button] = new Entry(pin);
            }
        }

        public bool IsConfigured(JoyButton button) => _entries.ContainsKey(button);

        public bool HasAny => _entries.Count > 0;

        /// <summary>Debounced state: true while the switch is closed.</summary>
        public bool IsPressed(JoyButton button) => _entries.TryGetValue(button, out var entry) && entry.Stable;

        /// <summary>True once per press, on the sample where the switch first settled closed.</summary>
        public bool WasPressed(JoyButton button) => _entries.TryGetValue(button, out var entry) && entry.RisingEdge;

        /// <summary>Reads every configured pin once. Call at the start of each loop iteration.</summary>
        public void Sample()
        {
            foreach (var entry in _entries.Values)
            {
                var level = _gpio.Read(entry.Pin);
                var pressed = _activeLow ? level == PinValue.Low : level == PinValue.High;
                entry.Update(pressed, _debounceSamples);
            }
        }

        public string DescribeRaw()
        {
            if (_entries.Count == 0)
            {
                return "no button pins configured";
            }

            var builder = new StringBuilder();
            foreach (var (button, entry) in _entries.OrderBy(pair => pair.Key))
            {
                builder.Append(button)
                    .Append('=')
                    .Append(entry.Stable ? "PRESSED" : "open")
                    .Append('(')
                    .Append(Cb1Pin.Describe(entry.Pin))
                    .Append(") ");
            }

            return builder.ToString().TrimEnd();
        }

        public string DescribePins()
        {
            return _entries.Count == 0
                ? "none"
                : string.Join(", ", _entries.OrderBy(p => p.Key).Select(p => $"{p.Key}={Cb1Pin.Describe(p.Value.Pin)}"));
        }

        private static IEnumerable<(JoyButton Button, string? Pin)> Enumerate(DigitalPinOptions pins)
        {
            yield return (JoyButton.XPlus, pins.XPlus);
            yield return (JoyButton.XMinus, pins.XMinus);
            yield return (JoyButton.YPlus, pins.YPlus);
            yield return (JoyButton.YMinus, pins.YMinus);
            yield return (JoyButton.ZPlus, pins.ZPlus);
            yield return (JoyButton.ZMinus, pins.ZMinus);
            yield return (JoyButton.Precision, pins.Precision);
            yield return (JoyButton.ModeToggle, pins.ModeToggle);
        }

        public void Dispose()
        {
            foreach (var entry in _entries.Values)
            {
                try
                {
                    _gpio.ClosePin(entry.Pin);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Ignoring error while closing {Pin}.", Cb1Pin.Describe(entry.Pin));
                }
            }

            _entries.Clear();
        }

        private sealed class Entry(int pin)
        {
            private int _streak;

            public int Pin { get; } = pin;

            public bool Stable { get; private set; }

            public bool RisingEdge { get; private set; }

            public void Update(bool pressed, int debounceSamples)
            {
                RisingEdge = false;

                if (pressed == Stable)
                {
                    _streak = 0;
                    return;
                }

                if (++_streak < debounceSamples)
                {
                    return;
                }

                _streak = 0;
                Stable = pressed;
                RisingEdge = pressed;
            }
        }
    }
}
