using System.Device.I2c;

using Iot.Device.Ads1115;

namespace Voron.Joystick.Input
{
    /// <summary>
    /// ADS1115 over I2C. Reads are single-shot so that switching the input multiplexer between
    /// channels cannot hand back the previous channel's conversion.
    /// </summary>
    internal sealed class Ads1115AnalogInput : IAnalogInput
    {
        private static readonly InputMultiplexer[] Channels =
        [
            InputMultiplexer.AIN0,
            InputMultiplexer.AIN1,
            InputMultiplexer.AIN2,
            InputMultiplexer.AIN3,
        ];

        private readonly I2cDevice _device;
        private readonly Ads1115 _adc;
        private readonly I2cOptions _options;

        public Ads1115AnalogInput(I2cOptions options)
        {
            _options = options;

            if (!Enum.TryParse<DataRate>(options.DataRate, ignoreCase: true, out var dataRate))
            {
                throw new InvalidOperationException(
                    $"'{options.DataRate}' is not an ADS1115 data rate. Use one of: {string.Join(", ", Enum.GetNames<DataRate>())}.");
            }

            if (!Enum.TryParse<MeasuringRange>(options.MeasuringRange, ignoreCase: true, out var range))
            {
                throw new InvalidOperationException(
                    $"'{options.MeasuringRange}' is not an ADS1115 range. Use one of: {string.Join(", ", Enum.GetNames<MeasuringRange>())}.");
            }

            _device = I2cDevice.Create(new I2cConnectionSettings(options.BusId, options.Address));
            _adc = new Ads1115(_device, InputMultiplexer.AIN0, range, dataRate, DeviceMode.PowerDown);
        }

        public string Description =>
            $"ADS1115 on /dev/i2c-{_options.BusId} at 0x{_options.Address:X2} ({_options.MeasuringRange}, {_options.DataRate})";

        /// <summary>The ADS1115 is 16-bit signed; a single-ended reading spans 0..32767.</summary>
        public int MaxRaw => short.MaxValue;

        public int ChannelCount => 4;

        public int Read(int channel)
        {
            if (channel is < 0 or > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(channel), channel, "The ADS1115 has channels 0-3.");
            }

            return _adc.ReadRaw(Channels[channel]);
        }

        public void Dispose()
        {
            _adc.Dispose();
            _device.Dispose();
        }
    }
}
