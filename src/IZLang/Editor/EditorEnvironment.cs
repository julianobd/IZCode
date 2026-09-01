using System;
using System.Collections.Generic;
using IZLang.Devices;
using IZLang.Vm;

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

        /// <summary>
        /// Which equipment a batch selector stands for.
        ///
        /// When the prefab is written out - 'named(StructureDiode, "led")' - it is the
        /// prefab itself. When only a label is given - 'named("led")' - the data network
        /// is asked, and the answer only comes back when every device carrying that
        /// label is the same prefab: with two kinds of equipment answering to one label
        /// there is no single set of properties to offer. null when nothing matches, or
        /// when the prefab is not in the catalog.
        /// </summary>
        DeviceInfo? ResolveSelector(DeviceSelector selector);

        /// <summary>
        /// Current value of a property over the devices the selector matches, collapsed
        /// the same way a batch read collapses them. null when nothing readable matches,
        /// except for <see cref="BatchAggregation.Count"/>, whose answer is then 0.
        /// </summary>
        double? GetSelectorValue(DeviceSelector selector, int logicType, BatchAggregation aggregation);
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
        public DeviceInfo? ResolveSelector(DeviceSelector selector) => null;
        public double? GetSelectorValue(DeviceSelector selector, int logicType,
                                        BatchAggregation aggregation) => null;
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

        // ------------------------------------------------------------------
        //  Simulated data network, reachable through all()/named()
        // ------------------------------------------------------------------

        private sealed class NetworkDevice
        {
            public DeviceInfo Device = null!;
            public string? Label;
            public readonly Dictionary<int, double> Values = new Dictionary<int, double>();
        }

        private readonly List<NetworkDevice> _network = new List<NetworkDevice>();

        /// <summary>Puts one device on the simulated network, with the label it carries.</summary>
        public void AddNetworkDevice(DeviceInfo device, string? label = null,
                                     Dictionary<int, double>? values = null)
        {
            var entry = new NetworkDevice { Device = device, Label = label };
            if (values != null)
                foreach (var pair in values) entry.Values[pair.Key] = pair.Value;
            _network.Add(entry);
        }

        private bool Matches(NetworkDevice entry, DeviceSelector selector)
        {
            if (selector.PrefabName != null &&
                !string.Equals(entry.Device.PrefabName, selector.PrefabName, StringComparison.Ordinal))
                return false;

            if (selector.Label != null &&
                !string.Equals(entry.Label, selector.Label, StringComparison.Ordinal))
                return false;

            return true;
        }

        public DeviceInfo? ResolveSelector(DeviceSelector selector)
        {
            if (selector.IsEmpty) return null;

            // The prefab was written out: the catalog already knows the answer, and no
            // device has to be on the network for the properties to be the right ones.
            if (selector.PrefabName != null) return Catalog.FindByName(selector.PrefabName);

            DeviceInfo? found = null;
            foreach (var entry in _network)
            {
                if (!Matches(entry, selector)) continue;
                if (found == null) { found = entry.Device; continue; }

                // Two kinds of equipment under one label: no single property list.
                if (!string.Equals(found.PrefabName, entry.Device.PrefabName, StringComparison.Ordinal))
                    return null;
            }
            return found;
        }

        public double? GetSelectorValue(DeviceSelector selector, int logicType,
                                        BatchAggregation aggregation)
        {
            if (selector.IsEmpty) return null;

            double sum = 0.0;
            double minimum = double.MaxValue;
            double maximum = double.MinValue;
            int count = 0;

            foreach (var entry in _network)
            {
                if (!Matches(entry, selector)) continue;
                if (!entry.Values.TryGetValue(logicType, out double value)) continue;

                sum += value;
                if (value < minimum) minimum = value;
                if (value > maximum) maximum = value;
                count++;
            }

            if (count == 0) return aggregation == BatchAggregation.Count ? 0.0 : (double?)null;

            switch (aggregation)
            {
                case BatchAggregation.Sum: return sum;
                case BatchAggregation.Minimum: return minimum;
                case BatchAggregation.Maximum: return maximum;
                case BatchAggregation.Count: return count;
                default: return sum / count;
            }
        }
    }
}
