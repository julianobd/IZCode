# StationeersIZCode

A Stationeers mod that replaces IC10 with **IZ**: a structured language, with
types, real functions and recursion, running on its own virtual machine inside
the Programmable Chip.

```iz
#iz
device sensor = d0;
device inlet  = d1;

const TARGET = 101.325;
const MARGIN = 5.0;

fn main() {
    loop {
        var p = sensor.Pressure;
        inlet.On = p < TARGET - MARGIN;
        yield;
    }
}
```

## What changes compared to IC10

| | IC10 | IZ |
|---|---|---|
| Code limit | 128 lines | bounded only by the source size |
| Registers | 16, managed by hand | named variables, allocated by the compiler |
| Functions | `jal`/`ra`, one practical level | `fn` with parameters, return values and recursion |
| State between ticks | restarts from the top every tick | **the VM freezes and resumes where it stopped** |
| Infinite loop | freezes the chip | preempted by budget, the game carries on |
| Wrong property name | you find out at runtime | compile error, with a suggestion for the right name |
| Types | everything is a `double` | `num`, `bool`, `str`, `dev` checked at compile time |
| Text | `HASH("name")`, and the text is gone | a real `str`: joined, compared and hashed while it runs |
| Data structures | 16 loose registers | `struct` and arrays, in a heap the compiler lays out |

The line that matters most is *State between ticks*: **the IZVm is
preemptive**. A `loop { }` with no `yield` freezes nothing: the VM runs its
instruction budget, hands the tick back to the game, and carries on at the next
instruction on the following tick.

The one below it is newer. In IC10 a piece of text only ever became a number:
`HASH("name")` at compile time, and the text itself was gone. In IZ a `str` is a
value like any other, so the devices a program talks to can be chosen while it
runs:

```iz
var side = "north";

named("vent-" + side).On = true;          // hashed on the spot
all("Structure" + kind).Setting = 0;      // so is a prefab name
if label == "vent-north" { ... }          // compares the text, not an address
```

It joins with `+`, compares with the six operators, and comes with a small text
library (`text`, `sub`, `find`, `chr`, `char`, `fixed`, `parse`, `hash`, `len`).
A literal label still folds to its hash at compile time, so nothing that was
free before became expensive. See section 11 of
[docs/language-spec.md](docs/language-spec.md) and `samples/text.iz`.

## How it works inside

```
.iz source
    │
    ├─ Lexer ────────► tokens
    ├─ Parser ───────► AST
    ├─ Compiler ─────► bytecode + function table        (resolves names, checks types)
    │
    └─ IZVm ─────────► runs N instructions per tick, then freezes the state
                          │
                          └─ IDeviceHost ──► the game's CircuitHousing
```

The separation the project rests on: **`IZLang` references neither Unity nor
`Assembly-CSharp`**. All contact with the game goes through the `IDeviceHost`
interface. That is why the compiler and the VM are entirely testable without
opening Stationeers: the 439 tests run in ~40 ms.

### Why reuse the Programmable Chip

The mod does not create a new item. A chip becomes IZ when the first line of the
source is the `#iz` marker; without the marker, IC10 behaviour is untouched.

That is deliberate: it inherits the in-game editor, saving the code into the
save file, and multiplayer replication for free. A new prefab would need a Unity
asset bundle and a reimplementation of all of that. As a bonus, `#iz` starts
with `#`, which the IC10 parser treats as a comment, so a chip carrying IZ code
does not break the game for someone without the mod installed.

The chip's behaviour comes down to just two Harmony grafts, both on
`ProgrammableChip`:

- **`SetSourceCode` (postfix)**: compiles the IZ and undoes the error the IC10
  parser inevitably produced.
- **`Execute` (prefix)**: runs the IZVm in place of the IC10 interpreter.

The other three are interface only, and none of them changes behaviour, only
what shows on screen: `InputSourceCode` to hang the overlay off,
`EditorLineOfCode` and `ProgrammableChipMotherboard` to paint IZ code with the
right highlighter.

## Tools in the game's editor

Once you write `#iz`, the code editor gains four things.

**A real code area.** The Stationeers editor is not a text field: it is 128
independent `TMP_InputField`s, one per line. That is what prevents dragging the
mouse across three lines, pressing `Ctrl+A` over the whole program or cutting a
block: each field only sees its own line.

In IZ mode it is replaced by **a single, multi-line field with a dark gray
background**, and all of that becomes native behaviour: selecting by dragging
the mouse, `Shift`+arrows, double clicking a word, `Ctrl+A`/`C`/`X`/`V`,
`PageUp`/`PageDown`, the mouse wheel, a line-number gutter and a current-line
highlight.

Along with it comes VS Code's automatic indentation:

| key | what it does |
|---|---|
| `Enter` | repeats the line's indent; goes in one level if it opened a block |
| `Enter` between `{` and `}` | opens both lines and leaves the caret in between |
| `}` on a blank line | goes back one level, to line up with the opening brace |
| `Tab` / `Shift+Tab` | shifts the whole selected block |

The original 128 lines do not go away: they become invisible and get the text
back on every change, so `Copy()`, the save button, the byte count and the chip
keep working as before. Deleting the `#iz` gives the game's editor back, whole,
with the caret on the same line. And if building the panel fails for any reason,
the original editor never leaves the stage.

**Completion that knows your wiring.** The editor is opened by the Programmable
Chip Motherboard, which knows which CircuitHousing is selected, and the housing
knows what is wired to each pin. Typing `pump.` suggests the properties of
**that pump**, with its current value alongside, instead of all 358 in the game:

```
pump.|
      On          rw
      Setting     rw  = 45
      Pressure    r   = 101.325
```

It also completes pins (`device x = ` shows what is on each one), prefab names
inside `all(...)` and `#"..."`, slot properties, and the names declared in the
program itself.

`Tab` accepts the suggestion, `Ctrl`+arrows move through the list, `Esc` closes
it, `Ctrl+Space` opens the full list. With the list closed, `Tab` goes back to
indenting. On a blank line, with nothing typed, the list does **not** open by
itself: dumping the whole vocabulary over the code gets in the way more than it
helps. After a `.`, after `device x = ` or after `all(`, it does open: there
the context is already the request.

**Compile errors while you type.** The same compiler the chip runs on CONFIRM
compiles what is on screen, with a 0.35 s breath after the last keystroke. Lines
with problems get an underline (red for an error, amber for a warning), and a
panel at the bottom says what it is:

```
error line 5     a batch write takes num (or bool), not str
warning line 2   const 'LED' was declared and never used
```

The goal is for `all(DISPLAY).Setting = "OK"` to be caught right there, not on
CONFIRM.

**Hover with real values.** Hovering a device variable shows the equipment, the
label, the hash and the value of every readable property at that moment. Over a
property, it says whether that device accepts it and whether it is read only.
Over a `#"..."`, it shows the hash and warns when the prefab does not exist.

That is fed by the [device catalog](docs/device-catalog.md), scanned from the
game once per version and written to
`Documents\My Games\Stationeers\izcode\` as `devices.txt` and `devices.json`.
The console has `izcode_devices rescan` to force it.

## Structure

```
src/IZLang/            compiler + VM, with no game dependency (netstandard2.0)
  Diagnostics/           spans, errors with line/column and a caret
  Lexing/                tokens
  Parsing/               AST and recursive descent parser
  Binding/               symbols, types, bytecode emission
  Vm/                    opcodes, program, IZVm, IDeviceHost
  Devices/               device catalog and its file format
  Editor/                completion, hover, highlighting and indentation - pure, no Unity
src/IZCode.Mod/        the game layer: Harmony, device host, UI (net472)
  Diagnostics/           log and on-disk paths - the switch lives here
  Patches/               the grafts onto the game
  Runtime/               per-chip VM, bridge to the housing
  Devices/               prefab scanning, on-disk cache
  UI/                    IZ code area, completion, tooltip and error panel
tests/IZLang.Tests/    439 tests
tools/GenLogicTypes/   generates the LogicType table from the game
samples/               example .iz programs
docs/                  language specification, catalog, roadmap
```

The same boundary that already held for the VM holds for the editor tools:
**what decides is pure code in `IZLang`, what draws is Unity in `IZCode.Mod`.**
The completion and hover engines take `(text, offset, IEditorEnvironment)` and
return a list, so the whole behaviour is tested without opening the game, and
the blind part is only the drawing.

## Build

Requires the .NET SDK and an installed copy of Stationeers.

```bash
dotnet test                      # runs the tests
pwsh build/pack.ps1              # assembles dist/IZCode/
pwsh build/pack.ps1 -Deploy      # installs into the game's mods folder
```

If the game is not in the default Steam path:

```bash
dotnet build -p:StationeersDir="D:\Steam\steamapps\common\Stationeers"
```

### After a Stationeers update

The device property table (`LogicType`) is generated from the game itself. When
Stationeers updates, regenerate it:

```bash
dotnet run tools/GenLogicTypes/gen.cs
```

## Installation

**IZCode is a C# plugin, and Stationeers does not load C# on its own.** The game
only reads `GameData` (XML and prefabs) from mods; nothing in
`mods\IZCode\*.dll` runs without a loader. So, before the mod, these have to be
installed:

1. **BepInEx** (the loader; it puts `winhttp.dll` and the `BepInEx\` folder in
   the game root, next to `rocketstation.exe`);
2. **[StationeersLaunchPad](https://github.com/StationeersLaunchPad/StationeersLaunchPad)**,
   which runs on top of BepInEx and is what calls the mod's `OnLoaded()`.

With both in place, `build/pack.ps1 -Deploy` copies the mod to
`Documents\My Games\Stationeers\mods\IZCode`; from there just enable it in the
mods menu.

### Checking that it loaded

Open the game and look for `[IZCode]` in
`%USERPROFILE%\AppData\LocalLow\Rocketwerkz\rocketstation\Player.log`:

```
[IZCode] ======================================================
[IZCode] IZCode 0.1.0.0 starting through OnLoaded (StationeersLaunchPad)
```

That line always comes out, even with the log switched off. If it is not there,
the mod was never even called: the problem is in the loader, not in IZCode. If
it is there, the rest of the diagnosis is in
`Documents\My Games\Stationeers\izcode\izcode.log`.

## Log

Everything the mod does goes through `IZLog`, and the switch lives in one place:
`Documents\My Games\Stationeers\izcode\log.cfg`, created on the first run.

```
enabled=true
level=info                       # off | error | warn | info | debug | trace
areas=load,chip,vm,editor,completion,catalog
file=true                        # also write to izcode.log
```

The subjects exist so only the part being investigated can be switched on:
`hover` and `highlight` talk on every frame and every keystroke, which is why
they start off. Inside the game, the console does the same without leaving the
session:

```
izcode_log                          shows the current state
izcode_log off                      switches everything off
izcode_log level debug              talks more
izcode_log areas completion,editor  only completion and the overlay
izcode_log path                     where the log and the config are
```

Every command rewrites `log.cfg`, so what you choose applies to the next session
too.

## Current state

Working and covered by tests:

- lexer, parser, type checking, bytecode and VM, all complete
- `if`/`else`/`while`/`loop`/`for`/`break`/`continue`
- functions with parameters, typed return values and recursion
- `struct` and fixed length arrays, with a heap that is laid out at compile time
  and released when the call that declared it returns
- device reads and writes, slots, batch operations by hash and by label
- `yield`, `sleep`, preemption by budget
- a runtime `str`: `+`, `+=`, the six comparisons, and a text library, with a
  string table that interns what repeats and collects what nobody points at
- 33 native functions (`abs`, `sqrt`, `clamp`, `text`, `sub`, …), and `len` over
  an array or a string
- errors with line, column, source snippet and a name suggestion
- device catalog: format, reading that tolerates a truncated file, JSON export
- completion and hover engines, including the offset seam with the editor
- completion of struct fields, following `t.reading.` and `ps[0].` through the
  declared types
- a warning for a name declared and never used (and its silence when there is
  already an error)
- IZ syntax highlighting, including the `#"Prefab"` that IC10 read as a comment
- automatic indentation: `Enter`, `}` and `Tab`/`Shift+Tab` over a selected block
- logging with level and subject, switchable by file or from the console

Written but **not tested inside the game** (see
[docs/roadmap.md](docs/roadmap.md)):

- the prefab scan that generates the catalog
- the IZ mode code area (the single field that replaces the 128 lines)
- drawing the completion popup, the tooltip and the error panel
- the Harmony grafts (they compile against the real assemblies, but have never
  run)

Not done yet:

- persisting the VM state in the save
- validating the prefab hash against a running game
- multiplayer testing
