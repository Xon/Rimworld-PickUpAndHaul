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


		var virtualInventory = new PawnVirtualInventory(pawn);

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
			if (!virtualInventory.ThingDefsToSkip.Contains(nextThing.def)
			    && ScheduleDeliveryFor(planner, virtualInventory, nextThing))
			{
				lastThing = nextThing;
			}

			// unlikely for the inventory to be perfectly packed, but if so stop looking for new things to pickup
			if (virtualInventory.IsOverEncumbered())
			{
				break;
			}
		} while ((nextThing = GetClosestAndRemove(lastThing.Position, map, haulables, PathEndMode.ClosestTouch,
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

	public static bool ScheduleDeliveryFor(DeliveryPlanner deliveryPlanner, PawnVirtualInventory inventory, Thing thing)
	{
		var countToPickupRequest = inventory.CountToPickUpUntilOverEncumbered(thing);
		if (countToPickupRequest <= 0)
		{
			// this thingDef can't be added at all, so skip over attempting deliveries but keep checking for things without that def
			inventory.ThingDefsToSkip.Add(thing.def);

			return false;
		}

		var countToPickup = countToPickupRequest;
		var currentPriority = StoreUtility.CurrentStoragePriorityOf(thing);
		while (countToPickup > 0)
		{
			if (!ScheduleDeliveryFor(deliveryPlanner, inventory.Pawn, currentPriority, thing, countToPickup, out var pickedUpCount, out var deliveryTarget))
			{
				break;
			}

			Log.Message($"{inventory.Pawn} will pickup {thing}:{pickedUpCount} from {thing.Position} and deliver to {deliveryTarget}");
			inventory.AddToInventory(thing, pickedUpCount, deliveryTarget);
			countToPickup -= pickedUpCount;
		}

		return countToPickup < countToPickupRequest;
	}

	public static bool ScheduleDeliveryFor(DeliveryPlanner deliveryPlanner, Pawn pawn, StoragePriority currentPriority, Thing thing, int count, out int deliveredCount, out Deliveries.StoreTarget deliveryTarget)
	{
		// this customized variant skips cells/things which have been matched before as such any returned values are unique, but this is not per-type so later haulables can be under-delivered
		if (deliveryPlanner.TryFindBestBetterStorageFor(thing, pawn, currentPriority, pawn.Faction, out var nextStoreCell, out var haulDestination, out var innerInteractableThingOwner))
		{
			if (innerInteractableThingOwner is null)
			{
				deliveryTarget = new Deliveries.StoreTarget(nextStoreCell, null);
				if (!deliveryPlanner.StoreCellCapacity.ContainsKey(deliveryTarget))
				{
					deliveryPlanner.StoreCellCapacity[deliveryTarget] = new Deliveries.CellAllocation(nextStoreCell, pawn.Map);
					Log.Message($"{pawn} New cell for {thing} = {nextStoreCell}");
				}
			}
			else
			{
				var destinationAsThing = (Thing)haulDestination;
				deliveryTarget = new Deliveries.StoreTarget(destinationAsThing.Position, destinationAsThing);
				if (!deliveryPlanner.StoreCellCapacity.ContainsKey(deliveryTarget))
				{
					deliveryPlanner.StoreCellCapacity[deliveryTarget] = new Deliveries.CellAllocation(innerInteractableThingOwner);
					Log.Message($"{pawn} New haulDestination for {thing} = {haulDestination}");
				}
			}
		}
		else
		{
			Log.Message($"{pawn} can not find a destination for {thing}");

			deliveredCount = 0;
			deliveryTarget = default;
			return false;
		}

		Log.Message($"{pawn} allocating {thing}:{count}");
		var remaining = deliveryPlanner.StoreCellCapacity[deliveryTarget].TryStoreThing(thing, count);
		deliveredCount = count - remaining;
		if (deliveredCount > 0)
		{
			Log.Message($"{pawn} allocated {thing}:{count - remaining} in {deliveryTarget}");
			deliveryPlanner.targetQueueB.Add(nextStoreCell);
			deliveryPlanner.targetQueueA.Add(thing);
			deliveryPlanner.countQueue.Add(count);

			return true;
		}

		Log.Message($"{pawn} can not store any {thing}:{count} in {deliveryTarget}");

		return false;
	}
}
