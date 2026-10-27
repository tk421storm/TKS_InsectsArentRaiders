using HarmonyLib;
using RimWorld;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace TKS_InsectsArentRaiders
{
    [StaticConstructorOnStartup]
    public static class InsertHarmony
    {
        static InsertHarmony()
        {
            Harmony harmony = new Harmony("TKS_InsectsArentRaiders");
            harmony.PatchAll();
            Log.Message("TKS_InsectsArentRaiders: Patching finished");
        }
    }

    [DefOf]
    public static class InsectsArentRaidersDefOf
    {
        public static DutyDef TKS_InsectSwarm;

        static InsectsArentRaidersDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(InsectsArentRaidersDefOf));
        }
    }

    public static class HiveLordUtility
    {
        // same leash as vanilla's DefendHiveAggressively toil
        public const float HivelessDefendRadius = 40f;

        public static readonly IntRange ReleaseDelayTicks = new IntRange(3 * GenDate.TicksPerDay, 4 * GenDate.TicksPerDay);

        public static readonly IntRange CocoonRageTicks = new IntRange(3 * GenDate.TicksPerHour, 6 * GenDate.TicksPerHour);

        // Every hive lord, whether its hive spawned aggressive (infestations) or not, except on insect lair maps
        // (vanilla gives those their own nest assault toil)
        public static bool IsManagedHiveLord(Lord lord)
        {
            return lord?.LordJob is LordJob_DefendAndExpandHive && !IsInsectLair(lord.Map);
        }

        public static bool IsInsectLair(Map map)
        {
            return map != null && MapGeneratorDefOf.InsectLair != null && map.generatorDef == MapGeneratorDefOf.InsectLair;
        }

        // The hive toils keep each insect's assigned hive in their toil data, which stays put while the lord is assaulting
        public static Hive AnySpawnedHive(Lord lord)
        {
            return SpawnedHives(lord).FirstOrDefault();
        }

        public static IEnumerable<Hive> SpawnedHives(Lord lord)
        {
            foreach (LordToil toil in lord.Graph.lordToils)
            {
                if (toil.data is LordToil_HiveRelatedData data)
                {
                    foreach (Hive hive in data.assignedHives.Values)
                    {
                        if (hive != null && hive.Spawned)
                        {
                            yield return hive;
                        }
                    }
                }
            }
        }

        // Duties this mod gives to hiveless insects focus on a cell; vanilla hive duties always focus on the hive itself
        public static bool TryGetHivelessAnchor(Pawn pawn, out IntVec3 cell)
        {
            LocalTargetInfo focus = pawn.mindState?.duty?.focus ?? LocalTargetInfo.Invalid;
            if (focus.IsValid && !focus.HasThing)
            {
                cell = focus.Cell;
                return true;
            }
            cell = IntVec3.Invalid;
            return false;
        }
    }

    // Decides whether harm to a hive or its insects can be blamed on the player: the damage has to come from a player pawn
    // the insects could see. Fire never counts (even when it knows who started it), and neither does damage from buildings
    // (turrets, mortars, traps) or anything without an instigator (heat, starvation, disease).
    public static class HarmCause
    {
        // same as the target acquire radius of vanilla's DefendHiveAggressively duty
        public const float SightRange = 50f;

        // set while Fire.DoFireDamage runs
        public static bool inFireDamage;

        // Memos carry no damage info, so the Hive patches keep the damage behind the memo the hive is sending
        public static DamageInfo? hiveDamage;

        public static IntVec3 hiveDamageCell = IntVec3.Invalid;

        // the last attacker IsSeenPlayerAttacker accepted, so a swarm it provokes can head for it
        public static Pawn lastSeenAttacker;

        public static int lastSeenAttackerTick = -1;

        public static bool IsSeenPlayerHarm(Lord lord, TriggerSignal signal)
        {
            if (signal.type == TriggerSignalType.Memo)
            {
                return hiveDamage.HasValue && IsSeenPlayerAttacker(lord, hiveDamage.Value, hiveDamageCell);
            }
            if (signal.type == TriggerSignalType.PawnDamaged || signal.type == TriggerSignalType.PawnLost)
            {
                return IsSeenPlayerAttacker(lord, signal.dinfo, signal.Pawn?.PositionHeld ?? IntVec3.Invalid);
            }
            return false;
        }

        // seen from the harmed thing, or from any of the lord's insects
        public static bool IsSeenPlayerAttacker(Lord lord, DamageInfo dinfo, IntVec3 victimCell)
        {
            if (inFireDamage || !(dinfo.Instigator is Pawn attacker) || attacker.Faction != Faction.OfPlayer || !attacker.Spawned || attacker.Map != lord.Map)
            {
                return false;
            }
            if (CanSee(victimCell, attacker) || AnyInsectCanSee(lord, attacker))
            {
                lastSeenAttacker = attacker;
                lastSeenAttackerTick = Find.TickManager.TicksGame;
                return true;
            }
            return false;
        }

        public static bool AnyInsectCanSee(Lord lord, Thing target)
        {
            foreach (Pawn insect in lord.ownedPawns)
            {
                if (insect.Spawned && CanSee(insect.Position, target))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool CanSee(IntVec3 from, Thing target)
        {
            return from.IsValid && from.InHorDistOf(target.Position, SightRange) && GenSight.LineOfSight(target.Position, from, target.Map);
        }
    }

    public class TriggerFilter_SeenPlayerHarm : TriggerFilter
    {
        public bool invert;

        public override bool AllowActivation(Lord lord, TriggerSignal signal)
        {
            return HarmCause.IsSeenPlayerHarm(lord, signal) != invert;
        }
    }

    public class TriggerFilter_Never : TriggerFilter
    {
        public override bool AllowActivation(Lord lord, TriggerSignal signal)
        {
            return false;
        }
    }

    // Vanilla hive lord: calm defense -> aggressive defense (or straight to AssaultColony for hives that spawned aggressive)
    // on harm from any faction, an insect lost, or hive memos; calm or aggressive defense -> AssaultColony on harm from the
    // map's owner. Here: anything not caused by a player pawn the insects can see leads to aggressive defense, and only a
    // seen player attacker leads to a swarm (LordToil_InsectSwarm), for every hive. The swarm returns to calm defense, and
    // so to the hive, when it runs out of targets or after 1200 ticks without harm, like vanilla's assault.
    // Saved lords store their current toil and their trigger data by index (numbered across all transitions), so nothing
    // is removed: vanilla's triggers get filters, the swarm toil and its transitions are appended at the end, and the
    // transitions to AssaultColony are filtered to never fire. The assault toil stays in the graph, unreachable.
    [HarmonyPatch(typeof(LordJob_DefendAndExpandHive), nameof(LordJob_DefendAndExpandHive.CreateGraph))]
    public static class LordJob_DefendAndExpandHive_CreateGraph_Patch
    {
        public static void Postfix(LordJob_DefendAndExpandHive __instance, StateGraph __result)
        {
            if (!HiveLordUtility.IsManagedHiveLord(__instance.lord))
            {
                return;
            }
            LordToil calm = __result.lordToils.Find(t => t is LordToil_DefendAndExpandHive);
            LordToil defendAggressively = __result.lordToils.Find(t => t is LordToil_DefendHiveAggressively);
            LordToil assault = __result.lordToils.Find(t => t is LordToil_AssaultColony);
            if (calm == null || defendAggressively == null || assault == null)
            {
                return;
            }
            foreach (Transition transition in __result.transitions)
            {
                // the escalation out of calm defense on any harm, insect lost or hive memo
                if (transition.sources.Contains(calm) && transition.triggers.Any(t => t is Trigger_PawnLostViolently))
                {
                    transition.target = defendAggressively;
                    AddFilter(transition, new TriggerFilter_SeenPlayerHarm { invert = true });
                }
                // harmed by the map's owner -> AssaultColony, from calm and from aggressive defense
                else if (transition.target == assault)
                {
                    AddFilter(transition, new TriggerFilter_Never());
                }
            }
            LordToil_InsectSwarm swarm = new LordToil_InsectSwarm(endWhenNoTarget: true);
            __result.AddToil(swarm);
            Transition toSwarm = new Transition(calm, swarm);
            toSwarm.AddSource(defendAggressively);
            toSwarm.AddTrigger(new Trigger_PawnHarmed(0.5f));
            toSwarm.AddTrigger(new Trigger_PawnLostViolently(allowRoofCollapse: false));
            toSwarm.AddTrigger(new Trigger_Memo(Hive.MemoAttackedByEnemy));
            toSwarm.AddTrigger(new Trigger_Memo(Hive.MemoBurnedBadly));
            toSwarm.AddTrigger(new Trigger_Memo(Hive.MemoDestroyedNonRoofCollapse));
            AddFilter(toSwarm, new TriggerFilter_SeenPlayerHarm());
            toSwarm.AddPostAction(new TransitionAction_EndAllJobs());
            __result.AddTransition(toSwarm);
            Transition swarmDone = new Transition(swarm, calm);
            swarmDone.AddTrigger(new Trigger_Memo(LordToil_InsectSwarm.MemoNoTarget));
            // home after QuietTicks without any of the swarm's insects being harmed (from anyone; the insects' own attacks
            // don't count) or a hive memo. Restarts whenever the swarm starts. Vanilla's assault uses 1200, too short
            // for a swarm walking to a far cell.
            swarmDone.AddTrigger(new Trigger_TicksPassedWithoutHarmOrMemos(LordToil_InsectSwarm.QuietTicks, Hive.MemoAttackedByEnemy, Hive.MemoBurnedBadly, Hive.MemoDestroyedNonRoofCollapse, Hive.MemoDeSpawned, HediffGiver_Heat.MemoPawnBurnedByAir));
            swarmDone.AddPostAction(new TransitionAction_EndAttackBuildingJobs());
            __result.AddTransition(swarmDone);
        }

        public static void AddFilter(Transition transition, TriggerFilter filter)
        {
            foreach (Trigger trigger in transition.triggers)
            {
                if (trigger.filters == null)
                {
                    trigger.filters = new List<TriggerFilter>();
                }
                trigger.filters.Add(filter);
            }
        }
    }

    public class LordToilData_InsectSwarm : LordToilData
    {
        public IntVec3 pos = IntVec3.Invalid;

        public IntVec3 dest = IntVec3.Invalid;

        public int lastMoved;

        // when the point last moved a cell
        public int lastAdvanced;

        // when the swarm started looking around at its destination cell; -1 while it's on its way
        public int arrivedTick = -1;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref pos, "pos", IntVec3.Invalid);
            Scribe_Values.Look(ref dest, "dest", IntVec3.Invalid);
            Scribe_Values.Look(ref lastMoved, "lastMoved", 0);
            Scribe_Values.Look(ref lastAdvanced, "lastAdvanced", 0);
            Scribe_Values.Look(ref arrivedTick, "arrivedTick", -1);
        }
    }

    // Swarm built on the pattern of vanilla's LordToil_EntitySwarm (shambler swarms): a point moves along a path and the
    // insects follow it, fighting what they see within SwarmRadius of it. Unlike the shambler version, insects stay in
    // the lord when they pick a target or can't reach the point, and unspawned or downed insects are skipped.
    // The point heads for a cell: where the attacker stood, or the closest enemy pawn the swarm can see. Once it gets
    // there (or makes no progress for StuckTicks) the swarm looks again from there: a seen enemy gives it a new cell; with
    // none, a raid-style swarm heads for a colonist or colony building, and a hive swarm searches for SearchTicks after
    // its insects catch up, then ends (MemoNoTarget). The point stops in front of walls and closed doors; the insects
    // attack buildings around them now and then (JobGiver_InsectSwarmAttackBuilding) until the way is open.
    public class LordToil_InsectSwarm : LordToil
    {
        public const string MemoNoTarget = "TKS_InsectSwarmNoTarget";

        // insects fight within this distance of the point
        public const float SwarmRadius = 12f;

        // insects farther than this from the point move back toward it (vanilla shambler swarms use 7)
        public const float FollowRadius = 7f;

        // 1.5 cells per second
        private const int MoveInterval = 40;

        // with nowhere to go, look again after this long
        private const int RetargetDelay = 250;

        // a hive swarm looks around its destination cell this long before going home
        private const int SearchTicks = 600;

        // a point that hasn't moved for this long counts as arrived
        private const int StuckTicks = 5000;

        // a hive swarm goes home after this long without any of its insects harmed (one in-game hour)
        public const int QuietTicks = GenDate.TicksPerHour;

        private bool endWhenNoTarget;

        private PawnPath path;

        private LordToilData_InsectSwarm Data => (LordToilData_InsectSwarm)data;

        public override bool ForceHighStoryDanger => true;

        public override bool AllowSatisfyLongNeeds => false;

        public LordToil_InsectSwarm(bool endWhenNoTarget)
        {
            this.endWhenNoTarget = endWhenNoTarget;
            data = new LordToilData_InsectSwarm();
        }

        public override void Init()
        {
            base.Init();
            ReleasePath();
            Data.pos = SwarmCenter();
            Pawn attacker = HarmCause.lastSeenAttacker;
            bool provoked = attacker != null && HarmCause.lastSeenAttackerTick == Find.TickManager.TicksGame && attacker.Spawned && attacker.Map == lord.Map;
            Data.dest = provoked ? attacker.Position : FindDestination();
            Data.lastMoved = Find.TickManager.TicksGame;
            Data.lastAdvanced = Find.TickManager.TicksGame;
            Data.arrivedTick = -1;
        }

        public override void UpdateAllDuties()
        {
            foreach (Pawn pawn in lord.ownedPawns)
            {
                IntVec3 point = Data.pos.IsValid ? Data.pos : pawn.Position;
                pawn.mindState.duty = new PawnDuty(InsectsArentRaidersDefOf.TKS_InsectSwarm, point, point, SwarmRadius);
            }
        }

        public override void LordToilTick()
        {
            if (lord.ownedPawns.Count == 0)
            {
                return;
            }
            FollowPoint(lord.ownedPawns[Find.TickManager.TicksGame % lord.ownedPawns.Count]);
            if (Find.TickManager.TicksGame >= Data.lastMoved + MoveInterval)
            {
                Data.lastMoved = Find.TickManager.TicksGame;
                MovePoint();
            }
        }

        public override void Notify_PawnAcquiredTarget(Pawn detector, Thing newTarg)
        {
            detector.TryGetComp<CompCanBeDormant>()?.WakeUp();
        }

        public override void Cleanup()
        {
            base.Cleanup();
            ReleasePath();
        }

        private void FollowPoint(Pawn pawn)
        {
            if (!pawn.Spawned || pawn.Downed || pawn.mindState?.duty?.def != InsectsArentRaidersDefOf.TKS_InsectSwarm || !Data.pos.IsValid)
            {
                return;
            }
            // the fight job giver leashes to focusSecond
            pawn.mindState.duty.focusSecond = Data.pos;
            if (pawn.pather.Moving)
            {
                return;
            }
            // a cell the insect can't get to (behind a wall or embrasure) would leave it standing still
            LocalTargetInfo focus = pawn.mindState.duty.focus;
            if (focus.IsValid && focus.Cell.InHorDistOf(Data.pos, FollowRadius) && pawn.CanReach(focus, PathEndMode.OnCell, Danger.Deadly))
            {
                return;
            }
            TraverseParms traverseParms = TraverseParms.For(pawn);
            if (CellFinder.TryFindRandomReachableCellNearPosition(pawn.Position, Data.pos, lord.Map, FollowRadius, traverseParms, c => c.Standable(lord.Map), null, out IntVec3 cell)
                || CellFinder.TryFindRandomReachableCellNearPosition(pawn.Position, Data.pos, lord.Map, SwarmRadius, traverseParms, c => c.Standable(lord.Map), null, out cell))
            {
                pawn.mindState.duty.focus = cell;
            }
        }

        private void MovePoint()
        {
            if (!Data.pos.IsValid)
            {
                Data.pos = SwarmCenter();
                if (!Data.pos.IsValid)
                {
                    return;
                }
            }
            int ticksGame = Find.TickManager.TicksGame;
            // after loading the path is gone; find it again to the saved destination
            if (path == null && Data.dest.IsValid && Data.arrivedTick < 0)
            {
                FindPath();
            }
            bool stuck = ticksGame >= Data.lastAdvanced + StuckTicks;
            if (path == null || path.NodesLeftCount <= 1 || stuck)
            {
                ReleasePath();
                // the search starts once an insect has caught up with the point
                if (Data.arrivedTick < 0)
                {
                    if (!stuck && !lord.ownedPawns.Any(p => p.Spawned && p.Position.InHorDistOf(Data.pos, FollowRadius)))
                    {
                        return;
                    }
                    Data.arrivedTick = ticksGame;
                }
                Data.dest = FindDestination();
                FindPath();
                if (path == null)
                {
                    if (!endWhenNoTarget)
                    {
                        Data.lastMoved = ticksGame + RetargetDelay;
                    }
                    else if (ticksGame >= Data.arrivedTick + SearchTicks)
                    {
                        lord.ReceiveMemo(MemoNoTarget);
                    }
                    return;
                }
                Data.arrivedTick = -1;
                Data.lastAdvanced = ticksGame;
                // already standing on the new destination
                if (path.NodesLeftCount <= 1)
                {
                    return;
                }
            }
            // the point waits for the swarm
            if (!lord.ownedPawns.Any(p => p.Spawned && p.Position.InHorDistOf(Data.pos, SwarmRadius)))
            {
                return;
            }
            IntVec3 next = path.Peek(1);
            Building_Door door = next.GetDoor(lord.Map);
            if (!next.Walkable(lord.Map) || (door != null && !door.Open))
            {
                return;
            }
            Data.pos = path.ConsumeNextNode();
            Data.lastAdvanced = ticksGame;
        }

        private void FindPath()
        {
            ReleasePath();
            if (!Data.dest.IsValid)
            {
                return;
            }
            // through walls and doors, so the point gets as far as the wall in its way
            path = lord.Map.pathFinder.FindPathNow(Data.pos, Data.dest, TraverseParms.For(TraverseMode.PassAllDestroyableThings));
            if (!path.Found)
            {
                ReleasePath();
            }
        }

        private void ReleasePath()
        {
            path?.ReleaseToPool();
            path = null;
        }

        private IntVec3 FindDestination()
        {
            Pawn enemy = ClosestSeenEnemy();
            if (enemy != null)
            {
                return enemy.Position;
            }
            if (endWhenNoTarget)
            {
                return IntVec3.Invalid;
            }
            // a raid-style swarm with nothing in sight goes for the colony
            Map map = lord.Map;
            if (map.mapPawns.FreeColonistsSpawned.TryRandomElement(out Pawn colonist))
            {
                return colonist.Position;
            }
            if (map.listerBuildings.allBuildingsColonist.TryRandomElement(out Building building))
            {
                return building.Position;
            }
            return IntVec3.Invalid;
        }

        private Pawn ClosestSeenEnemy()
        {
            Pawn closest = null;
            float closestDistSq = float.MaxValue;
            foreach (IAttackTarget target in lord.Map.attackTargetsCache.TargetsHostileToFaction(lord.faction))
            {
                if (!(target.Thing is Pawn enemy) || !enemy.Spawned || enemy.Downed)
                {
                    continue;
                }
                float distSq = (enemy.Position - Data.pos).LengthHorizontalSquared;
                if (distSq < closestDistSq && HarmCause.AnyInsectCanSee(lord, enemy))
                {
                    closest = enemy;
                    closestDistSq = distSq;
                }
            }
            return closest;
        }

        // the spawned insect closest to the middle of the swarm
        private IntVec3 SwarmCenter()
        {
            List<Pawn> spawned = lord.ownedPawns.Where(p => p.Spawned).ToList();
            if (!spawned.Any())
            {
                return IntVec3.Invalid;
            }
            IntVec3 middle = new IntVec3((int)spawned.Average(p => p.Position.x), 0, (int)spawned.Average(p => p.Position.z));
            return spawned.MinBy(p => (p.Position - middle).LengthHorizontalSquared).Position;
        }
    }

    // Lord for insects that vanilla sends as a raid (insect ambushes, insects spawned by tunnel hives, and anything else
    // that makes a LordJob_AssaultColony for the insect faction): a swarm that keeps finding targets for as long as it has
    // insects.
    public class LordJob_InsectSwarm : LordJob
    {
        public override bool AddFleeToil => false;

        public override StateGraph CreateGraph()
        {
            StateGraph stateGraph = new StateGraph();
            stateGraph.StartingToil = new LordToil_InsectSwarm(endWhenNoTarget: false);
            return stateGraph;
        }
    }

    // Goes to the duty's cell near the point when the insect has fallen behind it. Vanilla's JobGiver_GotoTravelDestination
    // (without wanderOnArrival) and JobGiver_Wander flip the same nextMoveOrderIsWait toggle, so together they only ever
    // gave the insect wait jobs.
    public class JobGiver_InsectSwarmFollow : ThinkNode_JobGiver
    {
        private const int ExpiryInterval = 250;

        protected override Job TryGiveJob(Pawn pawn)
        {
            PawnDuty duty = pawn.mindState.duty;
            if (!duty.focus.IsValid || !duty.focusSecond.IsValid || pawn.Position == duty.focus.Cell
                || pawn.Position.InHorDistOf(duty.focusSecond.Cell, LordToil_InsectSwarm.FollowRadius)
                || !pawn.CanReach(duty.focus, PathEndMode.OnCell, Danger.Deadly))
            {
                return null;
            }
            Job job = JobMaker.MakeJob(JobDefOf.Goto, duty.focus);
            job.locomotionUrgency = LocomotionUrgency.Jog;
            job.expiryInterval = ExpiryInterval;
            job.checkOverrideOnExpire = true;
            return job;
        }
    }

    // fights within the swarm's radius of its point
    public class JobGiver_InsectSwarmFight : JobGiver_AIFightEnemies
    {
        protected override IntVec3 GetFlagPosition(Pawn pawn)
        {
            LocalTargetInfo point = pawn.mindState.duty.focusSecond;
            return point.IsValid ? point.Cell : pawn.Position;
        }

        protected override float GetFlagRadius(Pawn pawn)
        {
            return pawn.mindState.duty.radius;
        }

        // Only enemies the insect can get to: one seen through an embrasure or a gap in a wall would hold the insect in
        // place instead of letting it follow the point.
        protected override bool ExtraTargetValidator(Pawn pawn, Thing target)
        {
            return base.ExtraTargetValidator(pawn, target) && pawn.CanReach(target, PathEndMode.Touch, Danger.Deadly);
        }
    }

    // Now and then attacks a building next to the insect: whatever walls or doors the swarm is stuck behind, but not in a
    // focused way like breachers. The job expires after vanilla's trash interval even without enemies nearby, so the insect
    // looks around again.
    public class JobGiver_InsectSwarmAttackBuilding : ThinkNode_JobGiver
    {
        private float chance = 0.15f;

        private float radius = 2.9f;

        private static List<Building> tmpBuildings = new List<Building>();

        public override ThinkNode DeepCopy(bool resolve = true)
        {
            JobGiver_InsectSwarmAttackBuilding obj = (JobGiver_InsectSwarmAttackBuilding)base.DeepCopy(resolve);
            obj.chance = chance;
            obj.radius = radius;
            return obj;
        }

        protected override Job TryGiveJob(Pawn pawn)
        {
            if (!Rand.Chance(chance))
            {
                return null;
            }
            Map map = pawn.Map;
            tmpBuildings.Clear();
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(pawn.Position, radius, useCenter: false))
            {
                if (!cell.InBounds(map))
                {
                    continue;
                }
                Building building = cell.GetEdifice(map);
                if (building != null && !tmpBuildings.Contains(building) && TrashUtility.ShouldTrashBuilding(pawn, building, attackAllInert: true))
                {
                    tmpBuildings.Add(building);
                }
            }
            if (!tmpBuildings.TryRandomElement(out Building target))
            {
                return null;
            }
            tmpBuildings.Clear();
            Job job = TrashUtility.TrashJob(pawn, target, allowPunchingInert: true);
            if (job != null)
            {
                job.expireRequiresEnemiesNearby = false;
            }
            return job;
        }
    }

    // Insect raids (LordJob_AssaultColony for the insect faction) become swarms, except on insect lair maps and for
    // resurrected insects (see README, not changed yet).
    [HarmonyPatch(typeof(LordMaker), nameof(LordMaker.MakeNewLord))]
    public static class LordMaker_MakeNewLord_Patch
    {
        public static void Prefix(Faction faction, ref LordJob lordJob, Map map)
        {
            if (ResurrectionUtility_TryResurrect_Patch.resurrecting || faction == null || faction != Faction.OfInsects)
            {
                return;
            }
            if (lordJob != null && lordJob.GetType() == typeof(LordJob_AssaultColony) && !HiveLordUtility.IsInsectLair(map))
            {
                lordJob = new LordJob_InsectSwarm();
            }
        }
    }

    [HarmonyPatch(typeof(ResurrectionUtility), nameof(ResurrectionUtility.TryResurrect))]
    public static class ResurrectionUtility_TryResurrect_Patch
    {
        public static bool resurrecting;

        public static void Prefix()
        {
            resurrecting = true;
        }

        public static void Finalizer()
        {
            resurrecting = false;
        }
    }

    // Sleeping insects (ancient structures) wake into an attached LordJob_AssaultColony subgraph. Send the wake-up
    // transition to a raid-style swarm toil appended to the graph instead; the assault toils stay, unreachable.
    [HarmonyPatch(typeof(LordJob_SleepThenAssaultColony), nameof(LordJob_SleepThenAssaultColony.CreateGraph))]
    public static class LordJob_SleepThenAssaultColony_CreateGraph_Patch
    {
        private static readonly AccessTools.FieldRef<LordJob_SleepThenAssaultColony, Faction> factionRef =
            AccessTools.FieldRefAccess<LordJob_SleepThenAssaultColony, Faction>("faction");

        public static void Postfix(LordJob_SleepThenAssaultColony __instance, StateGraph __result)
        {
            Faction faction = factionRef(__instance);
            if (faction == null || faction != Faction.OfInsects || HiveLordUtility.IsInsectLair(__instance.lord?.Map))
            {
                return;
            }
            LordToil assault = __result.lordToils.Find(t => t is LordToil_AssaultColony);
            if (assault == null)
            {
                return;
            }
            LordToil_InsectSwarm swarm = new LordToil_InsectSwarm(endWhenNoTarget: false);
            __result.AddToil(swarm);
            foreach (Transition transition in __result.transitions)
            {
                if (transition.target == assault)
                {
                    transition.target = swarm;
                }
            }
        }
    }

    // Vanilla doesn't count hive insects as an active threat unless their duty is AssaultColony. Count a swarming hive
    // the same way.
    [HarmonyPatch(typeof(GenHostility), nameof(GenHostility.IsActiveThreatTo), new[] { typeof(IAttackTarget), typeof(Faction), typeof(bool), typeof(bool) })]
    public static class GenHostility_IsActiveThreatTo_Patch
    {
        public static void Postfix(IAttackTarget target, Faction faction, bool ignoreHives, bool canBeFogged, ref bool __result)
        {
            if (__result || !ignoreHives || !(target.Thing is Pawn pawn) || pawn.mindState?.duty?.def != InsectsArentRaidersDefOf.TKS_InsectSwarm)
            {
                return;
            }
            __result = pawn.HostileTo(faction) && (canBeFogged || !pawn.Fogged()) && GenHostility.IsPotentialThreat(target);
        }
    }

    [HarmonyPatch(typeof(Fire), "DoFireDamage")]
    public static class Fire_DoFireDamage_Patch
    {
        public static void Prefix()
        {
            HarmCause.inFireDamage = true;
        }

        public static void Finalizer()
        {
            HarmCause.inFireDamage = false;
        }
    }

    // sends MemoAttackedByEnemy and MemoBurnedBadly
    [HarmonyPatch(typeof(Hive), nameof(Hive.PostApplyDamage))]
    public static class Hive_PostApplyDamage_Patch
    {
        public static void Prefix(Hive __instance, DamageInfo dinfo)
        {
            HarmCause.hiveDamage = dinfo;
            HarmCause.hiveDamageCell = __instance.Position;
        }

        public static void Finalizer()
        {
            HarmCause.hiveDamage = null;
            HarmCause.hiveDamageCell = IntVec3.Invalid;
        }
    }

    // sends MemoDestroyedNonRoofCollapse to every lord on the map
    [HarmonyPatch(typeof(Hive), nameof(Hive.Kill))]
    public static class Hive_Kill_Patch
    {
        public static void Prefix(Hive __instance, DamageInfo? dinfo)
        {
            HarmCause.hiveDamage = dinfo;
            HarmCause.hiveDamageCell = __instance.Position;
        }

        public static void Finalizer()
        {
            HarmCause.hiveDamage = null;
            HarmCause.hiveDamageCell = IntVec3.Invalid;
        }
    }

    // Vanilla gives hive insects without a hive the AssaultColony duty. Keep them defending the spot where their hive was
    // instead, until MapComponent_HiveLords releases them.
    [HarmonyPatch]
    public static class HiveToils_UpdateAllDuties_Patch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(LordToil_DefendAndExpandHive), nameof(LordToil.UpdateAllDuties));
            yield return AccessTools.Method(typeof(LordToil_DefendHiveAggressively), nameof(LordToil.UpdateAllDuties));
        }

        public static void Postfix(LordToil __instance)
        {
            Lord lord = __instance.lord;
            if (!HiveLordUtility.IsManagedHiveLord(lord))
            {
                return;
            }
            MapComponent_HiveLords hiveLords = lord.Map.GetComponent<MapComponent_HiveLords>();
            hiveLords.UpdateHiveCell(lord);
            IntVec3 hiveCell = hiveLords.HiveCellFor(lord);
            foreach (Pawn pawn in lord.ownedPawns)
            {
                if (pawn.mindState?.duty?.def == DutyDefOf.AssaultColony)
                {
                    IntVec3 anchor = hiveCell.IsValid ? hiveCell : pawn.Position;
                    pawn.mindState.duty = new PawnDuty(DutyDefOf.DefendHiveAggressively, anchor, HiveLordUtility.HivelessDefendRadius);
                }            }
        }
    }

    // Without a hive, vanilla's hive job givers fall back to the pawn's own position, so the leash would move with the
    // insect. Use the duty's cell instead.
    [HarmonyPatch(typeof(JobGiver_HiveDefense), "GetFlagPosition")]
    public static class JobGiver_HiveDefense_GetFlagPosition_Patch
    {
        public static void Postfix(Pawn pawn, ref IntVec3 __result)
        {
            if (HiveLordUtility.TryGetHivelessAnchor(pawn, out IntVec3 cell))
            {
                __result = cell;
            }
        }
    }

    [HarmonyPatch(typeof(JobGiver_WanderHive), "GetWanderRoot")]
    public static class JobGiver_WanderHive_GetWanderRoot_Patch
    {
        public static void Postfix(Pawn pawn, ref IntVec3 __result)
        {
            if (HiveLordUtility.TryGetHivelessAnchor(pawn, out IntVec3 cell))
            {
                __result = cell;
            }
        }
    }

    // Cocoon insects spawn wild instead of joining an AssaultColony lord. They keep the CocoonDisturbed mental state for a
    // few hours, which makes them hostile, so the wild-insect "fight nearby enemies" think node attacks what they can see.
    [HarmonyPatch(typeof(CompPawnSpawnOnWakeup), "Spawn")]
    public static class CompPawnSpawnOnWakeup_Spawn_Patch
    {
        public static void Prefix(CompPawnSpawnOnWakeup __instance, out int __state)
        {
            __state = __instance.spawnedPawns.Count;
        }

        public static void Postfix(CompPawnSpawnOnWakeup __instance, int __state)
        {
            CompProperties_PawnSpawnOnWakeup props = (CompProperties_PawnSpawnOnWakeup)__instance.props;
            if (props.lordJob != typeof(LordJob_AssaultColony) || props.mentalState == null)
            {
                return;
            }
            for (int i = __state; i < __instance.spawnedPawns.Count; i++)
            {
                Pawn pawn = __instance.spawnedPawns[i];
                if (pawn == null || pawn.Dead || !pawn.RaceProps.Insect || pawn.Faction == null || pawn.Faction.IsPlayer)
                {
                    continue;
                }
                // leaving the faction also removes the pawn from its lord and clears its mental state
                pawn.SetFaction(null);
                if (pawn.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.CocoonDisturbed, forced: true))
                {
                    pawn.MentalState.forceRecoverAfterTicks = HiveLordUtility.CocoonRageTicks.RandomInRange;
                }
            }
        }
    }

    // Tracks hive lords that lost their hive: where the hive was, and when the remaining insects are released.
    public class MapComponent_HiveLords : MapComponent
    {
        private List<HiveLordRecord> records = new List<HiveLordRecord>();

        public MapComponent_HiveLords(Map map) : base(map)
        {
        }

        public void UpdateHiveCell(Lord lord)
        {
            Hive hive = HiveLordUtility.AnySpawnedHive(lord);
            if (hive != null)
            {
                RecordFor(lord).hiveCell = hive.Position;
            }
        }

        public IntVec3 HiveCellFor(Lord lord)
        {
            HiveLordRecord record = records.Find(r => r.lord == lord);
            return record != null ? record.hiveCell : IntVec3.Invalid;
        }

        public override void MapComponentTick()
        {
            if (Find.TickManager.TicksGame % GenDate.TicksPerHour != 0)
            {
                return;
            }
            records.RemoveAll(r => r.lord == null || !map.lordManager.lords.Contains(r.lord));
            int ticksGame = Find.TickManager.TicksGame;
            // releasing the last insect destroys the lord, so iterate over a copy
            foreach (Lord lord in map.lordManager.lords.ToList())
            {
                if (!HiveLordUtility.IsManagedHiveLord(lord))
                {
                    continue;
                }
                HiveLordRecord record = RecordFor(lord);
                Hive hive = HiveLordUtility.AnySpawnedHive(lord);
                if (hive != null)
                {
                    record.hiveCell = hive.Position;
                    record.releaseTick = -1;
                }
                else if (!record.hiveCell.IsValid)
                {
                    JoinClosestHiveLord(lord);
                }
                else if (record.releaseTick < 0)
                {
                    record.releaseTick = ticksGame + HiveLordUtility.ReleaseDelayTicks.RandomInRange;
                }
                else if (ticksGame >= record.releaseTick)
                {
                    Release(lord);
                }
            }
        }

        private void Release(Lord lord)
        {
            List<Pawn> pawns = lord.ownedPawns.ToList();
            if (!pawns.Any())
            {
                return;
            }
            foreach (Pawn pawn in pawns)
            {
                if (pawn.Faction != null)
                {
                    pawn.SetFaction(null);
                }
            }
            Messages.Message("TKSInsectsArentRaidersReleased".Translate(), new LookTargets(pawns.Where(p => p.Spawned)), MessageTypeDefOf.NeutralEvent);
        }

        // A hive lord that never had a hive: VFE Insectoids 2 gives every insect hatched from a swarmling cocoon, and every
        // swarmling its queens spawn, a hive lord of its own, and only finds them a hive within 30 cells. Move them into
        // the lord of the closest hive on the map, assigned to that hive; with no hive on the map they go wild.
        private void JoinClosestHiveLord(Lord lord)
        {
            List<Pawn> pawns = lord.ownedPawns.ToList();
            Pawn first = pawns.Find(p => p.Spawned);
            if (first == null)
            {
                return;
            }
            Lord bestLord = null;
            Hive bestHive = null;
            float bestDist = float.MaxValue;
            foreach (Lord other in map.lordManager.lords)
            {
                if (other == lord || other.faction != lord.faction || !HiveLordUtility.IsManagedHiveLord(other))
                {
                    continue;
                }
                foreach (Hive hive in HiveLordUtility.SpawnedHives(other))
                {
                    float dist = hive.Position.DistanceToSquared(first.Position);
                    if (dist < bestDist)
                    {
                        bestLord = other;
                        bestHive = hive;
                        bestDist = dist;
                    }
                }
            }
            foreach (Pawn pawn in pawns)
            {
                lord.RemovePawn(pawn);
                if (bestLord == null)
                {
                    pawn.SetFaction(null);
                    continue;
                }
                foreach (LordToil toil in bestLord.Graph.lordToils)
                {
                    if (toil.data is LordToil_HiveRelatedData data)
                    {
                        data.assignedHives[pawn] = bestHive;
                    }
                }
                bestLord.AddPawn(pawn);
            }
        }

        private HiveLordRecord RecordFor(Lord lord)
        {
            HiveLordRecord record = records.Find(r => r.lord == lord);
            if (record == null)
            {
                record = new HiveLordRecord { lord = lord };
                records.Add(record);
            }
            return record;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                records.RemoveAll(r => r.lord == null || !map.lordManager.lords.Contains(r.lord));
            }
            Scribe_Collections.Look(ref records, "records", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (records == null)
                {
                    records = new List<HiveLordRecord>();
                }
                records.RemoveAll(r => r.lord == null);
            }
        }
    }

    public class HiveLordRecord : IExposable
    {
        public Lord lord;

        public IntVec3 hiveCell = IntVec3.Invalid;

        public int releaseTick = -1;

        public void ExposeData()
        {
            Scribe_References.Look(ref lord, "lord");
            Scribe_Values.Look(ref hiveCell, "hiveCell", IntVec3.Invalid);
            Scribe_Values.Look(ref releaseTick, "releaseTick", -1);
        }
    }
}
