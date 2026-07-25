namespace Voron.Joystick.Input
{
    /// <summary>An external ADC. Keeps the analog stick source independent of I2C vs SPI parts.</summary>
    public interface IAnalogInput : IDisposable
    {
        string Description { get; }

        /// <summary>Highest count the converter can report, for scaling and diagnostics.</summary>
        int MaxRaw { get; }

        /// <summary>Number of single-ended channels the part exposes.</summary>
        int ChannelCount { get; }

        int Read(int channel);
    }
}
