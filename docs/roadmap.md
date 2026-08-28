# Roadmap

State as of 2026-08-28. v0.1 closes the language and the VM; what is left is
almost entirely integration with the game.

## Known gaps in v0.1

### 1. The prefab hash has not been checked against a running game

`PrefabHash.Compute` uses CRC-32/ISO-HDLC, the algorithm the community
documents for Stationeers, and it passes the standard's canonical check value
(`"123456789"` → `0xCBF43926`). But **it has not been confirmed against a real
prefab inside the game**: no Stationeers data file was found that carries a
name/hash pair to compare offline.

This affects `#"PrefabName"`, `all(...)` and `named(...)`. The rest of the
language does not depend on it.

How to validate it: place a Logic Hash Gen in the world, read the hash of a
known prefab and compare it with `PrefabHash.Compute("...")`. If they diverge,
the fix is localized to just that one file.

### 2. The VM state does not survive a save

`SourceCode` is stored by the game, so the program comes back after loading,
but recompiled and restarted from scratch. Global variables and the execution
position are lost.

IC10 saves its registers and `_NextAddr`, so an IC10 chip resumes where it
stopped and an IZ chip does not. That is a real regression against vanilla for
programs that accumulate state.

The way forward: serialize globals + stack + frames into an extended
`ThingSaveData`, or squeeze them into the existing register array. The heap
goes with them now: the arrays and structs of the live frames are state just
like the locals are. So does the string table, and it is the awkward one - a
str slot holds a handle, which means nothing without the table that gives it a
meaning, so the text has to be written alongside and the handles remapped on
the way back in.

### 3. Error panel in the editor

Syntax highlighting **is done** (`IZLang/Editor/SyntaxHighlighter.cs` +
`Patches/SyntaxHighlightPatches.cs`, and the motherboard screen in
`Patches/MotherboardScreenPatches.cs`). What used to be the visible symptom
(a whole IZ program in red, `#"Prefab"` eaten by comment gray) came from the
editor painting with the IC10 highlighter, which treats whatever it does not
recognize as an error and `#` as the start of a comment.

What is still missing is the message: compile errors **do** reach the editor
through the chip's error line, but the detailed text only goes to the log:
the player sees the line, not the reason.

### 4. Multiplayer has not been tested

The source is replicated by the game, so in theory every machine compiles and
runs the same thing. But the VM runs on the server and on the client with no
state synchronization between them, and divergence has not been tested.
Determinism depends on `rand()` (a different seed per machine) and on the
iteration order of batch operations.

### 5. The editor tools have never run in the game

Completion and hover are split in two halves. The one that decides what to show
(`IZLang/Editor/`) is pure code and has test coverage. The one that draws it
(`IZCode.Mod/UI/`) compiles against the real assemblies, and the members it
reaches by reflection were checked against `Assembly-CSharp.dll`, but it has
never executed.

The places where trouble is most likely, in order:

1. **Detecting the character under the mouse.**
   `TMP_TextUtilities.FindIntersectingCharacter` needs generated geometry. We
   use the line's `InputText`, which is the raw text (its indices match the
   source), but it may not be getting drawn. There is a defensive
   `ForceMeshUpdate()`; if that is not enough, the tooltip simply does not show
   up.
2. **The Tab key.** The game's `TMP_InputField` may consume Tab before the
   overlay does. The arrow keys were left out for exactly that reason:
   fighting the text field over the caret would give unpredictable behaviour. If
   Tab does not work, swapping it for another key is a one-liner.
3. **Popup positioning.** It anchors to the corner of the current line, not to
   the character: following the character would need the same geometry as item
   1. It ends up aligned to the left of the line instead of under the caret.
4. **Installation order.** The overlay is installed in the editor's
   `Initialize`, with `SetVisible` as a safety net in case the mod loads later.

The overlay's `Update()` is wrapped in a try/catch with a throttled log; ten
consecutive failures switch the component off instead of dumping an exception
every frame.

One problem on this list **was found and fixed** before running: the overlay
used to hang off a `GameObject` with a plain `Transform` under the canvas. Since
`anchoredPosition` is measured against the parent's rectangle, and a `Transform`
has no rectangle, the panels would never have landed in the right place. Today
the host is a `RectTransform` stretched over the canvas.

### 6. The prefab scan has never run

`CatalogScanner` probes hundreds of prefabs against 358 `LogicType`s. Each probe
is individually guarded because `CanLogicRead` is virtual and implemented by
hundreds of classes, some of them assuming world state that does not exist on a
loose prefab.

The real question is one of **timing**: the scan runs on `Prefab.OnPrefabsLoaded`,
and `Device.CanLogicRead` starts with
`if (!IsStructureCompleted && GameManager.GameState == GameState.Running) return false;`.
During loading the state is not `Running`, so the guard should pass and the real
capabilities should show up. If that reasoning is wrong, the catalog comes out
empty or incomplete, and then `izcode_devices rescan` with a world loaded is
plan B.

## Next steps, in order of value

1. **Validate the prefab hash in-game**: small, and it unblocks `all`/`named`.
2. **Persist the VM state**: closes the regression against IC10.
3. **Error panel in the editor**: the highlighting is out; showing the
   diagnostic message, not just the line, is what is left.
4. **Test on a dedicated server**: decide whether the VM runs on the server
   only.

## Done since v0.1

### `struct` and arrays

The VM gained a heap: `IZLimits.HeapSize` cells, addressed by frame. Each
function reserves what its own declarations need on the call and gives it back
on the return, so a recursive call gets its own arrays and a declaration inside
a loop reuses the same cells, cleared again, instead of leaking.

The layout is decided at compile time - there is no allocator to call and
nothing to collect - which is what pays for the two rules the language enforces:
a function cannot return an aggregate, and an aggregate variable is bound once,
at its declaration. Together they make it impossible for an address to outlive
the cells behind it.

What is left as a deliberate limit: the length is part of the type, so a
function taking `num[3]` does not accept a `num[4]`, and there is no way to
write one that works over any length. Lifting that would mean carrying the
length at runtime, which is a header cell on every array and a bounds check the
compiler can no longer fold. See section 10 of the language spec.

### Runtime `str`

A `str` used to be a hash the compiler folded away: `"north"` was gone by the
time the chip ran. It is a value now - joined with `+`, compared with the six
operators, passed to and returned from functions, held in an array cell or a
struct field - which is what makes a group of devices addressable by a label the
program itself assembles: `named("vent-" + wing)`.

The representation is what kept it from costing anything elsewhere: a str is a
handle into the VM's string table, NaN boxed into the same `double` every slot
already was. No new storage, no second kind of variable, and arithmetic never
touches one because the compiler refuses to mix a str with a num.

Text built at runtime is bounded by two rules instead of an allocator: the same
text always interns to the same slot, so a loop rebuilding it allocates once;
and when the table fills, a mark and sweep frees what the stack, the locals, the
globals and the heap no longer point at. NaN boxing is what makes that sweep
exact - it is how a handle is told apart from a number inside arrays that carry
no type tag. It only runs when there is no room left, so an ordinary tick never
pays for it.

What is deliberately left out: text still cannot be written to a device. The
game's logic network carries numbers, so `hash(s)` is the whole bridge between a
str and the world. If a way to show text in game ever turns up (a display, a
label the mod can set), that is where it would plug in.

## Language ideas for later

None of this is needed for v0.1 to work.

- explicit batch aggregation: `sum(all(X).Power)` beyond the implicit average
- `import` from another chip, for shared libraries
- unreachable code warning (the `UnreachableCode` error code is already
  reserved; `UnusedVariable` is out, and the editor's error panel shows it)
- a peephole optimizer over the bytecode (there is none today)
