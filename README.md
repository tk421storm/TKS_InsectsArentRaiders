# Insects Aren't Raiders

In vanilla RimWorld, insects can switch to the `AssaultColony` lord toil/duty, the same behaviour raiders use. When nothing
is in sight, it sends them toward the colony through `JobGiver_AITrashColonyClose`, `JobGiver_AITrashBuildingsDistant`,
`JobGiver_AIGotoNearestHostile` and `JobGiver_AISapper`, so a bug that was minding its hive ends up walking across the map to
chew on a random wall. The triggers for it are broad: a wildfire, a mortar shell or another faction's raid can send a whole
hive after the colony. This mod keeps insects behaving like animals defending a nest, and only lets them go after the colony
when they have seen a colonist attack them. When insects do attack, they move as a swarm (section 2) instead of as raiders.

Code references below are to the 1.6 game assembly.

## The rule

Harm to a hive or its insects counts as **caused by the player** only when all of these hold:

- the damage's instigator is a **pawn** of the player's faction (a colonist, a tamed animal, a mech), and
- the damage is **not fire burning** (`Fire.DoFireDamage`). Vanilla passes a fire's starter along as the instigator, even as
  the fire spreads; the mod ignores it entirely, and
- that pawn is **within 50 cells and in line of sight** of the harmed insect or hive, or of any insect in the same hive
  lord. 50 cells is the target acquire radius of vanilla's aggressive hive defense.

So a gunshot, melee attack or the blast of a thrown molotov counts if the insects can see the colonist. A wildfire or a
fire a colonist started, a mortar shell, turret fire, traps and IEDs, a remotely detonated blasting charge (their
instigator is the building), heat, cold, starvation, disease and other factions do not.

Only harm caused by the player sends a hive into a swarm attack. Everything else sends it to aggressive defense.

## Changes

### 1. Hive lords: swarm only on a seen player attacker

Applies to every `LordJob_DefendAndExpandHive`, including infestation hives that spawn aggressive. Insect lair maps
(Odyssey) are left alone; vanilla uses `LordToil_NestAssault` there.

`LordJob_DefendAndExpandHive.CreateGraph` (postfix). Vanilla graph:

| # | From | To | Triggers |
|---|---|---|---|
| 0 | calm defense (`LordToil_DefendAndExpandHive`, 10-cell leash) | aggressive defense (`LordToil_DefendHiveAggressively`, 40-cell leash); **assault** if the hive spawned aggressive | insect harmed by anything with a faction (50%), insect downed or killed (not by roof collapse), hive attacked, hive burned badly, any hive on the map destroyed, insect burned by hot air |
| 1 | calm defense | assault | insect harmed by the map's owner (50%) |
| 2 | aggressive defense | assault | insect harmed by the map's owner (50%) |
| 3, 4 | each defense toil | itself | hive despawned (reassigns hives) |
| 5 | assault, aggressive defense | calm defense | 1200 ticks with no harm or hive memos |

The mod:

- points transition 0 at aggressive defense for every hive, and adds a filter to each of its triggers so it fires only when
  the cause is **not** a seen player attacker;
- filters transitions 1 and 2 so they never fire;
- appends a swarm toil (`LordToil_InsectSwarm`, section 2) that ends when the swarm runs out of targets;
- appends a transition from calm and aggressive defense to the swarm that fires only on a seen player attacker. It has the
  harm-related triggers of transition 0 (insect harmed at 50%, insect downed or killed, hive attacked, hive burned badly, a
  hive destroyed) and the same "end all jobs" action as vanilla's. The hot-air memo isn't on it, since hot air never has an
  attacker. The swarm starts out heading for that attacker;
- appends a transition from the swarm back to calm defense, so the insects go home, when the swarm has no target left or
  after one in-game hour (2500 ticks) in which none of its insects was harmed and no hive memo came, ending attack-building
  jobs as vanilla's does. This is vanilla's rule for leaving the assault (`Trigger_TicksPassedWithoutHarmOrMemos`) with a
  longer time: vanilla's 1200 ticks ended swarms on their way to a far attacker. Any harm to the insects resets it, from
  anyone; the insects' own attacks don't. It restarts each time the swarm starts.

Transitions are checked in order and the first match wins, so a signal caused by a seen player attacker passes through 0
(filtered out) and 1 and 2 (never) to the new one; anything else is caught by 0 in calm defense and ignored in aggressive
defense. The vanilla assault toil is no longer reachable; a save loaded while a hive was assaulting returns to calm defense
through transition 5 after 1200 quiet ticks.

Nothing is removed from the graph: saved lords store their current toil and their trigger data by index (trigger data is
numbered across all transitions in order, toil data by toil), so removing a toil, transition or trigger breaks loading.
Filters don't change the numbering, and the new toil and transitions are appended after vanilla's.

Hive memos carry no damage info. Prefixes on `Hive.PostApplyDamage` (sends "attacked" and "burned badly") and `Hive.Kill`
(sends "destroyed" to every lord on the map) keep the damage while vanilla sends the memo, which is handled within the same
call, and the filter reads it. For "destroyed", each lord checks the attacker against its own insects, so a colonist
destroying one hive only provokes the hives whose insects saw it.

### 2. Insect swarms instead of colony assaults

`LordToil_InsectSwarm` follows the pattern of vanilla's shambler swarm (`LordToil_EntitySwarm`, Anomaly): a point moves
along a path and the insects follow it. The point (saved with the toil) starts at the insect closest to the middle of the
swarm and moves one path cell every 40 ticks (1.5 cells per second; shamblers 0.1). It waits while no insect is within 12
cells of it. Its path ignores walls and doors (`PassAllDestroyableThings`), but the point itself stops in front of a cell
it can't walk into or a closed door.

Every tick one insect, in turn, is checked: one that isn't moving and whose cell to go to is more than 7 cells from the
point, or one it can't reach (behind a wall or embrasure), gets a new reachable cell within 7 cells of the point (12 if there
is none). Unlike the shambler toil, insects stay in the lord when they pick a target or
can't reach the point, and unspawned or downed insects are skipped.

Duty `TKS_InsectSwarm` (`Defs/DutyDefs/Duties_InsectSwarm.xml`), in order:

1. `JobGiver_InsectSwarmFight`: vanilla's `JobGiver_AIFightEnemies`, leashed to the point: targets within 12 cells of the
   point, found within 20 cells of the insect (kept to 25), line of sight needed, targets out of sight are dropped. Only
   targets the insect can reach: one seen through an embrasure would otherwise hold it in place.
2. `JobGiver_InsectSwarmAttackBuilding`: with a 15% chance each time the insect looks for something to do, attacks a random
   building of an enemy within 3 cells (`TrashUtility.ShouldTrashBuilding` with `attackAllInert`, so walls and doors count).
   The job expires after vanilla's trash interval even without enemies around, so insects pick at the walls they are stuck
   behind instead of breaching them in a focused way.
3. `JobGiver_GetFood`, so a long-lived swarm doesn't starve.
4. `JobGiver_InsectSwarmFollow`: an insect more than 7 cells from the point jogs to its cell near the point (the job
   expires after 250 ticks so it looks for targets on the way). Vanilla's `JobGiver_GotoTravelDestination` can't be used
   here: it and `JobGiver_Wander` flip the same move/wait toggle, so together they only ever gave wait jobs.
5. Wander within 5 cells of it (`JobGiver_WanderNearDutyLocation`, jogging).

The point heads for a fixed cell: where the attacker stood when the swarm started, or where a seen enemy was. Once it is
there and an insect has caught up (within 7 cells), or once it hasn't moved for 5000 ticks, it picks the closest enemy pawn
any of the swarm's insects can see (50 cells, line of sight) and heads for that pawn's cell. With none:

- a **hive swarm** keeps looking for 600 ticks, then sends `TKS_InsectSwarmNoTarget`, and the hive lord returns to calm
  defense (section 1);
- a **raid-style swarm** heads for a random colonist on the map, or a random colony building, and looks again every 250
  ticks if there are neither. It goes on for as long as it has insects.

**Raid-style swarms** replace vanilla's colony assaults by insects:

- `LordMaker.MakeNewLord` (prefix): a new `LordJob_AssaultColony` for the insect faction becomes `LordJob_InsectSwarm`
  (a single raid-style swarm toil, no flee). That covers insect ambushes in ancient complexes (`InsectAmbush`), the insects
  a tunnel hive spawns with `insectsPoints`, quest ambushes by insects and anything else that sends insects as a raid.
  Not on insect lair maps, and not while `ResurrectionUtility.TryResurrect` runs (resurrected insects are unchanged for
  now, see the proposal below). Lords already in a save stay as they are.
- `LordJob_SleepThenAssaultColony.CreateGraph` (postfix): sleeping insects in ancient structures wake into an attached
  assault subgraph. For the insect faction, a raid-style swarm toil is appended and the wake-up transition points at it;
  the assault toils stay in the graph, unreachable.

`GenHostility.IsActiveThreatTo` (postfix): vanilla ignores hive insects unless their duty is `AssaultColony`; insects with
the swarm duty count as an active threat too, as assaulting ones do in vanilla.

### 3. Aggressive defense patrols wider and faster

`Patches/DutyDefs.xml` sets the `JobGiver_WanderHive` node of the `DefendHiveAggressively` duty to `wanderRadius` 35
(vanilla 7.5; the leash is 40) and `locomotionUrgency` `Jog`. Wandering defaults to `Walk`, which halves movement speed in
`Pawn_PathFollower`; `Jog` is full speed. Attack jobs already jog, so insects now patrol at the speed they charge. Calm
hives still walk within 7.5 cells. Insects running patrols across the area is also how the player can tell a hive is
defending itself aggressively.

### 4. Insects that lose their hive keep defending its spot, then go wild after 3 to 4 days

Both hive toils (`UpdateAllDuties`) give a pawn with no hive the `AssaultColony` duty. A postfix on each replaces it with
`DefendHiveAggressively` focused on the cell where the lord's hive last stood, with a 40-cell radius. Without a hive, vanilla's
`JobGiver_HiveDefense.GetFlagPosition` and `JobGiver_WanderHive.GetWanderRoot` fall back to the pawn's own position, so the
leash would travel with the insect; postfixes on both use the duty's cell when the duty focuses on a cell instead of a hive.
A lord already assaulting keeps its `AssaultColony` duties until it calms down, then gets these.

`MapComponent_HiveLords` checks hive lords once an in-game hour. A lord has a hive while any hive in its hive toils' data
(each insect's assigned hive) is still spawned. When none is, it starts a timer of 3 to 4 days (random). If a hive turns up
again the timer is cancelled. When it runs out, every insect in the lord becomes wild (`SetFaction(null)`, which also removes
it from the lord, and the empty lord is destroyed) and the message "Now that their hive is destroyed, the insects are no
longer controlled and will wander, hunt and leave the map as normal animals." is shown. Wild insects use the wild branch of
the vanilla `Insect` think tree: they fight enemies within 30 cells, hunt and eat, leave in the wrong season or when
starving, and leave at random (60-day MTB).

The last hive position and release time are saved with the map.

A hive lord that never had a hive isn't released this way. Vanilla Factions Expanded - Insectoids 2 makes one per insect
for the insects its swarmling cocoons hatch (`VFEInsectoids.Cocoon`) and the swarmlings its queens spawn
(`CompSpawnSwarmlings`), and only finds them a hive within 30 cells (`LordToil_HiveRelated.FindClosestHive`), so insects
hatched farther out wandered and starved. The hourly check moves these insects into the lord of the closest hive on the map
and assigns them to that hive in its toil data, so they get its calm defense duty. With no hive on the map they go wild,
without the message.

### 5. Cocoon insects spawn wild instead of with a raid lord

`CompPawnSpawnOnWakeup.Spawn` (prefix + postfix). The Core cocoons (megaspider, megascarab, spelopede) and the Odyssey locust
cocoon spawn their insect into a `LordJob_AssaultColony` and give it the `CocoonDisturbed` mental state. That state only makes
the insect hostile (to everything except insects and wild animals); all of its attacking comes from the lord. The postfix
makes each newly spawned insect wild, which drops it from the lord, then starts `CocoonDisturbed` again (changing faction
clears mental states) with `forceRecoverAfterTicks` set to 3 to 6 hours. While enraged, the wild-insect think-tree node
`JobGiver_AIFightEnemies` (30-cell acquire radius) attacks what it can see. Afterwards it is an ordinary wild insect.

This applies to any `CompPawnSpawnOnWakeup` whose `lordJob` is `LordJob_AssaultColony` and which has a `mentalState`, and only
to insect pawns.

## Left unchanged

- **Insect lair maps** (Odyssey).
- **Resurrected insects**, see the proposal below.

## Side effects

- `GenHostility.IsActiveThreatTo` (by default) ignores pawns of a hive lord unless their duty is `AssaultColony`. It backs
  `AnyHostileActiveThreatTo`, used for forming and reforming caravans, "defeat all enemies" site/quest checks and similar.
  Hive insects only count as an active threat there while swarming (section 2), which now needs a seen player attacker. Released
  (wild) and cocoon insects are not in a hive lord, so this doesn't apply to them.
- Vanilla assigns each insect a hive once (`LordToil_HiveRelated.GetHiveFor`) and keeps it until that hive despawns. It
  searches only for an insect without one: the nearest hive of the insect's faction within 30 cells and 30 regions that it
  can walk to (`FindClosestHive`). A lord counts as hiveless only when none of its insects has a spawned hive. So it can be
  released while a hive still exists only if every remaining insect lost its own hive and no surviving hive passed that
  search (too far, more than 30 regions away, or walled off). Those insects defend the old spot and then go wild; the
  surviving hive keeps spawning insects of its own.

## Proposed, not implemented: resurrected insects

`ResurrectionUtility.TryResurrect`: a resurrected pawn is no longer in a lord (death removed it). If it belongs to a hostile
faction and no `noLord` parameter is passed, vanilla puts it in a new `LordJob_AssaultColony` on its own. For an insect from
`Faction.OfInsects`, killed defending its hive and brought back (for example by a hostile mech resurrector or an Anomaly
effect), that makes it a lone raider.

Proposal: a postfix on `TryResurrect` that, for a resurrected insect of `Faction.OfInsects` whose new lord is a
`LordJob_AssaultColony` containing only that insect:

1. removes it from that lord, then
2. adds it to the nearest hive lord (`LordJob_DefendAndExpandHive`, not on an insect lair map) whose hive is within about 30
   cells, so it goes back to defending its hive under the rules above, or
3. if there is no such lord, makes it wild (`SetFaction(null)`).
