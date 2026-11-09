using System.Device.Gpio;
using System.Device.Spi;

using Iot.Device.Adc;
using Iot.Device.Spi;

namespace Voron.Joystick.Input
{
    /// <summary>
    /// MCP3008 (10-bit) or MCP3208 (12-bit) over SPI. The bus can be a real spidev node or four
    /// bit-banged GPIO pins, which is the escape hatch when the CB1 image exposes no /dev/spidev.
    /// </summary>
    internal sealed class McpAnalogInput : IAnalogInput
    {
        private readonly SpiDevice _spi;
        private readonly Mcp3xxx _adc;
        private readonly string _description;

        public McpAnalogInput(SpiOptions options, AnalogDeviceKind kind, GpioController? gpio)
        {
            var settings = new SpiConnectionSettings(options.BusId, options.ChipSelectLine)
            {
                ClockFrequency = options.ClockHz,
                Mode = SpiMode.Mode0,
            };

            if (options.Transport == SpiTransport.Software)
            {
                if (gpio is null)
                {
                    throw new InvalidOperationException("Bit-banged SPI needs a GPIO controller.");
                }

                var clock = RequirePin(options.ClockPin, nameof(options.ClockPin));
                var miso = RequirePin(options.MisoPin, nameof(options.MisoPin));
                var mosi = RequirePin(options.MosiPin, nameof(options.MosiPin));
                var chipSelect = RequirePin(options.ChipSelectPin, nameof(options.ChipSelectPin));

                // The chip select pin is driven explicitly, so keep it out of the connection settings.
                settings.ChipSelectLine = -1;
                _spi = new SoftwareSpi(clock, miso, mosi, chipSelect, settings, gpio, shouldDispose: false);
                _description =
                    $"{kind} on bit-banged SPI (CLK {Cb1Pin.Describe(clock)}, MISO {Cb1Pin.Describe(miso)}, "
                    + $"MOSI {Cb1Pin.Describe(mosi)}, CS {Cb1Pin.Describe(chipSelect)})";
            }
            else
            {
                _spi = SpiDevice.Create(settings);
                _description = $"{kind} on /dev/spidev{options.BusId}.{options.ChipSelectLine} at {options.ClockHz} Hz";
            }

            (_adc, MaxRaw) = kind switch
            {
                AnalogDeviceKind.Mcp3008 => ((Mcp3xxx)new Mcp3008(_spi), 1023),
                AnalogDeviceKind.Mcp3208 => (new Mcp3208(_spi), 4095),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not an MCP ADC."),
            };
        }

        public string Description => _description;

        public int MaxRaw { get; }

        public int ChannelCount => 8;

        public int Read(int channel) => _adc.Read(channel);

        private static int RequirePin(string? value, string name)
        {
            if (!Cb1Pin.TryParse(value, out var pin, out var error))
            {
                throw new InvalidOperationException($"Joystick:Analog:Spi:{name} is required for bit-banged SPI. {error}");
            }

            return pin;
        }

        public void Dispose()
        {
            _adc.Dispose();
            _spi.Dispose();
        }
    }
}
