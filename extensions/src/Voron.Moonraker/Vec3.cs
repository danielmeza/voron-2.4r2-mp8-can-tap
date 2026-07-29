using System.Text.Json;

namespace Voron.Moonraker
{
    /// <summary>A cartesian triple. Klipper reports positions as <c>[x, y, z, e]</c> arrays.</summary>
    public readonly record struct Vec3(double X, double Y, double Z)
    {
        public static Vec3 Zero => default;

        public double Length => Math.Sqrt((X * X) + (Y * Y) + (Z * Z));

        public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        public static Vec3 operator *(Vec3 a, double scale) => new(a.X * scale, a.Y * scale, a.Z * scale);

        /// <summary>Reads the first three elements of a Klipper position array. Missing elements stay at 0.</summary>
        public static Vec3 FromJsonArray(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                return Zero;
            }

            Span<double> values = stackalloc double[3];
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                if (index >= 3)
                {
                    break;
                }

                values[index++] = item.ValueKind == JsonValueKind.Number ? item.GetDouble() : 0d;
            }

            return new Vec3(values[0], values[1], values[2]);
        }

        public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";
    }
}
