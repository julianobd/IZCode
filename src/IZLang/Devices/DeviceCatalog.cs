using System;
using System.Collections.Generic;

namespace IZLang.Devices
{
    /// <summary>How a logic property can be used on a specific device.</summary>
    [Flags]
    public enum LogicAccess
    {
        None = 0,
        Read = 1,
        Write = 2,
        ReadWrite = Read | Write,
    }

    public static class LogicAccessExtensions
    {
        public static bool CanRead(this LogicAccess access) => (access & LogicAccess.Read) != 0;
        public static bool CanWrite(this LogicAccess access) => (access & LogicAccess.Write) != 0;

        /// <summary>Short label shown in completion: "rw", "r" or "w".</summary>
        public static string Label(this LogicAccess access)
        {
            switch (access)
            {
                case LogicAccess.ReadWrite: return "rw";
                case LogicAccess.Read: return "r";
                case LogicAccess.Write: return "w";
                default: return "-";
            }
        }
    }

    /// <summary>A device logic property, with the access that device allows.</summary>
    public sealed class LogicProperty
    {
        public string Name { get; }
        public int LogicType { get; }
        public LogicAccess Access { get; }

        public LogicProperty(string name, int logicType, LogicAccess access)
        {
            Name = name;
            LogicType = logicType;
            Access = access;
        }

        public override string ToString() => Name + " (" + Access.Label() + ")";
    }

    /// <summary>A slot property. Slots are always read only in the game.</summary>
    public sealed class SlotProperty
    {
        public string Name { get; }
        public int LogicSlotType { get; }

        public SlotProperty(string name, int logicSlotType)
        {
            Name = name;
            LogicSlotType = logicSlotType;
        }

        public override string ToString() => Name;
    }

    /// <summary>Everything known about a prefab that responds to logic.</summary>
    public sealed class DeviceInfo
    {
        public string PrefabName { get; }
        public int PrefabHash { get; }
        public string DisplayName { get; }
        public int SlotCount { get; }

        public IReadOnlyList<LogicProperty> Properties { get; }
        public IReadOnlyList<SlotProperty> SlotProperties { get; }

        private readonly Dictionary<string, LogicProperty> _byName;

        public DeviceInfo(string prefabName, int prefabHash, string displayName, int slotCount,
                          IReadOnlyList<LogicProperty> properties, IReadOnlyList<SlotProperty> slotProperties)
        {
            PrefabName = prefabName;
            PrefabHash = prefabHash;
            DisplayName = displayName;
            SlotCount = slotCount;
            Properties = properties;
            SlotProperties = slotProperties;

            _byName = new Dictionary<string, LogicProperty>(properties.Count, StringComparer.Ordinal);
            foreach (var property in properties) _byName[property.Name] = property;
        }

        public LogicProperty? FindProperty(string name) =>
            _byName.TryGetValue(name, out var property) ? property : null;

        public override string ToString() =>
            PrefabName + " (" + Properties.Count + " properties)";
    }

    /// <summary>
    /// Catalog of every device in the game and the properties each one accepts.
    ///
    /// It cannot be built from the DLL: <c>CanLogicRead</c> queries prefab state that
    /// lives in Unity's asset bundles. So the catalog is scanned at runtime, inside
    /// the game, and written to disk - this type only loads the result. That is why
    /// it lives in IZLang, away from Unity: that way completion and hover can be
    /// tested without opening Stationeers.
    /// </summary>
    public sealed class DeviceCatalog
    {
        private readonly Dictionary<string, DeviceInfo> _byPrefabName;
        private readonly Dictionary<int, DeviceInfo> _byPrefabHash;

        public IReadOnlyList<DeviceInfo> Devices { get; }

        /// <summary>Game version the catalog was generated from. Empty when unknown.</summary>
        public string GameVersion { get; }

        public static DeviceCatalog Empty { get; } =
            new DeviceCatalog(new List<DeviceInfo>(), string.Empty);

        public DeviceCatalog(IReadOnlyList<DeviceInfo> devices, string gameVersion)
        {
            Devices = devices;
            GameVersion = gameVersion ?? string.Empty;

            _byPrefabName = new Dictionary<string, DeviceInfo>(devices.Count, StringComparer.Ordinal);
            _byPrefabHash = new Dictionary<int, DeviceInfo>(devices.Count);

            foreach (var device in devices)
            {
                // Duplicate prefabs should not exist, but if the game registers two
                // with the same name, the first one wins instead of throwing.
                if (!_byPrefabName.ContainsKey(device.PrefabName))
                    _byPrefabName.Add(device.PrefabName, device);
                if (!_byPrefabHash.ContainsKey(device.PrefabHash))
                    _byPrefabHash.Add(device.PrefabHash, device);
            }
        }

        public bool IsEmpty => Devices.Count == 0;

        public DeviceInfo? FindByName(string prefabName) =>
            _byPrefabName.TryGetValue(prefabName, out var device) ? device : null;

        public DeviceInfo? FindByHash(int prefabHash) =>
            _byPrefabHash.TryGetValue(prefabHash, out var device) ? device : null;

        /// <summary>
        /// Prefabs whose name contains <paramref name="fragment"/>, case insensitively.
        ///
        /// Sorting is by match position, not alphabetical. That matters because nearly
        /// every prefab in the game starts with "Structure" or "Item": a "starts with"
        /// criterion would never fire, and alphabetical order would put
        /// StructureCircuitHousingSolar ahead of StructureSolarPanel for someone who
        /// typed "Solar". An earlier match wins; on a tie, the shorter name wins.
        /// </summary>
        public List<DeviceInfo> Search(string fragment, int limit = 32)
        {
            var matches = new List<(DeviceInfo Device, int Index)>();

            foreach (var device in Devices)
            {
                if (fragment.Length == 0)
                {
                    matches.Add((device, 0));
                    continue;
                }

                int index = device.PrefabName.IndexOf(fragment, StringComparison.OrdinalIgnoreCase);
                if (index >= 0) matches.Add((device, index));
            }

            matches.Sort((a, b) =>
            {
                if (a.Index != b.Index) return a.Index.CompareTo(b.Index);

                int lengthOrder = a.Device.PrefabName.Length.CompareTo(b.Device.PrefabName.Length);
                if (lengthOrder != 0) return lengthOrder;

                return string.CompareOrdinal(a.Device.PrefabName, b.Device.PrefabName);
            });

            int count = Math.Min(limit, matches.Count);
            var result = new List<DeviceInfo>(count);
            for (int i = 0; i < count; i++) result.Add(matches[i].Device);
            return result;
        }
    }
}
