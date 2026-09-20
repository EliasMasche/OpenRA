--[[
   Copyright (c) The OpenRA Developers and Contributors
   This file is part of OpenRA, which is free software. It is made
   available to you under the terms of the GNU General Public License
   as published by the Free Software Foundation, either version 3 of
   the License, or (at your option) any later version. For more
   information, see COPYING.
]]

-- Test map for Lua script state in snapshots. Every construct below is one a snapshot has to carry, and
-- each is observable from the printed heartbeat: after a load the counters continue from their saved
-- values rather than restarting, which is only true if the closures came back sharing one cell.
--
--   bin/OpenRA.Utility.exe ra --verify-snapshot mods/ra/maps/lua-snapshot-test 20
--
-- What it exercises, and where the state lives:
--   * a shared upvalue cell between two closures (the binding joins the cells on load)
--   * scalars, a nested table, a table of actor references and an array grown with #
--   * a per-actor OnKilled closure, held in ScriptTriggers
--   * a pending AfterDelay closure, held in LuaDelayedCall as a handle id
--   * a CallFunc queued on an actor behind a Wait, held in CallLuaFunc as a handle id
--   * a footprint trigger, whose id the script keeps and hands back to remove it
--   * a Player and Actor reference captured by a closure, which the codec writes as an identity
--   * a reference to an actor that is already dead at save time, which the codec writes as a tombstone
--   * a map actor read at the top level of the chunk, which is how a real mission opens and the only
--     way to catch the globals being installed too late (see TopLevelLocation below)

-- A map actor read at chunk level, before any function is defined. Every ported campaign script opens
-- like this -- `InsertionPath = { InsertionEntry.Location, ... }` -- and it is the one construct that
-- fails when the map actor globals are not yet installed at the moment the chunk runs. A headless
-- restore caught this where a guarded reference inside WorldLoaded did not: the chunk runs during
-- LoadComplete, and a restore rebuilds the world after it.
--
-- Deliberately not behind a nil check. A missing global has to raise here rather than be absorbed, which
-- is the entire point of the line.
TopLevelLocation = TestWalker.Location

-- TickIncrement and TickCounter capture this local. A restore that gave each closure its own cell would
-- leave the counter frozen at whatever it was when the save was taken.
local tickCounter = 0

-- The player comes from an actor's owner rather than a name: the engine registers globals for the mod's
-- binding tables and for map actors, and none for players.
local player

TicksSeen = 0
GlobalCounter = 0
BlockerKilled = false
StashedActors = {}
Watched = {}
-- Set from the OnKilled handler below, which is how a mission normally comes to hold a dead actor.
DeadActor = nil
DeadActorSeen = "none"
Nested = { inner = { value = 0, label = "start" }, actor = nil }
-- Set in WorldLoaded to actor.Hunt, which is a bound CLR method rather than a Lua function.
BoundMethod = nil
BoundMethodCalled = false

TickIncrement = function()
	tickCounter = tickCounter + 1
	GlobalCounter = GlobalCounter + 1
end

TickCounter = function()
	return tickCounter
end

local function announce(text)
	Media.DisplayMessage(text)
end

-- The sandbox in ScriptContext removes `error` and `assert` from the globals, so a check that has to
-- stop the run raises by indexing a nil instead. ScriptContext.FatalError reports the message and
-- ends the game, which is what turns a heap-only mismatch into a failure the harness reports.
local function fail(message)
	local raise = nil
	return raise[message]
end

WorldLoaded = function()
	player = TestWalker.Owner

	PrimaryObjective = player.AddPrimaryObjective("Keep the script alive, then save and reload.")
	SecondaryObjective = player.AddSecondaryObjective("Kill TestBlocker.")

	-- Actor references reachable only through the script's own tables, so the heap walk has to write them
	-- as identities rather than rediscover them from the globals table.
	Nested.actor = TestBlocker
	Nested.inner.value = 1
	StashedActors[1] = TestGuardA
	StashedActors[2] = TestGuardB
	StashedActors[3] = TestGuardC

	Utils.Do({ TestWalker, TestBlocker }, function(a)
		Watched[#Watched + 1] = a
	end)

	-- Fires at tick 25, after a save taken at 20. A restore that brought the heap back but left this
	-- trigger unregistered looks correct at the save tick and never runs this, which is the silent
	-- failure the wiring exists to prevent.
	Trigger.OnKilled(TestBlocker, function()
		BlockerKilled = true
		player.MarkCompletedObjective(SecondaryObjective)
		announce("TestBlocker died.")
	end)

	-- Driven from Tick rather than AfterDelay, so the kill itself survives the restore and the trigger
	-- above is reached at all. Keyed off TicksSeen, which is heap state, so both worlds kill on the
	-- same tick. The handler completes an objective, so this also covers MissionObjectives round
	-- tripping: before that trait saved its state, the tick after this one diverged.
	KillBlockerAtTick = 22

	-- Pending at save time in the middle of a mission, which is the case a dropped effect would lose.
	Trigger.AfterDelay(DateTime.Seconds(15), function()
		GlobalCounter = GlobalCounter + 1000
		announce("AfterDelay fired.")

		Utils.Do(StashedActors, function(a)
			if not a.IsDead then
				a.Kill()
			end
		end)
	end)

	-- Killed early, so that a save at tick 20 already holds a reference to an actor the world has
	-- disposed. The handler stashes the actor the way a mission's own OnKilled handlers do.
	Trigger.OnKilled(TestGuardC, function(a)
		DeadActor = a
	end)

	Trigger.AfterDelay(10, function()
		TestGuardC.Kill()
	end)

	-- Due at tick 22, so a save at 20 has it pending and the gate soak of 4 ticks sees it fire. A dropped
	-- AfterDelay leaves DelayedFired false in the restored world while the source world sets it, and
	-- the two counters diverge, which is what makes this observable without a hash.
	DelayedFired = false
	Trigger.AfterDelay(21, function()
		DelayedFired = true
		GlobalCounter = GlobalCounter + 500
	end)

	-- A reinforcement whose units are still in their spawn delay at the save tick, which is what
	-- SpawnActorEffect holds. The units exist but are not in the world yet.
	ReinforcedSeen = 0
	Reinforcements.Reinforce(player, { "e1", "e1" }, { CPos.New(8, 22), CPos.New(9, 22) }, 23,
		function(a)
			ReinforcedSeen = ReinforcedSeen + 1
		end)

	-- A group trigger: holds a shrinking C# list and a fired flag, which used to make a save refuse.
	GroupFired = false
	Trigger.OnAllKilled({ TestGuardA, TestGuardB }, function()
		GroupFired = true
		GlobalCounter = GlobalCounter + 7000
	end)

	-- The returned id is script state: RemoveFootprintTrigger takes it back, so it has to round trip.
	-- A throwaway trigger created first, purely so the one below is not id 0. A restore that reissued
	-- ids from a fresh counter would still produce 0 for the first trigger, and the id check would
	-- pass without testing anything.
	Trigger.OnEnteredFootprint({ CPos.New(2, 2) }, function() end)

	FootprintEntered = 0
	FootprintReportedId = -1
	FootprintId = Trigger.OnEnteredFootprint({ CPos.New(24, 20) }, function(a, id)
		FootprintEntered = FootprintEntered + 1
		FootprintReportedId = id
		announce("Footprint " .. tostring(id) .. " entered by " .. a.Type .. ".")
	end)

	-- Walking the actor in is what fires the footprint trigger, and it leaves a move in flight.
	-- Started after the save tick, so the walker is still crossing the map when the snapshot is
	-- taken and the footprint trigger is still waiting for it in the restored world.
	Trigger.AfterDelay(21, function()
		TestWalker.Move(CPos.New(24, 20))
	end)

	-- A CallLuaFunc queued behind a Wait, so it is still pending on the actor's activity queue at the
	-- save tick. The Wait restores as itself; the CallLuaFunc has to restore its handle to ever run.
	CalledSeen = false
	TestGuardA.Wait(22)
	TestGuardA.CallFunc(function()
		CalledSeen = true
	end)

	-- A bound CLR method in the heap, which is what IdleHunt puts there in 150 mission scripts:
	-- actor.Hunt is not a Lua function but a C closure over ScriptMemberWrapper.Invoke, and it cannot
	-- be written down as bytecode. Saving it records the actor and the member name instead, so the
	-- restored function must be bound to the restored TestWalker rather than the one the save was
	-- taken from. Held in a table so the heap walk has to reach it, not just the trigger registry.
	BoundMethod = TestWalker.Hunt

	print("LUA-TEST: WorldLoaded ran; player=" .. tostring(player) .. " guards=" .. tostring(#StashedActors))
end

Tick = function()
	TicksSeen = TicksSeen + 1
	TickIncrement()

	-- Keyed off TicksSeen, which is heap state, so the restored world reaches this on the same tick the
	-- source world did. The kill fires the OnKilled trigger registered in WorldLoaded, which only runs
	-- if that registration came back as a live handle.
	if TicksSeen == KillBlockerAtTick and not Nested.actor.IsDead then
		Nested.actor.Kill()
	end

	-- Kills the group members one tick apart after the save, so the OnAllKilled trigger has to have
	-- survived the restore, with its group still holding the member that has not died yet.
	if TicksSeen == 23 and not StashedActors[1].IsDead then
		StashedActors[1].Kill()
	end

	if TicksSeen == 24 and not StashedActors[2].IsDead then
		StashedActors[2].Kill()
	end

	-- The kill above is synchronous, so by the tick after it the OnKilled handler must have run. An
	-- unrestored trigger leaves BlockerKilled false here while the actor is dead, and no sync hash
	-- notices: the handler's effects are all heap state. Only this raises.
	if TicksSeen > KillBlockerAtTick and Nested.actor.IsDead and not BlockerKilled then
		fail("the OnKilled trigger did not fire: it was not restored")
	end

	-- Calling the bound method saved in WorldLoaded, once, after the save tick. A restore that lost the
	-- binding leaves BoundMethod nil and this raises; one that rebound it to the wrong world hands Hunt
	-- an actor from the discarded one, which raises inside the call rather than quietly doing nothing.
	if TicksSeen == 24 and not BoundMethodCalled then
		BoundMethodCalled = true
		BoundMethod()
	end

	-- Reading the stashed actor every tick is what a restored tombstone has to survive: a reference that
	-- came back as nil, or as something that cannot answer these, raises here rather than at the load.
	if DeadActor ~= nil then
		local seen = DeadActor.Type .. "/" .. tostring(DeadActor.IsDead) .. "/" .. tostring(DeadActor.Owner.Name)

		-- The first read happens before any save, so DeadActorSeen holds what the live dead actor
		-- answered. A restore that rebuilt it differently changes this, and a script error is the only
		-- failure the harness reports: the values below are heap state, which no sync hash covers.
		if DeadActorSeen ~= "none" and seen ~= DeadActorSeen then
			fail("dead actor came back as " .. seen .. ", not " .. DeadActorSeen)
		end

		DeadActorSeen = seen
	end

	-- Printed rather than announced, so the values are visible in a headless run. A counter that
	-- restarted after a load shows here as a number that went backwards.
	-- The AfterDelay registered in WorldLoaded runs its callback in a frame-end task, so the flag is
	-- only visible from the tick after the one it fires on. Checked from 25 to leave that margin. A
	-- save that dropped the call, or a restore that never resolved its handle, leaves this false, and
	-- nothing in the sync hash covers it: the flag and the counter are both heap state.
	if TicksSeen > 23 and not DelayedFired then
		fail("the pending AfterDelay did not fire: it was not restored")
	end

	-- Both group members are dead by tick 25, so the OnAllKilled trigger must have fired. A restore
	-- that did not re-subscribe it leaves this false while both actors are gone, and no sync hash
	-- covers the difference.
	if TicksSeen > 24 and StashedActors[1].IsDead and StashedActors[2].IsDead and not GroupFired then
		fail("the OnAllKilled group trigger did not fire: it was not re-subscribed")
	end

	-- The CallLuaFunc queued behind a Wait(22) in WorldLoaded runs on tick 23. A restored activity
	-- that never resolved its handle leaves this false, and nothing in the sync hash covers it.
	if TicksSeen > 23 and not CalledSeen then
		fail("the queued CallFunc did not run: its handle was not restored")
	end

	-- The id the footprint callback reported has to be the one the script was handed, or a later
	-- RemoveFootprintTrigger(FootprintId) would remove nothing. A restore that reissued a fresh id
	-- shows up here and nowhere else.
	if FootprintEntered > 0 and FootprintReportedId ~= FootprintId then
		fail("footprint trigger reported id " .. tostring(FootprintReportedId) ..
			", but the script holds " .. tostring(FootprintId))
	end

	-- Entering is a one-off: the walker crosses the cell once. A restore that rebuilt the trigger
	-- without knowing who was already inside reports the same actor as entering a second time.
	if FootprintEntered > 1 then
		fail("footprint trigger fired " .. tostring(FootprintEntered) .. " times, expected once")
	end

	-- Every tick across the window the AfterDelay above fires in, so a short soak shows it; every
	-- fifth tick elsewhere, which keeps a long run readable.
	if TicksSeen % 5 == 0 or (TicksSeen >= 21 and TicksSeen <= 25) then
		print("LUA-TEST: ticks=" .. tostring(TickCounter()) .. " seen=" .. tostring(TicksSeen) ..
			" counter=" .. tostring(GlobalCounter) .. " nested=" .. tostring(Nested.inner.value) ..
			" watched=" .. tostring(#Watched) .. " blockerKilled=" .. tostring(BlockerKilled) ..
			" dead=" .. DeadActorSeen .. " delayed=" .. tostring(DelayedFired) ..
			" called=" .. tostring(CalledSeen) ..
			" fp=" .. tostring(FootprintEntered) .. "/" .. tostring(FootprintReportedId) ..
			"/" .. tostring(FootprintId) .. " reinf=" .. tostring(ReinforcedSeen) ..
			" group=" .. tostring(GroupFired))
	end
end
