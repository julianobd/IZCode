# Roadmap

State as of 2026-08-28. v0.1 closes the language and the VM; what is left is
almost entirely integration with the game.

## Known gaps in v0.1

### 1. The VM state does not survive a save

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

### 2. Multiplayer has not been tested

The source is replicated by the game, so in theory every machine compiles and
runs the same thing. But the VM runs on the server and on the client with no
state synchronization between them, and divergence has not been tested.
Determinism depends on `rand()` (a different seed per machine) and on the
iteration order of batch operations.

## Language ideas for later

None of this is needed for v0.1 to work.

- explicit batch aggregation: `sum(all(X).Power)` beyond the implicit average
- `import` from another chip, for shared libraries
- unreachable code warning (the `UnreachableCode` error code is already
  reserved; `UnusedVariable` is out, and the editor's error panel shows it)
- a peephole optimizer over the bytecode (there is none today)
