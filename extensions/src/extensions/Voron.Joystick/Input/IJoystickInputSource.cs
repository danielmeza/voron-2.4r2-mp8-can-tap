namespace Voron.Joystick.Input
{
    /// <summary>
    /// A source of jog input. Implementations own their bus or pins and are polled by the worker;
    /// everything hardware-specific — debouncing, calibration, response curves — stops here.
    /// </summary>
    public interface IJoystickInputSource : IDisposable
    {
        /// <summary>Human-readable summary, logged once at startup.</summary>
        string Description { get; }

        /// <summary>Opens pins and buses. Throws if the hardware is not reachable.</summary>
        void Open();

        /// <summary>
        /// Reads the current stick position.
        /// </summary>
        /// <param name="elapsedSeconds">Time since the previous read, used by ramped sources.</param>
        JogInput Read(double elapsedSeconds);

        /// <summary>Raw hardware values for the wiring self-test, e.g. pin states or ADC counts.</summary>
        string DescribeRaw();
    }
}
