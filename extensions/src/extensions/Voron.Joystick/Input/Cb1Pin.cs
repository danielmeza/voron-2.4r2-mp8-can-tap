using System.Globalization;

namespace Voron.Joystick.Input
{
    /// <summary>
    /// Translates CB1 pin names to the logical numbers the sunxi GPIO driver expects.
    /// The CB1 manual gives the rule: number = (bank - 'A') * 32 + index, so PI14 is 270 —
    /// the same numbering Klipper uses in config/printer/mcu/cb1.cfg ("gpio270").
    /// </summary>
    internal static class Cb1Pin
    {
        /// <summary>Accepts "PI14", "pi14", "gpio270" or "270".</summary>
        public static int Parse(string value)
        {
            if (!TryParse(value, out var pin, out var error))
            {
                throw new ArgumentException(error, nameof(value));
            }

            return pin;
        }

        public static bool TryParse(string? value, out int pin, out string error)
        {
            pin = -1;
            error = string.Empty;

            var text = value?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                error = "Pin name is empty.";
                return false;
            }

            if (text.StartsWith("gpio", StringComparison.OrdinalIgnoreCase))
            {
                text = text[4..];
            }

            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out pin))
            {
                if (pin < 0)
                {
                    error = $"'{value}' is not a valid pin number.";
                    return false;
                }

                return true;
            }

            if (text.Length >= 3 && (text[0] == 'P' || text[0] == 'p'))
            {
                var bank = char.ToUpperInvariant(text[1]);
                if (bank is >= 'A' and <= 'Z'
                    && int.TryParse(text[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                    && index is >= 0 and < 32)
                {
                    pin = ((bank - 'A') * 32) + index;
                    return true;
                }
            }

            error = $"'{value}' is not a CB1 pin. Use a name like 'PI14', or a number like 'gpio270'.";
            return false;
        }

        /// <summary>Formats a logical pin number the way both the CB1 manual and Klipper write it.</summary>
        public static string Describe(int pin) => $"P{(char)('A' + (pin / 32))}{pin % 32} (gpio{pin})";
    }
}
