namespace Voron.Joystick.Input
{
    /// <summary>
    /// One sample from a joystick, normalised so the jog loop never has to know what hardware
    /// produced it. Each component is a fraction of full speed in the range -1..1.
    /// </summary>
    public readonly record struct JogInput(double X, double Y, double Z, bool Precision)
    {
        public static JogInput Neutral => default;

        public bool IsNeutral => X == 0d && Y == 0d && Z == 0d;

        /// <summary>Length of the XY component; used to keep diagonals from running faster than straight moves.</summary>
        public double PlanarMagnitude => Math.Sqrt((X * X) + (Y * Y));
    }
}
