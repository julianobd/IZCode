using System.Collections.Generic;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using IZCode.Mod.Diagnostics;
using IZLang.Vm;
using UnityEngine;

namespace IZCode.Mod.Runtime
{
    /// <summary>
    /// Wires the <see cref="IZVm"/> to the real housing.
    ///
    /// All the VM's conversation with the game goes through here - and only here. That
    /// is what lets the compiler and the VM be tested without Stationeers running.
    ///
    /// Everything goes through <see cref="ICircuitHolder"/> rather than through the
    /// concrete CircuitHousing, because a circuit housing is not the only thing that
    /// takes a chip: a hardsuit holds one too, and there d0..d5 are the wearer's
    /// helmet, backpack, toolbelt, glasses and hands instead of six cables.
    /// </summary>
    public sealed class HousingDeviceHost : IDeviceHost
    {
        private readonly ICircuitHolder _housing;

        public HousingDeviceHost(ICircuitHolder housing)
        {
            _housing = housing;
        }

        public double CurrentTime => Time.time;

        /// <summary>
        /// The device a pin points at, asking the holder itself rather than reading its
        /// Devices array: only the holder knows what its indices mean, and only it
        /// checks that the device is still on the same data network.
        /// </summary>
        private ILogicable? GetDevice(int pin)
        {
            if (!DevicePins.IsValid(pin)) return null;

            try
            {
                // The network index is passed explicitly: the holder only hands back
                // itself for 'db' when no particular network was asked for, and relying
                // on an interface method's default argument is not worth the risk.
                return _housing?.GetLogicableFromIndex(GameIndex(pin), int.MinValue);
            }
            catch
            {
                // A holder with no wearer (a suit lying on the floor) throws rather
                // than returning null. To the program that is an empty pin.
                return null;
            }
        }

        /// <summary>
        /// Translates our pin numbering into the game's. We number 'db' as
        /// <see cref="DevicePins.Housing"/> = 6 so that the compiler and the editor keep
        /// working on small contiguous indices; the game marks it with int.MaxValue.
        /// </summary>
        private static int GameIndex(int pin) =>
            pin == DevicePins.Housing ? int.MaxValue : pin;

        public bool TryReadDevice(int pin, int logicType, out double value)
        {
            value = 0.0;
            var device = GetDevice(pin);
            if (device == null) return false;

            var type = (LogicType)logicType;
            if (!device.CanLogicRead(type)) return false;

            value = device.GetLogicValue(type);
            return true;
        }

        public bool TryWriteDevice(int pin, int logicType, double value)
        {
            var device = GetDevice(pin);
            if (device == null) return false;

            var type = (LogicType)logicType;
            if (!device.CanLogicWrite(type)) return false;

            // Writing the same value again generates pointless network traffic; IC10
            // itself makes this check before writing.
            if (device.GetLogicValue(type) != value)
                device.SetLogicValue(type, value);

            return true;
        }

        public bool TryReadSlot(int pin, int slotIndex, int logicSlotType, out double value)
        {
            value = 0.0;
            var device = GetDevice(pin);
            if (device == null) return false;

            var type = (LogicSlotType)logicSlotType;
            if (!device.CanLogicRead(type, slotIndex)) return false;

            value = device.GetLogicValue(type, slotIndex);
            return true;
        }

        // ------------------------------------------------------------------
        //  Batch operations
        // ------------------------------------------------------------------

        private List<ILogicable>? GetBatch() => _housing?.GetBatchOutput();

        public bool TryBatchRead(double prefabHash, int logicType, BatchAggregation aggregation, out double value) =>
            Aggregate(GetBatch(), (int)prefabHash, nameHash: null, (LogicType)logicType, aggregation, out value);

        public bool TryBatchNamedRead(double prefabHash, double nameHash, int logicType,
                                      BatchAggregation aggregation, out double value) =>
            Aggregate(GetBatch(), (int)prefabHash, (int)nameHash, (LogicType)logicType, aggregation, out value);

        /// <summary>
        /// Walks the network's devices applying the aggregation mode. Reimplemented here
        /// instead of calling the game's, to keep the semantics under our control - in
        /// particular, what happens when nothing matches the filter.
        /// </summary>
        private static bool Aggregate(List<ILogicable>? devices, int prefabHash, int? nameHash,
                                      LogicType type, BatchAggregation aggregation, out double value)
        {
            value = 0.0;
            if (devices == null) return false;

            double sum = 0.0;
            double minimum = double.MaxValue;
            double maximum = double.MinValue;
            int count = 0;

            for (int i = 0; i < devices.Count; i++)
            {
                var device = devices[i];
                if (device == null) continue;

                // prefabHash 0 means "any prefab", used by named(...).
                if (prefabHash != 0 && device.GetPrefabHash() != prefabHash) continue;
                if (nameHash.HasValue && device.GetNameHash() != nameHash.Value) continue;
                if (!device.CanLogicRead(type)) continue;

                double reading = device.GetLogicValue(type);
                sum += reading;
                if (reading < minimum) minimum = reading;
                if (reading > maximum) maximum = reading;
                count++;
            }

            // No device matched: return 0, like IC10, instead of NaN.
            if (count == 0)
            {
                ReportEmptyBatch("read", "batch-read-empty", devices.Count, prefabHash, nameHash, type);
                return false;
            }

            switch (aggregation)
            {
                case BatchAggregation.Sum: value = sum; break;
                case BatchAggregation.Minimum: value = minimum; break;
                case BatchAggregation.Maximum: value = maximum; break;
                default: value = sum / count; break;
            }
            return true;
        }

        public int BatchWrite(double prefabHash, int logicType, double value) =>
            WriteMatching((int)prefabHash, nameHash: null, (LogicType)logicType, value);

        public int BatchNamedWrite(double prefabHash, double nameHash, int logicType, double value) =>
            WriteMatching((int)prefabHash, (int)nameHash, (LogicType)logicType, value);

        private int WriteMatching(int prefabHash, int? nameHash, LogicType type, double value)
        {
            var devices = GetBatch();
            if (devices == null) return 0;

            int written = 0;
            for (int i = 0; i < devices.Count; i++)
            {
                var device = devices[i];
                if (device == null) continue;
                if (prefabHash != 0 && device.GetPrefabHash() != prefabHash) continue;
                if (nameHash.HasValue && device.GetNameHash() != nameHash.Value) continue;
                if (!device.CanLogicWrite(type)) continue;

                if (device.GetLogicValue(type) != value)
                    device.SetLogicValue(type, value);
                written++;
            }

            if (written == 0)
                ReportEmptyBatch("write", "batch-write-empty", devices.Count, prefabHash, nameHash, type);

            return written;
        }

        /// <summary>
        /// Warns that a batch operation reached no device at all.
        ///
        /// Without it the "nothing happens" case is indistinguishable from "it happened
        /// and the value was zero": the VM cannot tell the difference, and the player is
        /// left staring at a correct program that does nothing. The network size goes
        /// along because it separates the two causes at a glance - wrong prefab (the
        /// network has devices, none matched) from wrong wiring (the network is empty).
        /// </summary>
        private static void ReportEmptyBatch(string operation, string throttleKey, int networkSize,
                                             int prefabHash, int? nameHash, LogicType type)
        {
            if (!IZLog.IsOn(IZLogArea.Vm, IZLogLevel.Warn)) return;

            IZLog.Throttled(IZLogArea.Vm, IZLogLevel.Warn, throttleKey, 10f, () =>
                "batch " + operation + " reached no device: prefab " + prefabHash +
                (nameHash.HasValue ? " label " + nameHash.Value : string.Empty) +
                " property " + type + "; the housing network has " + networkSize +
                " device(s). Check the prefab name and whether the equipment is on the " +
                "same data network as the housing.");
        }
    }
}
