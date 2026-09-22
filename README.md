Format

The format acts as a container holding:

* a magic string
* a header containing the version number, the engine/mod ID, the map UID, the tick count, the sync hash, and the flags
* Deflate-or-raw sections
* a section table at the end, so that the writer does not have to know the length of the sections in advance

Actors

* Actor gained a forcedActorID ctor param and Initialize(addToWorld, restoring).
* World.RestoreActor rebuilds each actor via World.NextAID(forcedID), which rejects reused/stale IDs.
* Placement replays through the normal init path via LocationInit / CenterPositionInit / SubCellInit / FacingInit, plus two new inits: RestoringInit, SpawnedByMapInit.

Suppression flag

* World.IsRestoringSnapshot must be set before the world loads (throws otherwise).
* Over 15 calls check it to skip one-time side effects on restore: FreeActor, Crate, Harvester, Husk, CrateSpawner, SpawnMapActors, SpawnStartingUnits, ProducibleWithLevel, ActorSpawnManager, LegacyBridgeLayer, LoadWidgetAtGameStart.
* Actor.Initialize also skips ICreationActivity entirely when restoring.

Traits

* New ISaveState (SaveStateInfo, SaveState → List, LoadState), keyed by SnapshotTraitKey = actorId/traitTypeName[@instanceName].
* 46 implementers in Mods.Common.
* Plus INotifyStateRestored for post-load recompute.

Activities

* Activity.SaveState (virtual) + internal SaveBaseState / RestoreBaseState / RestoreLinks.
* New interfaces IActivityReferences, IProvidesMovePath.

Effects

* ISaveableEffect + IRequiresRestoredReferences.
* DelayedImpact is the reference example — a closure-based state machine effect with a (World, SnapshotReader, MiniYaml) restore ctor.

Projectiles / warheads

* IWarhead.IsValidAgainst changes from Actor firedBy to Player firedBy (the firing actor may not exist after restore, but the player always does).
* New IProjectileSource / IProjectileScriptInfo, plus ProjectileArgsCodec / WarheadArgsCodec / WeaponRefs.
* ArmamentProjectileSource rebuilds a muzzle from actor + trait instance name + barrel index, falling back to a frozen source over the saved position.

Netcode

* Protocol 21→22.
* New orders: RequestSnapshot, SaveSnapshot, SnapshotChunk, SnapshotChunkEnd, SnapshotReceived, SnapshotLoadFailed, SnapshotSaveFailed, SnapshotStartFailed, LoadGameSave.
* Server selects a save point (max(LastOrdersFrame) + OrderLatency + 10) ensuring client synchronization.
* Files transmit in parts through BlobReassembler.
* Server discards them if sync hash / defeat state mismatches occur.
* Loading halts until every client acknowledges; thirty-second timeouts revert the lobby.

Lua

* Lacks a core heap serializer — ILuaStateSnapshotCodec uses LUA_SNAPSHOT flag wrapping external Eluant.LuaSnapshot.
* Missing bindings trigger LuaStateSnapshotRefusedException, caught by World.WriteSnapshot, deprecating legacy replay saves.
* Mod layer: ScriptUserdataCodec stores actors / players / positions / colours / bound methods as MiniYaml; deceased actors turn into ActorTombstone handles.

Conditions

Conditions skip serialization — actors rebuild normally, suppressing side effects via IsRestoringSnapshot, then regain grants in StateRestored (Cloak.cs:397-404), being per-world runtime handles, not data.

Object graph recovery

* Involves serializing identifiers plus lookups, deferring forward references.
* Identity format reads A:, P:, F:<viewer>:<id>, C:, TP:.
* Actor targets include generation counters preventing recycled IDs resolving wrongly.
* Weapons / warheads store as rule keys (weaponKey:index) — never serialized, since rules objects remain immutable/shared.

SnapshotReader queues

csharp

Copy

readonly List<(uint ActorID, Action<Actor> Resolve)> deferredActorRefs = [];
readonly List<Action> deferredCompletions = [];

* RunDeferred() processes actor refs first, then completions.
* GetActorById checks reader's restoredActors map before World.GetActorById, since mid-restore actors exist but lack world presence.
* DeferTarget delays only when values name actors — cells / positions resolve instantly.

Other graph edges

* Activity trees numbered by ActivitySerializer, traversing childActivity / nextActivity, writing nodes per activity keyed by int, using Child / Next as numeric refs with Root pointers. Restore builds every node initially, connects them later — avoids forward-reference issues.
* Lua closures turn into integer handles inside ScriptContext.handles, fetched again after loading.
* C# delegates cannot serialize — Animation uses this model: store a SequenceState entry, then call PlayThen / PlayRepeating to reconstruct the tick delegate and apply saved counters onto it.

Verification

* World.SyncHash() recalculates after restore and matches against the header.
* Because ISync fields hash, missing ones show up as mismatches.
* This explains why savers include actors leaving worlds yet holding ISync state (world.Actors ∪ world.ActorsHavingTrait<ISync>()), and why Settings.Debug.SnapshotDiagnostics might print a Diff segment listing actor / trait during mismatch.

Restore sequence

The restore sequence within WorldRestorer.Restore forms a documented agreement, each phase warranted by reliance:

1. check header
2. spawn actors
3. ingest pre-script mass data
4. execute deferred map Lua block
5. individual-actor trait status plus actions plus join world
6. world / player condition
7. impacts
8. RunDeferred()
9. leftover mass data
10. StateRestored
11. tick plus RNG jointly
12. sync-hash validation

IWorldSaveState

IWorldSaveState differs from ISaveState, requesting solely from world actor and player actors:

csharp

Copy

public interface IWorldSaveState
{
    string SectionName { get; }
    void SaveState(Actor self, Stream s, SnapshotWriter w);
    void LoadState(Actor self, Stream s, SnapshotReader r);
}

ISaveState vs IWorldSaveState

* ISaveState provides a trait with a List within its actor's YAML block.
* IWorldSaveState grants a trait its specific named compressed section, acting as a raw Stream.

What it handles

This handles data too vast or malformed for individual actor YAML:

* Shroud — comprises four ProjectedCellLayer arrays and six flags, written directly via BinaryWriter.
  * Each player gets one section: "Shroud/" + owner.InternalName.
* ResourceLayer
* SmudgeLayer ("SmudgeLayer/" + Info.Type)
* MapLayerSnapshot ("MapLayers")
* PowerManager
* SpawnMapActors ("MapActors")
* LuaScript ("Script")

Why streams

* Streams bypass base64-through-YAML for binary loads.
* Allow readers to skip sections using the table.
* Permit compression per section.

Three ordering hooks

1. ILoadBeforeDeferredScript — map Lua chunks read the actor list at their root, requiring SpawnMapActors to load first.
2. IMapActorRoster — name-to-actor-id mapping plus id ranges, enabling restored actors to receive SpawnedByMapInit.
3. IDeferScriptUntilRestored — LuaScript builds its ScriptContext with an empty script list, then executes the chunk there.

4. Purpose of ActivityRegistry

It serves as a name to restore-constructor table across all Activity subclasses known by the mod's ObjectCreator, filtered by [SaveableActivity]:

* Ctor signature remains fixed: (Actor, SnapshotReader, MiniYaml) — the reader is mandatory so the ctor can delay references.
* Constructed once per ModData, from ObjectCreator.GetTypes(), ensuring coverage of every mod assembly.
* Verification occurs at startup, not during restoration: a flagged type lacking a restore ctor fails instantly, and duplicate names fail similarly.
* --check-activity-restore enumerates violations.
* NameOf outputs the type identifier (inner types named Declaring+Nested); Create mirrors and resends errors as InvalidDataException.

Thus it acts like a polymorphic label for actions. This happens since restoring cannot call standard constructors — activity builders often queue kids, make iterators, or hold pathfinder/delegate info (Move's Func<BlockedByActor, (bool, List<CPos>)> getPath fits perfectly).

EffectRegistry serves IEffect similarly using (World, SnapshotReader, MiniYaml).

One point to note, as it hurts

* ActivitySerializer discards the full tree (DroppedTrees++) if one action fails saving or repeats a key — a broken tree leaves dead links to missing items.
* Move.SaveState gives null for MoveSearch.Custom moves, so actors stuck mid-custom-move return idle silently.
* A similar form applies to effects: DroppedEffects counts because SyncHash computes sync effect hashes via location, thus losing one changes all later hashes.

Two process points

* I made and deleted temp git worktrees inside .worktrees/ to view branches.
* The noted file remains the sole leftover item. No commits occurred.
