using System.Collections.Generic;
using IZLang.Devices;

namespace IZLang.Editor
{
    /// <summary>
    /// What the completion engine and the hover need to know about the world.
    ///
    /// Inside the game this is implemented over the CircuitHousing open in the
    /// editor, which knows exactly which device sits on each pin. In the tests it
    /// is an in-memory object. Same role <c>IDeviceHost</c> plays for the VM: the
    /// boundary between the pure logic and Unity.
    /// </summary>
    public interface IEditorEnvironment
    {
        DeviceCatalog Catalog { get; }

        /// <summary>
        /// The device wired to the pin, or null when the pin is empty or the prefab
        /// is not in the catalog. Knowing this is what makes it possible to suggest
        /// exactly that equipment's properties, instead of all 358 in the game.
        /// </summary>
        DeviceInfo? GetWiredDevice(int pin);

        /// <summary>The label the player gave the device on that pin, if any.</summary>
        string? GetWiredDeviceLabel(int pin);

        /// <summary>Current value of a property, or null when it cannot be read right now.</summary>
        double? GetLiveValue(int pin, int logicType);

        /// <summary>Current value of a global program variable, when the program is running.</summary>
        double? GetGlobalValue(int slot);
    }

    /// <summary>Empty environment: no wired devices and no catalog.</summary>
    public sealed class EmptyEditorEnvironment : IEditorEnvironment
    {
        public static EmptyEditorEnvironment Instance { get; } = new EmptyEditorEnvironment();

        public DeviceCatalog Catalog => DeviceCatalog.Empty;
        public DeviceInfo? GetWiredDevice(int pin) => null;
        public string? GetWiredDeviceLabel(int pin) => null;
        public double? GetLiveValue(int pin, int logicType) => null;
        public double? GetGlobalValue(int slot) => null;
    }

    /// <summary>Configurable in-memory environment, for tests and dry runs.</summary>
    public sealed class MemoryEditorEnvironment : IEditorEnvironment
    {
        private readonly Dictionary<int, DeviceInfo> _wired = new Dictionary<int, DeviceInfo>();
        private readonly Dictionary<int, string> _labels = new Dictionary<int, string>();
        private readonly Dictionary<long, double> _values = new Dictionary<long, double>();
        private readonly Dictionary<int, double> _globals = new Dictionary<int, double>();

        public DeviceCatalog Catalog { get; set; } = DeviceCatalog.Empty;

        public void Wire(int pin, DeviceInfo device, string? label = null)
        {
            _wired[pin] = device;
            if (label != null) _labels[pin] = label;
        }

        public void SetValue(int pin, int logicType, double value) =>
            _values[((long)pin << 32) | (uint)logicType] = value;

        public void SetGlobal(int slot, double value) => _globals[slot] = value;

        public DeviceInfo? GetWiredDevice(int pin) =>
            _wired.TryGetValue(pin, out var device) ? device : null;

        public string? GetWiredDeviceLabel(int pin) =>
            _labels.TryGetValue(pin, out var label) ? label : null;

        public double? GetLiveValue(int pin, int logicType) =>
            _values.TryGetValue(((long)pin << 32) | (uint)logicType, out double value)
                ? value
                : (double?)null;

        public double? GetGlobalValue(int slot) =>
            _globals.TryGetValue(slot, out double value) ? value : (double?)null;
    }
}
