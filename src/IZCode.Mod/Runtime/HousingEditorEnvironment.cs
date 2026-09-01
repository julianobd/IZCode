using System.Collections.Generic;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using IZCode.Mod.Devices;
using IZLang.Binding;
using IZLang.Devices;
using IZLang.Editor;
using IZLang.Vm;

namespace IZCode.Mod.Runtime
{
    /// <summary>
    /// Implements <see cref="IEditorEnvironment"/> over the holder whose chip is open
    /// in the editor.
    ///
    /// It is what lets completion know each pin's equipment and the hover show real
    /// values: the holder knows exactly what is on d0..d5 and what 'db' is, so there is
    /// no guesswork. It asks through <see cref="ICircuitHolder"/> so that a hardsuit,
    /// where the pins are the wearer's slots, answers just as well as a wall housing.
    /// </summary>
    public sealed class HousingEditorEnvironment : IEditorEnvironment
    {
        private readonly ICircuitHolder? _housing;
        private readonly ProgrammableChip? _chip;

        public HousingEditorEnvironment(ICircuitHolder? housing, ProgrammableChip? chip)
        {
            _housing = housing;
            _chip = chip;
        }

        public DeviceCatalog Catalog => CatalogStore.Current;

        private ILogicable? GetLogicable(int pin)
        {
            if (_housing == null || !DevicePins.IsValid(pin)) return null;

            try
            {
                // int.MaxValue is how the game names the holder itself; int.MinValue as
                // the network index means "no particular network", which is what makes
                // it hand back the holder instead of one of its networks.
                return _housing.GetLogicableFromIndex(
                    pin == DevicePins.Housing ? int.MaxValue : pin, int.MinValue);
            }
            catch
            {
                // Completion runs on every keystroke; a holder that throws when asked
                // (a suit with nobody wearing it) must not take the editor down with it.
                return null;
            }
        }

        public DeviceInfo? GetWiredDevice(int pin)
        {
            var device = GetLogicable(pin);
            if (device == null) return null;

            try { return Catalog.FindByHash(device.GetPrefabHash()); }
            catch { return null; }
        }

        public string? GetWiredDeviceLabel(int pin)
        {
            var device = GetLogicable(pin);
            if (device == null) return null;

            try
            {
                string name = device.GetAsThing?.DisplayName ?? string.Empty;
                return string.IsNullOrEmpty(name) ? null : name;
            }
            catch { return null; }
        }

        public double? GetLiveValue(int pin, int logicType)
        {
            var device = GetLogicable(pin);
            if (device == null) return null;

            try
            {
                var type = (LogicType)logicType;
                if (!device.CanLogicRead(type)) return null;
                return device.GetLogicValue(type);
            }
            catch
            {
                // The tooltip shows up on every mouse move; a device that throws when
                // read must not turn into exception spam.
                return null;
            }
        }

        public double? GetGlobalValue(int slot)
        {
            if (_chip == null) return null;
            if (!IZChipRuntime.TryGet(_chip, out var runtime)) return null;

            var vm = runtime.Vm;
            return vm != null ? vm.GetGlobal(slot) : (double?)null;
        }

        // ------------------------------------------------------------------
        //  Batch selectors
        // ------------------------------------------------------------------

        /// <summary>
        /// The devices the holder can reach over its data networks - the same list the
        /// VM's batch operations walk, so what the editor shows is what the program
        /// will actually talk to.
        /// </summary>
        private List<ILogicable>? GetBatch()
        {
            try { return _housing?.GetBatchOutput(); }
            catch { return null; }
        }

        public DeviceInfo? ResolveSelector(DeviceSelector selector)
        {
            if (selector.PrefabName == null && selector.Label == null) return null;

            // The prefab is written out: the catalog knows its properties, whether or
            // not anything is powered on right now.
            if (selector.PrefabName != null) return Catalog.FindByName(selector.PrefabName);

            // Only a label: ask the network which equipment answers to it. Two prefabs
            // sharing one label have no single property list, so nothing is offered.
            var devices = GetBatch();
            if (devices == null) return null;

            int labelHash = PrefabHash.Compute(selector.Label!);
            int found = 0;
            bool any = false;

            for (int i = 0; i < devices.Count; i++)
            {
                var device = devices[i];
                if (device == null) continue;

                try
                {
                    if (device.GetNameHash() != labelHash) continue;

                    int hash = device.GetPrefabHash();
                    if (!any) { found = hash; any = true; continue; }
                    if (hash != found) return null;
                }
                catch { return null; }
            }

            return any ? Catalog.FindByHash(found) : null;
        }

        public double? GetSelectorValue(DeviceSelector selector, int logicType,
                                        BatchAggregation aggregation)
        {
            if (selector.PrefabName == null && selector.Label == null) return null;

            var devices = GetBatch();
            if (devices == null) return null;

            int prefabHash = selector.PrefabName != null ? PrefabHash.Compute(selector.PrefabName) : 0;
            int labelHash = selector.Label != null ? PrefabHash.Compute(selector.Label) : 0;
            var type = (LogicType)logicType;

            double sum = 0.0;
            double minimum = double.MaxValue;
            double maximum = double.MinValue;
            int count = 0;

            for (int i = 0; i < devices.Count; i++)
            {
                var device = devices[i];
                if (device == null) continue;

                try
                {
                    if (prefabHash != 0 && device.GetPrefabHash() != prefabHash) continue;
                    if (selector.Label != null && device.GetNameHash() != labelHash) continue;
                    if (!device.CanLogicRead(type)) continue;

                    double value = device.GetLogicValue(type);
                    sum += value;
                    if (value < minimum) minimum = value;
                    if (value > maximum) maximum = value;
                    count++;
                }
                catch
                {
                    // Completion runs on every keystroke: one device that throws when
                    // read is skipped, it does not take the whole list down.
                }
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
