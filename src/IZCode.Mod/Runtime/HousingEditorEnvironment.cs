using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using IZCode.Mod.Devices;
using IZLang.Devices;
using IZLang.Editor;

namespace IZCode.Mod.Runtime
{
    /// <summary>
    /// Implements <see cref="IEditorEnvironment"/> over the CircuitHousing currently
    /// open in the editor.
    ///
    /// It is what lets completion know each pin's equipment and the hover show real
    /// values: the housing knows exactly what is wired to d0..d5, so there is no
    /// guesswork.
    /// </summary>
    public sealed class HousingEditorEnvironment : IEditorEnvironment
    {
        private readonly CircuitHousing? _housing;
        private readonly ProgrammableChip? _chip;

        public HousingEditorEnvironment(CircuitHousing? housing, ProgrammableChip? chip)
        {
            _housing = housing;
            _chip = chip;
        }

        public DeviceCatalog Catalog => CatalogStore.Current;

        private ILogicable? GetLogicable(int pin)
        {
            if (_housing == null || pin < 0 || pin > 5) return null;

            var devices = _housing.Devices;
            return devices != null && pin < devices.Length ? devices[pin] : null;
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
