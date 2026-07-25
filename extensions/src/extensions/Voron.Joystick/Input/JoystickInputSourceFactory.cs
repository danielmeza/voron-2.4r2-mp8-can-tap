using System.Device.Gpio;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Voron.Moonraker;

namespace Voron.Joystick.Input
{
    /// <summary>
    /// Builds the configured input source. The GPIO controller is resolved lazily so a host with no
    /// sunxi hardware (a laptop, say) can still start the service with Source=None.
    /// </summary>
    internal sealed class JoystickInputSourceFactory(
        IOptions<JoystickOptions> options,
        Func<GpioController> gpioFactory,
        PrinterLink printer,
        ILoggerFactory loggerFactory)
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly JoystickOptions _options = options.Value;

        public IJoystickInputSource? Create()
        {
            switch (_options.Source)
            {
                case JoystickSourceKind.None:
                    return null;

                case JoystickSourceKind.Digital:
                    return new DigitalJoystickSource(
                        gpioFactory(),
                        _options,
                        loggerFactory.CreateLogger<DigitalJoystickSource>());

                case JoystickSourceKind.Analog:
                    return CreateAnalog();

                case JoystickSourceKind.Mock:
                    return new MockJoystickSource(
                        _options.Mock,
                        DescribePrinterAsJson,
                        _options.ZMode != ZAxisMode.None,
                        loggerFactory.CreateLogger<MockJoystickSource>());

                default:
                    throw new InvalidOperationException($"Unknown joystick source '{_options.Source}'.");
            }
        }

        /// <summary>
        /// Live printer state for the virtual joystick's status panel, so the page can say why it
        /// is refusing to move rather than just doing nothing.
        /// </summary>
        private string DescribePrinterAsJson()
        {
            var snapshot = printer.Snapshot;

            return JsonSerializer.Serialize(
                new
                {
                    connected = printer.IsOnline,
                    klippyReady = snapshot.KlippyReady,
                    printState = snapshot.PrintState,
                    printing = snapshot.IsPrinting,
                    homed = snapshot.IsHomed,
                    homedAxes = snapshot.HomedAxes,
                    position = snapshot.GcodePosition.ToString(),
                    zEnabled = _options.ZMode != ZAxisMode.None,
                },
                JsonOptions);
        }

        /// <summary>Opens just the ADC, for the calibration helper.</summary>
        public IAnalogInput CreateAnalogInput()
        {
            var analog = _options.Analog;
            if (analog.Device == AnalogDeviceKind.Ads1115)
            {
                return new Ads1115AnalogInput(analog.I2c);
            }

            var gpio = analog.Spi.Transport == SpiTransport.Software ? gpioFactory() : null;
            return new McpAnalogInput(analog.Spi, analog.Device, gpio);
        }

        private IJoystickInputSource CreateAnalog()
        {
            var analog = _options.Analog;
            var needsGpio = analog.Device != AnalogDeviceKind.Ads1115
                && analog.Spi.Transport == SpiTransport.Software;

            var wantsButtons = _options.ZMode is ZAxisMode.Buttons or ZAxisMode.ToggleWithY
                || analog.Pins.Precision is not null
                || analog.Pins.ModeToggle is not null;

            GpioController? gpio = needsGpio || wantsButtons ? gpioFactory() : null;

            IAnalogInput adc = analog.Device == AnalogDeviceKind.Ads1115
                ? new Ads1115AnalogInput(analog.I2c)
                : new McpAnalogInput(analog.Spi, analog.Device, gpio);

            GpioButtonBank? buttons = null;
            if (wantsButtons && gpio is not null)
            {
                buttons = new GpioButtonBank(
                    gpio,
                    analog.Pins,
                    _options.Digital.ActiveLow,
                    _options.Digital.UsePullUp,
                    _options.Digital.DebounceSamples,
                    loggerFactory.CreateLogger<GpioButtonBank>());
            }

            return new AnalogJoystickSource(adc, _options, buttons);
        }
    }
}
