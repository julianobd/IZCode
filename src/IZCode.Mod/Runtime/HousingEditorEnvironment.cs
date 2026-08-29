using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using IZCode.Mod.Devices;
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
    }
}
