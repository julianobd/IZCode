# Device catalog

IZCode keeps a list of every prefab in the game that responds to logic, and of
which properties each one accepts, with the distinction between reading and
writing. It is what feeds completion and hover in the editor.

## Why it is generated inside the game

That table cannot be extracted from `Assembly-CSharp.dll`. `CanLogicRead` and
`CanLogicWrite` are instance methods that query prefab state (`HasOnOffState`,
`HasPowerState`, `HasColorState`…), and that state is serialized in Unity's
asset bundles, not in the code:

```csharp
// Assets.Scripts.Objects.Pipes.Device
public virtual bool CanLogicRead(LogicType logicType) {
    switch (logicType) {
        case LogicType.On:    return HasOnOffState;   // <- comes from the prefab, not the DLL
        case LogicType.Power: return HasPowerState;
        ...
```

So the mod walks `Prefab.AllPrefabs` at runtime and probes each of the 358
`LogicType`s against every device. It takes a few seconds, and it happens **once
per game version**: after that the result comes from disk.

## Where the files live

```
Documents\My Games\Stationeers\izcode\
├── devices.txt     the format the mod reads
└── devices.json    the same information, for external use
```

They live in the user's folder, not the mod's: a mod coming from the Workshop
may sit in a read-only directory, and the catalog depends on the installation:
another mod that adds devices changes the result.

## When it is regenerated

- automatically, when the game version changes;
- automatically, if the file disappears or is corrupted;
- on demand, from the console: `izcode_devices rescan`.

`izcode_devices` on its own loads whatever is there and prints the summary and
the paths. Installing another mod that adds devices does **not** change the game
version, so in that case `rescan` is required.

## The `devices.txt` format

TAB separated lines, one entity per line. Chosen over JSON because the file has
thousands of lines, has to load fast during startup, and stays readable in a
diff when Stationeers updates.

```
V   1                                    format version
G   0.2.5678.22                          game version it was generated from
D   PrefabName    hash    slots   Name   starts a device
P   Name    logicType     rw             property  (rw | r | w)
S   Name    logicSlotType                slot property (always read only)
```

`P` and `S` lines belong to the last `D`. Blank lines and lines starting with
`#` are ignored. Example:

```
V	1
G	0.2.5678.22
D	StructureVolumePump	-321403609	0	Volume Pump
P	On	28	rw
P	Setting	12	rw
P	Pressure	5	r
D	StructureChuteInlet	1305252611	2	Chute Inlet
P	On	28	rw
S	Quantity	3
S	OccupantHash	2
```

The reader is forgiving: a malformed line is skipped rather than invalidating
the file, because a partial catalog still gives useful completion and the file
may have been truncated by a crash in the middle of a write. A different format
version, on the other hand, returns empty on purpose, at which point the mod
regenerates instead of reading something it does not understand.

## The `devices.json` format

The same information, for a wiki, an external tool or manual lookup. The mod
never reads this file; it exists only for you.

```json
{
  "formatVersion": 1,
  "gameVersion": "0.2.5678.22",
  "deviceCount": 412,
  "devices": [
    {
      "prefabName": "StructureVolumePump",
      "prefabHash": -321403609,
      "displayName": "Volume Pump",
      "slotCount": 0,
      "properties": [
        {"name": "On", "logicType": 28, "read": true, "write": true},
        {"name": "Pressure", "logicType": 5, "read": true, "write": false}
      ],
      "slotProperties": []
    }
  ]
}
```

## What the catalog unlocks

**Completion that knows the equipment.** The editor is opened by the
Programmable Chip Motherboard, which knows which CircuitHousing is selected,
and the housing knows what is wired to each pin. Put together with the catalog,
typing `pump.` suggests the properties of **that pump**, with its current value,
instead of all 358 in the game.

**Hover with real values.** Hovering a device variable shows the equipment, the
label, the hash and the current value of every readable property.

**A prefab typo becomes a warning.** `#"StructureVolunePump"` produces a
perfectly valid hash that matches nothing and would fail silently. With the
catalog loaded, the hover says "no prefab exists with this name".
