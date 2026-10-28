namespace PickUpAndHaul.Deliveries;

public class DeliveryPlanner(Map map)
{
	public readonly HashSet<IntVec3> SkipCells = new();
	public readonly HashSet<Thing> SkipThings = new();
	public readonly Dictionary<StoreTarget, CellAllocation> StoreCellCapacity = new();

	private static WorkGiver_HaulToInventory.ThingPositionComparer Comparer { get; } = new();

	/// <summary>
	/// GetClosestAndRemove depends on this being sorted from furthest to closest
	/// </summary>
	public static List<Thing> SortedHaulables(Pawn pawn)
	{
		var list = new List<Thing>();
		foreach (var thing in pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling())
		{
			if (thing.Spawned)
			{
				list.Add(thing);
			}
		}
		Comparer.rootCell = pawn.Position;
		list.Sort(Comparer);
		return list;
	}

	public readonly List<LocalTargetInfo> targetQueueA = new(); //more things
	public readonly List<LocalTargetInfo> targetQueueB = new(); //more storage; keep in mind the job doesn't use it, but reserve it so you don't over-haul
	public readonly List<int> countQueue = new();//thing counts

	public bool HasJob()
	{
		return targetQueueA.Count > 0;
	}

	public Job MakeJob(StoreTarget storeTarget)
	{
		var job = JobMaker.MakeJob(JobDefOfs.HaulToInventory, null, storeTarget);

		job.targetQueueA = targetQueueA;
		job.targetQueueB = targetQueueB;
		job.countQueue = countQueue;

		return job;
	}

	public bool Stackable(Thing nextThing, KeyValuePair<StoreTarget, CellAllocation> allocation)
		=> allocation.Value.IsStackable(nextThing)
		   || HoldMultipleThings_Support.StackableAt(nextThing, allocation.Key.Cell, nextThing.Map);

	public bool TryWrapDestination(IntVec3 targetCell, IHaulDestination haulDestination, out StoreTarget storeTarget)
	{
		if (haulDestination is ISlotGroupParent)
		{
			storeTarget = new StoreTarget(targetCell, null);
			StoreCellCapacity[storeTarget] = new CellAllocation(targetCell, map);

			return true;
		}

		ThingOwner nonSlotGroupThingOwner;
		if (haulDestination is Thing destinationAsThing && (nonSlotGroupThingOwner = destinationAsThing.TryGetInnerInteractableThingOwner()) != null)
		{
			storeTarget = new StoreTarget(destinationAsThing.Position, destinationAsThing);
			StoreCellCapacity[storeTarget] = new CellAllocation(nonSlotGroupThingOwner);

			return true;
		}

		storeTarget = default;
		return false;
	}

	public bool TryFindBestBetterStorageFor(Thing t, Pawn carrier, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell, out IHaulDestination haulDestination, out ThingOwner innerInteractableThingOwner)
	{
		var storagePriority = StoragePriority.Unstored;
		innerInteractableThingOwner = null;
		if (TryFindBestBetterStoreCellFor(t, carrier, currentPriority, faction, out var foundCell2))
		{
			storagePriority = foundCell2.GetSlotGroup(map).Settings.Priority;
		}

		if (!TryFindBestBetterNonSlotGroupStorageFor(t, carrier, currentPriority, faction, out var haulDestination2))
		{
			haulDestination2 = null;
		}

		if (storagePriority == StoragePriority.Unstored && haulDestination2 == null)
		{
			foundCell = IntVec3.Invalid;
			haulDestination = null;
			return false;
		}

		if (haulDestination2 != null && (storagePriority == StoragePriority.Unstored || (int)haulDestination2.GetStoreSettings().Priority > (int)storagePriority))
		{
			foundCell = IntVec3.Invalid;
			haulDestination = haulDestination2;

			if (haulDestination2 is not Thing destinationAsThing)
			{
				Verse.Log.Error($"{haulDestination2} is not a valid Thing. Pick Up And Haul can't work with this");
			}
			else
			{
				innerInteractableThingOwner = destinationAsThing.TryGetInnerInteractableThingOwner();
			}

			if (innerInteractableThingOwner is null)
			{
				Verse.Log.Error($"{haulDestination2} gave null ThingOwner during lookup in Pick Up And Haul's WorkGiver_HaulToInventory");
			}

			return true;
		}

		foundCell = foundCell2;
		haulDestination = foundCell2.GetSlotGroup(map).parent;
		return true;
	}

	private bool TryFindBestBetterStoreCellFor(Thing thing, Pawn carrier, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell)
	{
		var haulDestinations = map.haulDestinationManager.AllGroupsListInPriorityOrder;
		for (var i = 0; i < haulDestinations.Count; i++)
		{
			var slotGroup = haulDestinations[i];
			if (slotGroup.Settings.Priority <= currentPriority || !slotGroup.parent.Accepts(thing))
			{
				continue;
			}

			var cellsList = slotGroup.CellsList;

			for (var j = 0; j < cellsList.Count; j++)
			{
				var cell = cellsList[j];
				if (SkipCells.Contains(cell))
				{
					continue;
				}

				if (StoreUtility.IsGoodStoreCell(cell, map, thing, carrier, faction) && cell != default)
				{
					foundCell = cell;

					SkipCells.Add(cell);

					return true;
				}
			}
		}
		foundCell = IntVec3.Invalid;
		return false;
	}

	private bool TryFindBestBetterNonSlotGroupStorageFor(Thing t, Pawn carrier, StoragePriority currentPriority, Faction faction, out IHaulDestination haulDestination, bool acceptSamePriority = false)
	{
		var allHaulDestinationsListInPriorityOrder = map.haulDestinationManager.AllHaulDestinationsListInPriorityOrder;
		var intVec = t.SpawnedOrAnyParentSpawned ? t.PositionHeld : carrier.PositionHeld;
		var num = float.MaxValue;
		var storagePriority = StoragePriority.Unstored;
		haulDestination = null;
		for (var i = 0; i < allHaulDestinationsListInPriorityOrder.Count; i++)
		{
			var iHaulDestination = allHaulDestinationsListInPriorityOrder[i];

			if (iHaulDestination is ISlotGroupParent || (iHaulDestination is Building_Grave && !t.CanBeBuried()))
			{
				continue;
			}

			var priority = iHaulDestination.GetStoreSettings().Priority;
			if ((int)priority < (int)storagePriority || (acceptSamePriority && (int)priority < (int)currentPriority) || (!acceptSamePriority && (int)priority <= (int)currentPriority))
			{
				break;
			}

			float num2 = intVec.DistanceToSquared(iHaulDestination.Position);
			if (num2 > num || !iHaulDestination.Accepts(t))
			{
				continue;
			}

			if (iHaulDestination is Thing thing)
			{
				if (SkipThings.Contains(thing) || thing.Faction != faction)
				{
					continue;
				}

				if (carrier != null)
				{
					if (thing.IsForbidden(carrier)
						|| !carrier.CanReserveNew(thing)
						|| !carrier.Map.reachability.CanReach(intVec, thing, PathEndMode.ClosestTouch, TraverseParms.For(carrier)))
					{
						continue;
					}
				}
				else if (faction != null)
				{
					if (thing.IsForbidden(faction) || map.reservationManager.IsReservedByAnyoneOf(thing, faction))
					{
						continue;
					}
				}

				SkipThings.Add(thing);
			}
			else
			{
				//not supported. Seems dumb
				continue;

				//if (carrier != null && !carrier.Map.reachability.CanReach(intVec, iHaulDestination.Position, PathEndMode.ClosestTouch, TraverseParms.For(carrier)))
				//	continue;
			}

			num = num2;
			storagePriority = priority;
			haulDestination = iHaulDestination;
		}

		return haulDestination != null;
	}
}
