namespace Voron.Joystick
{
    /// <summary>Which kind of hardware is feeding the jog loop.</summary>
    public enum JoystickSourceKind
    {
        /// <summary>No hardware; the worker stays idle. Useful to disable the extension without removing config.</summary>
        None = 0,

        /// <summary>Switch-per-direction sticks (Sanwa JLF and friends) plus optional arcade buttons.</summary>
        Digital,

        /// <summary>Potentiometer stick read through an external ADC.</summary>
        Analog,

        /// <summary>An on-screen joystick served over HTTP. No hardware; for testing and bring-up.</summary>
        Mock,
    }

    /// <summary>External ADC used by <see cref="JoystickSourceKind.Analog"/>.</summary>
    public enum AnalogDeviceKind
    {
        /// <summary>16-bit, 4 channels, I2C. Needs a real /dev/i2c-N on the CB1.</summary>
        Ads1115 = 0,

        /// <summary>10-bit, 8 channels, SPI.</summary>
        Mcp3008,

        /// <summary>12-bit, 8 channels, SPI.</summary>
        Mcp3208,
    }

    public enum SpiTransport
    {
        /// <summary>/dev/spidev&lt;bus&gt;.&lt;cs&gt;, which the CB1 image must expose.</summary>
        Hardware = 0,

        /// <summary>Bit-banged over four GPIO pins. Slower, but needs no device-tree overlay.</summary>
        Software,
    }

    /// <summary>Where the Z component of a jog comes from.</summary>
    public enum ZAxisMode
    {
        /// <summary>Z is never jogged.</summary>
        None = 0,

        /// <summary>A third analog channel — a twist axis or a spring-return lever.</summary>
        Analog,

        /// <summary>Two momentary buttons (arcade buttons work well).</summary>
        Buttons,

        /// <summary>A button toggles the stick between XY and Z; in Z mode the Y axis drives Z.</summary>
        ToggleWithY,
    }

    public sealed class JoystickOptions
    {
        public const string SectionName = "Joystick";

        public bool Enabled { get; set; } = true;

        public JoystickSourceKind Source { get; set; } = JoystickSourceKind.Digital;

        /// <summary>How often the stick is sampled and a move is emitted. 50 Hz is a good balance.</summary>
        public int SampleRateHz { get; set; } = 50;

        public ZAxisMode ZMode { get; set; } = ZAxisMode.Buttons;

        public DigitalJoystickOptions Digital { get; set; } = new();

        public AnalogJoystickOptions Analog { get; set; } = new();

        public MockJoystickOptions Mock { get; set; } = new();

        public JogMotionOptions Motion { get; set; } = new();

        public JogSafetyOptions Safety { get; set; } = new();
    }

    /// <summary>
    /// Pin names accept "PI14", "gpio270" or "270" — all three mean the same CB1 pin.
    /// Leave a pin null to disable that input.
    /// </summary>
    public sealed class DigitalPinOptions
    {
        public string? XPlus { get; set; }

        public string? XMinus { get; set; }

        public string? YPlus { get; set; }

        public string? YMinus { get; set; }

        public string? ZPlus { get; set; }

        public string? ZMinus { get; set; }

        /// <summary>Held down: move at <see cref="JogMotionOptions.PrecisionFactor"/> of normal speed.</summary>
        public string? Precision { get; set; }

        /// <summary>Pressed: flip between XY and Z when <see cref="ZAxisMode.ToggleWithY"/> is used.</summary>
        public string? ModeToggle { get; set; }
    }

    public sealed class DigitalJoystickOptions
    {
        public DigitalPinOptions Pins { get; set; } = new();

        /// <summary>Arcade microswitches short to ground, so a closed switch reads low.</summary>
        public bool ActiveLow { get; set; } = true;

        /// <summary>Use the SoC's internal pull-up. Turn off only if you fitted external pull-ups.</summary>
        public bool UsePullUp { get; set; } = true;

        /// <summary>Consecutive identical samples required before a button press is accepted.</summary>
        public int DebounceSamples { get; set; } = 2;

        /// <summary>Speed the instant a direction is pressed, as a fraction of the maximum.</summary>
        public double RampStartFraction { get; set; } = 0.12;

        /// <summary>How long a direction must be held to reach full speed. 0 disables the ramp.</summary>
        public double RampSeconds { get; set; } = 1.2;
    }

    /// <summary>Calibration for one potentiometer channel, in raw ADC counts.</summary>
    public sealed class AnalogAxisOptions
    {
        /// <summary>ADC channel, or -1 to disable this axis.</summary>
        public int Channel { get; set; } = -1;

        public int Min { get; set; }

        public int Center { get; set; }

        public int Max { get; set; }

        /// <summary>Fraction of travel around centre that reads as zero. Covers pot noise and slop.</summary>
        public double Deadband { get; set; } = 0.08;

        /// <summary>Response curve. 1 is linear; 2 gives fine control near centre.</summary>
        public double Exponent { get; set; } = 2.0;

        public bool Invert { get; set; }

        public bool IsEnabled => Channel >= 0 && Max != Min;
    }

    public sealed class I2cOptions
    {
        /// <summary>N in /dev/i2c-N.</summary>
        public int BusId { get; set; } = 1;

        /// <summary>0x48 with ADDR tied to GND.</summary>
        public int Address { get; set; } = 0x48;

        /// <summary>ADS1115 sample rate. Must exceed SampleRateHz times the number of channels.</summary>
        public string DataRate { get; set; } = "SPS475";

        /// <summary>Full-scale range. FS4096 covers a 3.3 V wiper.</summary>
        public string MeasuringRange { get; set; } = "FS4096";
    }

    public sealed class SpiOptions
    {
        public SpiTransport Transport { get; set; } = SpiTransport.Software;

        public int BusId { get; set; } = 1;

        public int ChipSelectLine { get; set; }

        public int ClockHz { get; set; } = 1_000_000;

        // Bit-banged pins, used when Transport is Software.
        public string? ClockPin { get; set; }

        public string? MisoPin { get; set; }

        public string? MosiPin { get; set; }

        public string? ChipSelectPin { get; set; }
    }

    public sealed class AnalogJoystickOptions
    {
        public AnalogDeviceKind Device { get; set; } = AnalogDeviceKind.Ads1115;

        public I2cOptions I2c { get; set; } = new();

        public SpiOptions Spi { get; set; } = new();

        public AnalogAxisOptions X { get; set; } = new();

        public AnalogAxisOptions Y { get; set; } = new();

        public AnalogAxisOptions Z { get; set; } = new();

        /// <summary>Exponential smoothing on the raw reading: 1 is no smoothing, lower is calmer.</summary>
        public double SmoothingFactor { get; set; } = 0.35;

        /// <summary>Optional buttons alongside the stick; the same pin rules as the digital source apply.</summary>
        public DigitalPinOptions Pins { get; set; } = new();
    }

    public sealed class MockJoystickOptions
    {
        /// <summary>
        /// HttpListener prefix; it must end with '/'. Loopback by default, because anything else
        /// hands toolhead control to the whole network. Use "http://+:8088/" to open it up.
        /// </summary>
        public string Prefix { get; set; } = "http://127.0.0.1:8088/";

        /// <summary>
        /// Neutral is assumed if the page goes quiet for this long. A closed tab or a dropped
        /// connection must stop the toolhead, not leave it running. The page sends every 50 ms, so
        /// this tolerates five missed updates — and is also how far the toolhead can travel after a
        /// browser dies (250 ms is 25 mm at full speed).
        /// </summary>
        public int InputTimeoutMs { get; set; } = 250;
    }

    public sealed class JogMotionOptions
    {
        public double MaxSpeedXY { get; set; } = 100.0;

        public double MaxSpeedZ { get; set; } = 12.0;

        /// <summary>Speed multiplier while the precision button is held.</summary>
        public double PrecisionFactor { get; set; } = 0.2;

        /// <summary>
        /// How much motion is kept queued in Klipper. Larger is smoother but the toolhead coasts
        /// further after you let go, because queued moves cannot be recalled.
        /// </summary>
        public double LookaheadSeconds { get; set; } = 0.08;

        /// <summary>Moves shorter than this accumulate instead of being sent, so slow jogs stay smooth.</summary>
        public double MinSegmentMm { get; set; } = 0.01;

        /// <summary>Idle time at centre before the jog session is closed and gcode state restored.</summary>
        public double SessionIdleSeconds { get; set; } = 0.75;
    }

    public sealed class JogSafetyOptions
    {
        /// <summary>Home on the first stick input when the printer is not homed.</summary>
        public bool AutoHome { get; set; } = true;

        /// <summary>Conditional-home macro from config/printer/macros/homing.cfg.</summary>
        public string HomeGcode { get; set; } = "_CG28";

        public double HomeTimeoutSeconds { get; set; } = 240;

        /// <summary>After an auto-home, wait for the stick to return to centre before moving.</summary>
        public bool RequireRecenterAfterHome { get; set; } = true;

        /// <summary>Keep this far away from the soft limits so a jog never trips a boundary error.</summary>
        public double EdgeMarginMm { get; set; } = 1.0;

        /// <summary>Floor for jogged Z. Klipper allows negative Z; the joystick should not.</summary>
        public double MinZ { get; set; } = 0.0;

        /// <summary>Optional ceiling for jogged Z. Null uses the printer's own limit.</summary>
        public double? MaxZ { get; set; }
    }
}
