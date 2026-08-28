using System;
using System.Collections.Generic;

namespace IZLang.Vm
{
    /// <summary>Aggregation mode for a batch read, mirroring IC10.</summary>
    public enum BatchAggregation
    {
        Average = 0,
        Sum = 1,
        Minimum = 2,
        Maximum = 3,
    }

    /// <summary>
    /// Everything the VM needs from the outside world.
    ///
    /// Being an interface is what keeps <c>IZLang</c> free of any dependency on
    /// Unity and on <c>Assembly-CSharp</c>: the mod implements it over the real
    /// CircuitHousing, and the tests implement it over a dictionary.
    /// </summary>
    public interface IDeviceHost
    {
        /// <summary>World time in seconds. The basis for 'sleep'.</summary>
        double CurrentTime { get; }

        /// <summary>false when the pin has no device connected, or the LogicType is not readable.</summary>
        bool TryReadDevice(int pin, int logicType, out double value);

        /// <summary>false when the pin is empty, or the LogicType is not writable.</summary>
        bool TryWriteDevice(int pin, int logicType, double value);

        bool TryReadSlot(int pin, int slotIndex, int logicSlotType, out double value);

        bool TryBatchRead(double prefabHash, int logicType, BatchAggregation aggregation, out double value);

        /// <summary>Returns how many devices were written to.</summary>
        int BatchWrite(double prefabHash, int logicType, double value);

        bool TryBatchNamedRead(double prefabHash, double nameHash, int logicType,
                               BatchAggregation aggregation, out double value);

        int BatchNamedWrite(double prefabHash, double nameHash, int logicType, double value);
    }

    /// <summary>
    /// Host for tests and dry runs: keeps the device values in memory, with no
    /// game behind it at all.
    /// </summary>
    public sealed class MemoryDeviceHost : IDeviceHost
    {
        private readonly Dictionary<long, double> _deviceValues = new Dictionary<long, double>();
        private readonly Dictionary<long, double> _slotValues = new Dictionary<long, double>();
        private readonly HashSet<int> _connectedPins = new HashSet<int>();

        /// <summary>Every write is recorded here, in order - the tests assert against it.</summary>
        public List<DeviceWrite> Writes { get; } = new List<DeviceWrite>();

        public double CurrentTime { get; set; }

        public readonly struct DeviceWrite
        {
            public readonly int Pin;
            public readonly int LogicType;
            public readonly double Value;

            public DeviceWrite(int pin, int logicType, double value)
            {
                Pin = pin;
                LogicType = logicType;
                Value = value;
            }

            public override string ToString() => "d" + Pin + "." + LogicType + " = " + Value;
        }

        private static long Key(int a, int b) => ((long)a << 32) | (uint)b;
        private static long SlotKey(int pin, int slot, int logic) => ((long)pin << 40) | ((long)slot << 20) | (uint)logic;

        public void Connect(int pin) => _connectedPins.Add(pin);

        public void Set(int pin, int logicType, double value)
        {
            _connectedPins.Add(pin);
            _deviceValues[Key(pin, logicType)] = value;
        }

        public void SetSlot(int pin, int slotIndex, int logicSlotType, double value)
        {
            _connectedPins.Add(pin);
            _slotValues[SlotKey(pin, slotIndex, logicSlotType)] = value;
        }

        public double Get(int pin, int logicType) =>
            _deviceValues.TryGetValue(Key(pin, logicType), out double v) ? v : 0.0;

        public bool TryReadDevice(int pin, int logicType, out double value)
        {
            if (!_connectedPins.Contains(pin)) { value = 0.0; return false; }
            value = _deviceValues.TryGetValue(Key(pin, logicType), out double v) ? v : 0.0;
            return true;
        }

        public bool TryWriteDevice(int pin, int logicType, double value)
        {
            if (!_connectedPins.Contains(pin)) return false;
            _deviceValues[Key(pin, logicType)] = value;
            Writes.Add(new DeviceWrite(pin, logicType, value));
            return true;
        }

        public bool TryReadSlot(int pin, int slotIndex, int logicSlotType, out double value)
        {
            if (!_connectedPins.Contains(pin)) { value = 0.0; return false; }
            value = _slotValues.TryGetValue(SlotKey(pin, slotIndex, logicSlotType), out double v) ? v : 0.0;
            return true;
        }

        // ------------------------------------------------------------------
        //  Batch network
        // ------------------------------------------------------------------

        /// <summary>A device on the simulated "network", reachable through all()/named().</summary>
        public sealed class FakeDevice
        {
            public int PrefabHash { get; }
            public int NameHash { get; }
            public Dictionary<int, double> Values { get; } = new Dictionary<int, double>();

            public FakeDevice(int prefabHash, int nameHash)
            {
                PrefabHash = prefabHash;
                NameHash = nameHash;
            }

            public double Get(int logicType) => Values.TryGetValue(logicType, out double v) ? v : 0.0;
        }

        public List<FakeDevice> Network { get; } = new List<FakeDevice>();

        /// <summary>Every batch write is recorded, so the tests can check the target.</summary>
        public List<BatchWriteRecord> BatchWrites { get; } = new List<BatchWriteRecord>();

        public readonly struct BatchWriteRecord
        {
            public readonly int PrefabHash;
            public readonly int? NameHash;
            public readonly int LogicType;
            public readonly double Value;
            public readonly int Matched;

            public BatchWriteRecord(int prefabHash, int? nameHash, int logicType, double value, int matched)
            {
                PrefabHash = prefabHash;
                NameHash = nameHash;
                LogicType = logicType;
                Value = value;
                Matched = matched;
            }
        }

        public FakeDevice AddNetworkDevice(int prefabHash, int nameHash = 0)
        {
            var device = new FakeDevice(prefabHash, nameHash);
            Network.Add(device);
            return device;
        }

        /// <summary>prefabHash 0 matches any type; a null nameHash does not filter by label.</summary>
        private bool Matches(FakeDevice device, int prefabHash, int? nameHash)
        {
            if (prefabHash != 0 && device.PrefabHash != prefabHash) return false;
            if (nameHash.HasValue && device.NameHash != nameHash.Value) return false;
            return true;
        }

        private bool Aggregate(int prefabHash, int? nameHash, int logicType,
                               BatchAggregation aggregation, out double value)
        {
            value = 0.0;
            double sum = 0.0, minimum = double.MaxValue, maximum = double.MinValue;
            int count = 0;

            foreach (var device in Network)
            {
                if (!Matches(device, prefabHash, nameHash)) continue;

                double reading = device.Get(logicType);
                sum += reading;
                if (reading < minimum) minimum = reading;
                if (reading > maximum) maximum = reading;
                count++;
            }

            if (count == 0) return false;

            switch (aggregation)
            {
                case BatchAggregation.Sum: value = sum; break;
                case BatchAggregation.Minimum: value = minimum; break;
                case BatchAggregation.Maximum: value = maximum; break;
                default: value = sum / count; break;
            }
            return true;
        }

        private int WriteMatching(int prefabHash, int? nameHash, int logicType, double value)
        {
            int written = 0;
            foreach (var device in Network)
            {
                if (!Matches(device, prefabHash, nameHash)) continue;
                device.Values[logicType] = value;
                written++;
            }
            BatchWrites.Add(new BatchWriteRecord(prefabHash, nameHash, logicType, value, written));
            return written;
        }

        public bool TryBatchRead(double prefabHash, int logicType, BatchAggregation aggregation, out double value) =>
            Aggregate((int)prefabHash, null, logicType, aggregation, out value);

        public int BatchWrite(double prefabHash, int logicType, double value) =>
            WriteMatching((int)prefabHash, null, logicType, value);

        public bool TryBatchNamedRead(double prefabHash, double nameHash, int logicType,
                                      BatchAggregation aggregation, out double value) =>
            Aggregate((int)prefabHash, (int)nameHash, logicType, aggregation, out value);

        public int BatchNamedWrite(double prefabHash, double nameHash, int logicType, double value) =>
            WriteMatching((int)prefabHash, (int)nameHash, logicType, value);
    }
}
