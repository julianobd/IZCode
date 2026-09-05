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

        /// <summary>
        /// How many devices answered. The only mode for which an empty batch is a
        /// real answer instead of a failed read.
        /// </summary>
        Count = 4,
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

        /// <summary>
        /// false when the pin has no device connected, or the LogicType is not readable.
        ///
        /// <paramref name="pin"/> is 0..5 for d0..d5, or
        /// <see cref="DevicePins.Housing"/> for 'db' - the device holding the chip.
        /// </summary>
        bool TryReadDevice(int pin, int logicType, out double value);

        /// <summary>false when the pin is empty, or the LogicType is not writable.</summary>
        bool TryWriteDevice(int pin, int logicType, double value);

        /// <summary>
        /// Is there a device on the pin at all? Asked by 'isset', and never an error:
        /// an empty pin is the answer the program wanted.
        /// </summary>
        bool IsDeviceConnected(int pin);

        bool TryReadSlot(int pin, int slotIndex, int logicSlotType, out double value);

        bool TryBatchRead(double prefabHash, int logicType, BatchAggregation aggregation, out double value);

        /// <summary>Returns how many devices were written to.</summary>
        int BatchWrite(double prefabHash, int logicType, double value);

        bool TryBatchNamedRead(double prefabHash, double nameHash, int logicType,
                               BatchAggregation aggregation, out double value);

        int BatchNamedWrite(double prefabHash, double nameHash, int logicType, double value);

        /// <summary>
        /// The slot counterpart of <see cref="TryBatchRead"/>: reads slot
        /// <paramref name="slotIndex"/> of every device the prefab matched and
        /// collapses the readings with <paramref name="aggregation"/>.
        ///
        /// A device that has no such slot, or whose slot cannot answer that property,
        /// is skipped rather than counted as a zero - the same treatment a device that
        /// cannot read the property gets in the property batch.
        /// </summary>
        bool TryBatchSlotRead(double prefabHash, int slotIndex, int logicSlotType,
                              BatchAggregation aggregation, out double value);

        bool TryBatchNamedSlotRead(double prefabHash, double nameHash, int slotIndex,
                                   int logicSlotType, BatchAggregation aggregation, out double value);
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

            public override string ToString() =>
                DevicePins.Name(Pin) + "." + LogicType + " = " + Value;
        }

        private static long Key(int a, int b) => ((long)a << 32) | (uint)b;
        private static long SlotKey(int pin, int slot, int logic) =>
            ((long)pin << 40) | ((long)(slot & 0xFFFFF) << 20) | (uint)(logic & 0xFFFFF);

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

        public bool IsDeviceConnected(int pin) =>
            DevicePins.IsValid(pin) && _connectedPins.Contains(pin);

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

            /// <summary>Slot readings, keyed by slot index and LogicSlotType.</summary>
            public Dictionary<long, double> SlotValues { get; } = new Dictionary<long, double>();

            public FakeDevice(int prefabHash, int nameHash)
            {
                PrefabHash = prefabHash;
                NameHash = nameHash;
            }

            public double Get(int logicType) => Values.TryGetValue(logicType, out double v) ? v : 0.0;

            public void SetSlot(int slotIndex, int logicSlotType, double value) =>
                SlotValues[SlotEntry(slotIndex, logicSlotType)] = value;

            /// <summary>false when this device has nothing to say about that slot.</summary>
            public bool TryGetSlot(int slotIndex, int logicSlotType, out double value) =>
                SlotValues.TryGetValue(SlotEntry(slotIndex, logicSlotType), out value);

            private static long SlotEntry(int slotIndex, int logicSlotType) =>
                ((long)slotIndex << 32) | (uint)logicSlotType;
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

            // Nothing matched: 'count' still has an answer, and it is 0.
            if (count == 0) return aggregation == BatchAggregation.Count;

            switch (aggregation)
            {
                case BatchAggregation.Sum: value = sum; break;
                case BatchAggregation.Minimum: value = minimum; break;
                case BatchAggregation.Maximum: value = maximum; break;
                case BatchAggregation.Count: value = count; break;
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

        private bool AggregateSlot(int prefabHash, int? nameHash, int slotIndex, int logicSlotType,
                                   BatchAggregation aggregation, out double value)
        {
            value = 0.0;
            double sum = 0.0, minimum = double.MaxValue, maximum = double.MinValue;
            int count = 0;

            foreach (var device in Network)
            {
                if (!Matches(device, prefabHash, nameHash)) continue;
                if (!device.TryGetSlot(slotIndex, logicSlotType, out double reading)) continue;

                sum += reading;
                if (reading < minimum) minimum = reading;
                if (reading > maximum) maximum = reading;
                count++;
            }

            if (count == 0) return aggregation == BatchAggregation.Count;

            switch (aggregation)
            {
                case BatchAggregation.Sum: value = sum; break;
                case BatchAggregation.Minimum: value = minimum; break;
                case BatchAggregation.Maximum: value = maximum; break;
                case BatchAggregation.Count: value = count; break;
                default: value = sum / count; break;
            }
            return true;
        }

        public bool TryBatchSlotRead(double prefabHash, int slotIndex, int logicSlotType,
                                     BatchAggregation aggregation, out double value) =>
            AggregateSlot((int)prefabHash, null, slotIndex, logicSlotType, aggregation, out value);

        public bool TryBatchNamedSlotRead(double prefabHash, double nameHash, int slotIndex,
                                          int logicSlotType, BatchAggregation aggregation, out double value) =>
            AggregateSlot((int)prefabHash, (int)nameHash, slotIndex, logicSlotType, aggregation, out value);
    }
}
