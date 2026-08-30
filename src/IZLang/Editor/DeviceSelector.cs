using System;

namespace IZLang.Editor
{
    /// <summary>
    /// What a batch declaration points at, taken apart: 'all(StructureDiode)' names a
    /// prefab, 'named("led-dev")' names a label, and 'named(StructureDiode, "led-dev")'
    /// names both.
    ///
    /// Either half can be missing - the line is often still being typed - and that is
    /// exactly what the editor has to work with when it tries to tell which equipment
    /// the device stands for.
    /// </summary>
    public readonly struct DeviceSelector : IEquatable<DeviceSelector>
    {
        /// <summary>The prefab as it was written, or null when only a label was given.</summary>
        public string? PrefabName { get; }

        /// <summary>The label between quotes, or null for 'all(...)'.</summary>
        public string? Label { get; }

        public DeviceSelector(string? prefabName, string? label)
        {
            PrefabName = string.IsNullOrEmpty(prefabName) ? null : prefabName;
            Label = string.IsNullOrEmpty(label) ? null : label;
        }

        /// <summary>Nothing to go on: neither a prefab nor a label was written.</summary>
        public bool IsEmpty => PrefabName == null && Label == null;

        public bool Equals(DeviceSelector other) =>
            string.Equals(PrefabName, other.PrefabName, StringComparison.Ordinal) &&
            string.Equals(Label, other.Label, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is DeviceSelector other && Equals(other);

        public override int GetHashCode() =>
            (PrefabName != null ? PrefabName.GetHashCode() : 0) * 397 ^
            (Label != null ? Label.GetHashCode() : 0);

        public override string ToString() =>
            (PrefabName ?? "?") + (Label != null ? " \"" + Label + "\"" : string.Empty);
    }
}
