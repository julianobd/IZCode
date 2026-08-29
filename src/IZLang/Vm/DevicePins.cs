namespace IZLang.Vm
{
    /// <summary>
    /// The names a program can use to point at a device, and the numbers they turn
    /// into once they reach <see cref="IDeviceHost"/>.
    ///
    /// Two shapes exist: the six pins of the housing, 'd0' to 'd5', and 'db' - the
    /// device the chip itself is installed in. On a circuit housing 'db' is the
    /// housing; on a hardsuit, where the chip goes into the suit's own chip slot, it
    /// is the suit. That is what makes a suit able to read its own
    /// PressureExternal and drive its own AC.
    ///
    /// The number chosen for 'db' is <see cref="Housing"/> = 6, one past the last
    /// pin, and not the game's own <c>int.MaxValue</c>: it keeps every bound check,
    /// array and dictionary key in the compiler and the editor working on small
    /// contiguous indices. Translating to whatever the game expects is the host's
    /// job, and happens in one place.
    /// </summary>
    public static class DevicePins
    {
        public const int First = 0;

        /// <summary>Last of the numbered pins: 'd5'.</summary>
        public const int Last = 5;

        /// <summary>'db' - the device the chip is installed in.</summary>
        public const int Housing = Last + 1;

        /// <summary>How many distinct devices a program can name, 'db' included.</summary>
        public const int Count = Housing + 1;

        public static bool IsValid(int pin) => pin >= First && pin <= Housing;

        /// <summary>'d0' to 'd5' and 'db'. False for anything else, including 'd6'.</summary>
        public static bool TryParse(string text, out int pin)
        {
            pin = -1;
            if (text == null || text.Length != 2 || text[0] != 'd') return false;

            char second = text[1];
            if (second == 'b') { pin = Housing; return true; }
            if (second < '0' || second > '5') return false;

            pin = second - '0';
            return true;
        }

        /// <summary>The name the player wrote, for error messages and tooltips.</summary>
        public static string Name(int pin) =>
            pin == Housing ? "db" : "d" + pin;

        /// <summary>What sits on a pin, in words. 'db' needs the explanation; the pins do not.</summary>
        public static string Describe(int pin) =>
            pin == Housing ? "db - the device the chip is installed in" : Name(pin);
    }
}
