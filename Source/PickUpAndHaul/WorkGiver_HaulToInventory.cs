using PickUpAndHaul.Deliveries;
using System.Linq;

namespace PickUpAndHaul;
public partial class WorkGiver_HaulToInventory : WorkGiver_HaulGeneral
{
	//Thanks to AlexTD for the more dynamic search range
	//And queueing
	//And optimizing
	private const float SEARCH_FOR_OTHERS_RANGE_FRACTION = 0.5f;
	private const float MAX_SEARCH_RANGE = 12f;

	public override bool ShouldSkip(Pawn pawn, bool forced = false)
		=> base.ShouldSkip(pawn, forced)
		|| pawn.Faction != Faction.OfPlayerSilentFail
		|| !Settings.IsAllowedRace(pawn.RaceProps)
		|| pawn.GetComp<CompHauledToInventory>() == null
		|| pawn.IsQuestLodger()
		|| OverAllowedGearCapacity(pawn);

	public static bool GoodThingToHaul(Thing t, Pawn pawn)
		=> OkThingToHaul(t, pawn)
		&& t.IsNotCorpseOrAllowed()
		&& !t.IsInValidBestStorage();

	public static bool OkThingToHaul(Thing t, Pawn pawn)
		=> t.Spawned
		&& !t.IsForbidden(pawn);

	public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
	{
		return DeliveryPlanner.SortedHaulables(pawn);
	}

	public override bool HasJobOnThing(Pawn pawn, Thing thing, bool forced = false)
		=> OkThingToHaul(thing, pawn)
		&& thing.IsNotCorpseOrAllowed()
		&& HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, forced)
		&& StoreUtility.TryFindBestBetterStorageFor(thing, pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(thing), pawn.Faction, out _, out _, false);

	//bulky gear (power armor + minigun) so don't bother.
	public static bool OverAllowedGearCapacity(Pawn pawn) => MassUtility.GearMass(pawn) / MassUtility.Capacity(pawn) >= Settings.MaximumOccupiedCapacityToConsiderHauling;

	//pick up stuff until you can't anymore,
	//while you're up and about, pick up something and haul it
	//before you go out, empty your pockets
	public override Job JobOnThing(Pawn pawn, Thing thing, bool forced = false)
	{
		if (!OkThingToHaul(thing, pawn) || !HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, forced))
		{
			return null;
		}

		if (OverAllowedGearCapacity(pawn)
			|| pawn.GetComp<CompHauledToInventory>() is null // Misc. Robots compatibility
															 // See https://github.com/catgirlfighter/RimWorld_CommonSense/blob/master/Source/CommonSense11/CommonSense/OpportunisticTasks.cs#L129-L140
			|| !thing.IsNotCorpseOrAllowed() //This WorkGiver gets hijacked by AllowTool and expects us to urgently haul corpses.
			|| MassUtility.WillBeOverEncumberedAfterPickingUp(pawn, thing, 1)) //https://github.com/Mehni/PickUpAndHaul/pull/18
		{
			return HaulAIUtility.HaulToStorageJob(pawn, thing, forced);
		}

		var map = pawn.Map;
		var designationManager = map.designationManager;
		var currentPriority = StoreUtility.CurrentStoragePriorityOf(thing);
		if (!StoreUtility.TryFindBestBetterStorageFor(thing, pawn, map, currentPriority, pawn.Faction, out var targetCell, out var haulDestination, true))
		{
			JobFailReason.Is("NoEmptyPlaceLower".Translate());
			return null;
		}

		int capacityStoreCell;
		ThingOwner nonSlotGroupThingOwner;
		if (haulDestination is ISlotGroupParent)
		{
			//since we've gone through all the effort of getting the loc, might as well use it.
			//Don't multi-haul food to hoppers.
			if (HaulToHopperJob(thing, targetCell, map))
			{
				return HaulAIUtility.HaulToStorageJob(pawn, thing, forced);
			}
			capacityStoreCell = CapacityAt(thing, targetCell, map);
		}
		else if (haulDestination is Thing destinationAsThing && (nonSlotGroupThingOwner = destinationAsThing.TryGetInnerInteractableThingOwner()) != null)
		{
			capacityStoreCell = nonSlotGroupThingOwner.GetCountCanAccept(thing);
		}
		else
		{
			Verse.Log.Error($"[PickUpAndHaul] does not know how to handle HaulToStorageJob got {haulDestination}, {thing}");
			return null;
		}

		if (capacityStoreCell == 0)
		{
			Log.Message($"{pawn} can not find a destination for {thing}");
			return HaulAIUtility.HaulToStorageJob(pawn, thing, forced);
		}

		var planner = new DeliveryPlanner(pawn.Map);
		if (!planner.TryWrapDestination(targetCell, haulDestination, out var storeTarget))
		{
			Verse.Log.Error($"[PickUpAndHaul] does not know how to handle HaulToStorageJob got {haulDestination}, {thing}");
			return null;
		}
		if (storeTarget.Container != null)
		{
			planner.SkipThings.Add(storeTarget.Container);
		}
		else
		{
			planner.SkipCells.Add(storeTarget.Cell);
		}

		Log.Message($"-------------------------------------------------------------------");
		Log.Message($"------------------------------------------------------------------");//different size so the log doesn't count it 2x
		Log.Message($"{pawn} job found to haul: {thing} to {storeTarget}:{capacityStoreCell}, looking for more now");

		//Find what fits in inventory, set nextThingLeftOverCount to be
		var nextThingLeftOverCount = 0;
		var encumberance = MassUtility.EncumbrancePercent(pawn);

		var ceOverweight = false;

		if (ModCompatibilityCheck.CombatExtendedIsActive)
		{
			ceOverweight = CompatHelper.CeOverweight(pawn);
		}

		var distanceToHaul = (storeTarget.Cell - thing.Position).LengthHorizontal * SEARCH_FOR_OTHERS_RANGE_FRACTION;
		var distanceToSearchMore = Math.Max(MAX_SEARCH_RANGE, distanceToHaul);

		//Find extra things than can be hauled to inventory, queue to reserve them
		var haulUrgentlyDesignation = DesignationDefs.HaulUrgentlyDesignation;
		var isUrgent = ModCompatibilityCheck.AllowToolIsActive && designationManager.DesignationOn(thing)?.def == haulUrgentlyDesignation;

		// list should be considered cachable, so copy it into a local list preserving sorting but filter out the item
		var haulablesRelativeToPawn = DeliveryPlanner.SortedHaulables(pawn);
		var haulables = new List<Thing>(haulablesRelativeToPawn);
		haulables.Remove(thing);

		var nextThing = thing;
		var lastThing = thing;

		bool Validator(Thing t)
			=> (!isUrgent || designationManager.DesignationOn(t)?.def == haulUrgentlyDesignation)
			&& GoodThingToHaul(t, pawn) && HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, t, false); //forced is false, may differ from first thing

		var traverseParms = TraverseParms.For(pawn);

		do
		{
			if (AllocateThingAtCell2(planner, pawn, nextThing))
			{
				lastThing = nextThing;
				encumberance += AddedEncumberance(pawn, nextThing);

				if (encumberance > 1 || ceOverweight)
				{
					//can't CountToPickUpUntilOverEncumbered here, pawn doesn't actually hold these things yet
					nextThingLeftOverCount = CountPastCapacity(pawn, nextThing, encumberance);
					Log.Message($"{pawn} Inventory allocated, will carry {nextThing}:{nextThingLeftOverCount}");
					planner.countQueue.Pop();
					planner.countQueue.Add(nextThingLeftOverCount);

					// We are now out of inventory space - and should bail right away.
					break;
				}
			}
		}
		while ((nextThing = GetClosestAndRemove(lastThing.Position, map, haulables, PathEndMode.ClosestTouch,
			       traverseParms, distanceToSearchMore, Validator)) != null);

		if (planner.HasJob())
		{
			return planner.MakeJob(storeTarget);
		}

		Log.Message($"{pawn} can not find a destination for {thing}");
		return HaulAIUtility.HaulToStorageJob(pawn, thing, forced);
	}

	private static bool HaulToHopperJob(Thing thing, IntVec3 targetCell, Map map)
	{
		if (thing.def.IsNutritionGivingIngestible
			&& thing.def.ingestible.preferability is FoodPreferability.RawBad or FoodPreferability.RawTasty)
		{
			var thingList = targetCell.GetThingList(map);
			for (var i = 0; i < thingList.Count; i++)
			{
				if (thingList[i].def == ThingDefOf.Hopper)
				{
					return true;
				}
			}
		}
		return false;
	}

	public static Thing GetClosestAndRemove(IntVec3 center, Map map, List<Thing> sortedSearchSet, PathEndMode peMode, TraverseParms traverseParams, float maxDistance = 9999f, Predicate<Thing> validator = null)
	{
		if (sortedSearchSet == null || sortedSearchSet.Count == 0)
		{
			return null;
		}

		var maxDistanceSquared = maxDistance * maxDistance;
		// list is sorted from furthest to closest, this allows pruning from the end of the list efficiently
		for (var i = sortedSearchSet.Count - 1; i >= 0; i--)
		{
			var candidate = sortedSearchSet[i];
			sortedSearchSet.RemoveAt(i);
			if ((center - candidate.Position).LengthHorizontalSquared > maxDistanceSquared)
			{
				break;
			}

			if (!map.reachability.CanReach(center, candidate, peMode, traverseParams))
			{
				continue;
			}

			if (validator == null || validator(candidate))
			{
				return candidate;
			}
		}

		return null;
	}

	public static bool AllocateThingAtCell2(DeliveryPlanner deliveryPlanner, Pawn pawn, Thing nextThing)
				{
		var map = pawn.Map;
		var storeCellCapacity = deliveryPlanner.StoreCellCapacity;
		var allocation = storeCellCapacity.FirstOrDefault(kvp =>
			kvp.Key is var storeTarget
			&& (storeTarget.Container?.TryGetInnerInteractableThingOwner().CanAcceptAnyOf(nextThing)
			?? storeTarget.Cell.GetSlotGroup(map).parent.Accepts(nextThing))
			&& deliveryPlanner.Stackable(nextThing, kvp));
		var storeCell = allocation.Key;

		var currentPriority = StoreUtility.CurrentStoragePriorityOf(nextThing);

		//Can't stack with allocated cells, find a new cell:
		if (storeCell == default)
		{
			if (deliveryPlanner.TryFindBestBetterStorageFor(nextThing, pawn, map, currentPriority, pawn.Faction, out var nextStoreCell, out var haulDestination, out var innerInteractableThingOwner))
			{
				if (innerInteractableThingOwner is null)
				{
					storeCell = new(nextStoreCell, null);
					deliveryPlanner.targetQueueB.Add(nextStoreCell);

					storeCellCapacity[storeCell] = new(nextStoreCell, map);
					Log.Message($"{pawn} New cell for {nextThing} = {nextStoreCell}");
				}
				else
				{
					var destinationAsThing = (Thing)haulDestination;
					storeCell = new(destinationAsThing.Position, destinationAsThing);
					deliveryPlanner.targetQueueB.Add(destinationAsThing);

					storeCellCapacity[storeCell] = new(innerInteractableThingOwner);
					Log.Message($"{pawn} New haulDestination for {nextThing} = {haulDestination}");
				}
			}
			else
			{
				Log.Message($"{pawn} can not find a destination for {nextThing}");

				return false;
			}
		}

		deliveryPlanner.targetQueueA.Add(nextThing);
		var count = nextThing.stackCount;
		Log.Message($"{pawn} allocating {nextThing}:{count}");

		var remaining = storeCellCapacity[storeCell].TryStoreThing(nextThing, count);
		Log.Message($"{pawn} allocated {nextThing}:{count - remaining} in {storeCell}");

		// If we have any remaining items we need to store, we need to look for a new location to store this item.
		while (remaining > 0)
		{
			// this customized variant skips cells/things which have been matched before as such any returned values are unique, but this is not per-type so later haulables can be under-delivered
			if (deliveryPlanner.TryFindBestBetterStorageFor(nextThing, pawn, map, currentPriority, pawn.Faction, out var nextStoreCell, out var nextHaulDestination, out var innerInteractableThingOwner))
			{
				if (innerInteractableThingOwner is null)
				{
					storeCell = new(nextStoreCell, null);
					deliveryPlanner.targetQueueB.Add(nextStoreCell);

					storeCellCapacity[storeCell] = new(nextStoreCell, map);
					Log.Message($"{pawn} found new cell for {nextThing}:{remaining} at {nextStoreCell} (extra)");
				}
				else
				{
					var destinationAsThing = (Thing)nextHaulDestination;
					storeCell = new(destinationAsThing.Position, destinationAsThing);
					deliveryPlanner.targetQueueB.Add(destinationAsThing);

					storeCellCapacity[storeCell] = new(innerInteractableThingOwner);
					Log.Message($"{pawn} found new haulDestination for {nextThing}:{remaining} at {storeCell.Cell} (extra)");
				}

				var beforeCount = remaining;
				remaining = storeCellCapacity[storeCell].TryStoreThing(nextThing, remaining);
				Log.Message($"{pawn} allocated another {nextThing}:{beforeCount - remaining} in {storeCell}");
			}
			else
			{
				// We were able to haul `count - remaining`.
				deliveryPlanner.countQueue.Add(count - remaining);
				Log.Message($"{pawn} could not find a place to store {nextThing.def.LabelCap}, was able to store {nextThing}:{count - remaining}");
				return false;
			}
		}
		deliveryPlanner.countQueue.Add(count);
		Log.Message($"{pawn} {nextThing}:{count} allocated");
		return true;
	}

	public static float AddedEncumberance(Pawn pawn, Thing thing)
		=> thing.stackCount * thing.GetStatValue(StatDefOf.Mass) / MassUtility.Capacity(pawn);

	public static int CountPastCapacity(Pawn pawn, Thing thing, float encumberance)
		=> (int)Math.Ceiling((encumberance - 1) * MassUtility.Capacity(pawn) / thing.GetStatValue(StatDefOf.Mass));
}
