using System.Linq;

namespace PickUpAndHaul;
public class WorkGiver_HaulToInventory : WorkGiver_HaulGeneral
{
	//Thanks to AlexTD for the more dynamic search range
	//And queueing
	//And optimizing
	private const float SEARCH_FOR_OTHERS_RANGE_FRACTION = 0.5f;

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
		return SortedHaulables(pawn);
	}

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

	private static ThingPositionComparer Comparer { get; } = new();
	public class ThingPositionComparer : IComparer<Thing>
	{
		public IntVec3 rootCell;
		public int Compare(Thing x, Thing y) => (y.Position - rootCell).LengthHorizontalSquared.CompareTo((x.Position - rootCell).LengthHorizontalSquared);
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
		var storeCellCapacity = new Dictionary<StoreTarget, CellAllocation>();
		StoreTarget storeTarget;
		int capacityStoreCell;
		if (!StoreUtility.TryFindBestBetterStorageFor(thing, pawn, map, currentPriority, pawn.Faction, out var targetCell, out var haulDestination, true))
		{
			JobFailReason.Is("NoEmptyPlaceLower".Translate());
			return null;
		}

		ThingOwner nonSlotGroupThingOwner;
		if (haulDestination is ISlotGroupParent)
		{
			//since we've gone through all the effort of getting the loc, might as well use it.
			//Don't multi-haul food to hoppers.
			if (HaulToHopperJob(thing, targetCell, map))
			{
				return HaulAIUtility.HaulToStorageJob(pawn, thing, forced);
			}
			storeTarget = new(targetCell);
			capacityStoreCell = CapacityAt(thing, storeTarget.cell, map);
			storeCellCapacity[storeTarget] = new CellAllocation(targetCell, map);
		}
		else if (haulDestination is Thing destinationAsThing && (nonSlotGroupThingOwner = destinationAsThing.TryGetInnerInteractableThingOwner()) != null)
		{
			storeTarget = new(destinationAsThing);
			capacityStoreCell = nonSlotGroupThingOwner.GetCountCanAccept(thing);
			storeCellCapacity[storeTarget] = new CellAllocation(nonSlotGroupThingOwner);
		}
		else
		{
			Verse.Log.Error("Don't know how to handle HaulToStorageJob for storage " + haulDestination.ToStringSafe() + ". thing=" + thing.ToStringSafe());
			return null;
		}

		if (capacityStoreCell == 0)
		{
			return HaulAIUtility.HaulToStorageJob(pawn, thing, forced);
		}

		var job = JobMaker.MakeJob(JobDefOfs.HaulToInventory, null, storeTarget);   //Things will be in queues
		Log.Message($"-------------------------------------------------------------------");
		Log.Message($"------------------------------------------------------------------");//different size so the log doesn't count it 2x
		Log.Message($"{pawn} job found to haul: {thing} to {storeTarget}:{capacityStoreCell}, looking for more now");

		//Find what fits in inventory, set nextThingLeftOverCount to be
		var nextThingLeftOverCount = 0;
		var encumberance = MassUtility.EncumbrancePercent(pawn);
		job.targetQueueA = new List<LocalTargetInfo>(); //more things
		job.targetQueueB = new List<LocalTargetInfo>(); //more storage; keep in mind the job doesn't use it, but reserve it so you don't over-haul
		job.countQueue = new List<int>();//thing counts

		var ceOverweight = false;

		if (ModCompatibilityCheck.CombatExtendedIsActive)
		{
			ceOverweight = CompatHelper.CeOverweight(pawn);
		}

		var distanceToHaul = (storeTarget.Position - thing.Position).LengthHorizontal * SEARCH_FOR_OTHERS_RANGE_FRACTION;
		var distanceToSearchMore = Math.Max(12f, distanceToHaul);

		//Find extra things than can be hauled to inventory, queue to reserve them
		var haulUrgentlyDesignation = DesignationDefs.HaulUrgentlyDesignation;
		var isUrgent = ModCompatibilityCheck.AllowToolIsActive && designationManager.DesignationOn(thing)?.def == haulUrgentlyDesignation;

		// list should be considered cachable, so copy it into a local list preserving sorting but filter out the item
		var haulablesRelativeToPawn = SortedHaulables(pawn);
		var haulables = new List<Thing>(haulablesRelativeToPawn);
		haulables.Remove(thing);

		var nextThing = thing;
		var lastThing = thing;

		//skipTargets = new() { storeTarget };
		skipCells = new();
		skipThings = new();
		if (storeTarget.container != null)
		{
			skipThings.Add(storeTarget.container);
		}
		else
		{
			skipCells.Add(storeTarget.cell);
		}

		bool Validator(Thing t)
			=> (!isUrgent || designationManager.DesignationOn(t)?.def == haulUrgentlyDesignation)
			&& GoodThingToHaul(t, pawn) && HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, t, false); //forced is false, may differ from first thing

		var traverseParms = TraverseParms.For(pawn);

		do
		{
			if (AllocateThingAtCell(storeCellCapacity, pawn, nextThing, job))
			{
				lastThing = nextThing;
				encumberance += AddedEncumberance(pawn, nextThing);

				if (encumberance > 1 || ceOverweight)
				{
					//can't CountToPickUpUntilOverEncumbered here, pawn doesn't actually hold these things yet
					nextThingLeftOverCount = CountPastCapacity(pawn, nextThing, encumberance);
					Log.Message($"{pawn} Inventory allocated, will carry {nextThing}:{nextThingLeftOverCount}");
					job.countQueue.Pop();
					job.countQueue.Add(nextThingLeftOverCount);

					// We are now out of inventory space - and should bail right away.
					break;
				}
			}
		}
		while ((nextThing = GetClosestAndRemove(lastThing.Position, map, haulables, PathEndMode.ClosestTouch,
			       traverseParms, distanceToSearchMore, Validator)) != null);

		skipCells = null;
		skipThings = null;
		return job;
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

	public struct StoreTarget : IEquatable<StoreTarget>
	{
		public IntVec3 cell;
		public Thing container;
		public IntVec3 Position => container?.Position ?? cell;

		public StoreTarget(IntVec3 cell)
		{
			this.cell = cell;
			container = null;
		}
		public StoreTarget(Thing container)
		{
			cell = default;
			this.container = container;
		}

		public bool Equals(StoreTarget other) => container is null ? other.container is null && cell == other.cell : container == other.container;
		public override int GetHashCode() => container?.GetHashCode() ?? cell.GetHashCode();
		public override string ToString() => container?.ToString() ?? cell.ToString();
		public override bool Equals(object obj) => obj is StoreTarget target ? Equals(target) : obj is Thing thing ? container == thing : obj is IntVec3 intVec && cell == intVec;
		public static bool operator ==(StoreTarget left, StoreTarget right) => left.Equals(right);
		public static bool operator !=(StoreTarget left, StoreTarget right) => !left.Equals(right);
		public static implicit operator LocalTargetInfo(StoreTarget target) => target.container != null ? target.container : target.cell;
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

	// We effectively need to store a structure which looks like the below:
	// struct Cell { List<List<Thing, Count>> stacks; }
	// Where each `List<Thing, Count>` represents a stack of things which can be stored in a single cell, i.e. multiple
	// partial stacks, i.e. `Meat x33, Meat x20, ...`. This can happen in the case we are intending on hauling to a
	// building, but haven't reached there yet.
	// We need another list, because a single `Cell` can contain multiple stacks, in the case of storage buildings.
	public class CellAllocation
	{
		private struct Cell
		{
			public int id;
			public Thing thing;
			public int count;

			public Cell(int id, Thing thing)
			{
				this.id = id;
				this.thing = thing;
				this.count = thing.stackCount;
			}

			public Cell(int id, Thing thing, int count)
			{
				this.id = id;
				this.thing = thing;
				this.count = count;
			}
		}

		// How many item stacks we can fit in a particular cell. i.e. storage buildings this can be > 1 - must be at
		// least equal to one.
		private int maximumAllowedStacks;
		private int freeCells;

		// The existing items which have space on the map, plus the items which we would like to haul ontop of them,
		// elements are removed from this list when there is not enough space to haul ontop of them.

		// invariant: cells with the same `id` must be next to each other in `desiredContents`
		private List<Cell> desiredContents;
		private int cid;

		public CellAllocation(IntVec3 cell, Map map)
		{
			maximumAllowedStacks = cell.GetMaxItemsAllowedInCell(map);
			freeCells = maximumAllowedStacks;
			desiredContents = new List<Cell>();
			cid = 0;

			var thingList = cell.GetThingList(map);
			foreach (var thing in thingList)
			{
				// if its a building, or plant we don't care - if its a stack, but its full - we don't care either.
				if (thing.def.category != ThingCategory.Item || thing.def.stackLimit == thing.stackCount) { continue; }

				// We have a partial stack we can add to, potentially
				desiredContents.Add(new Cell(cid, thing));
				// this still takes a cell slot in the storage
				freeCells -= 1;
				// if we have multiple partial stacks, we need to differentiate them.
				cid += 1;
			}
		}

		public CellAllocation(ThingOwner thingOwner)
		{
			maximumAllowedStacks = thingOwner.maxStacks;
			freeCells = maximumAllowedStacks - thingOwner.Count;
			desiredContents = new List<Cell>();
			cid = 0;

			for (int i = 0; i < thingOwner.Count; ++i)
			{
				var thing = thingOwner.GetAt(i);
				if (thing.stackCount < thing.def.stackLimit)
				{
					desiredContents.Add(new Cell(cid, thing));
				}
			}
		}

		// Returns the `stackCount` of the `thing` which did not fit in `this`.
		public int TryStoreThing(Thing thing, int storeAmount)
		{
			// How much of the item we need to store, this gets updated as we allocate bits of space.
			var requestedStorageAmount = storeAmount;

			// Stage 1 - Try merge with existing stacks:
			int cellCapacity = thing.def.stackLimit;
			bool cellCanStore = true;
			for (int i = 0; i < desiredContents.Count; ++i)
			{
				var cell = desiredContents[i];
				if (cell.thing.def != thing.def)
				{
					continue;
				}

				cellCapacity -= cell.count;
				cellCanStore &= cell.thing.CanStackWith(thing);

				// If our next slot is not pertaining to our current cell
				if (i + 1 == desiredContents.Count || cell.id != desiredContents[i + 1].id)
				{
					if (cellCanStore)
					{
						var storedCount = Math.Min(requestedStorageAmount, cellCapacity);
						requestedStorageAmount -= storedCount;

						// We have fully utilized the slot represented by `cell.id`. We need to remove all partial
						// stakcs that reference `cell.id` from our list.
						if (storedCount == cellCapacity)
						{
							var fullyUtilisedCellId = cell.id;

							// Walk backwards destroying cells which are now considered full.
							while (i >= 0 && desiredContents[i].id == fullyUtilisedCellId) {
								desiredContents.RemoveAt(i);
								i -= 1;
							}
						}
						else // We have added to `cell.id` - but not yet completely utilised it, we need to track our addition
						{
							desiredContents.Insert(i + 1, new Cell(cell.id, thing, storedCount));
						}

						// If we have satisfied our storage request - we can bail.
						if (requestedStorageAmount == 0)
						{
							return 0;
						}
					}

					cellCapacity = thing.def.stackLimit;
					cellCanStore = true;
				}
			}

			// Stage 2 - Allocate a new stack from the storage if possible.
			if (freeCells > 0)
			{
				freeCells -= 1;

				// If we are not storing an entire stack, we need to store the partial amount we are storing, so we can
				// fully use the cell.
				if (requestedStorageAmount != thing.def.stackLimit)
				{
					desiredContents.Add(new Cell(cid, thing, requestedStorageAmount));
					++cid;
				}

				return 0;
			}

			return requestedStorageAmount;
		}

		public bool IsStackable(Thing thing)
		{
			if (freeCells > 0)
			{
				return true;
			}

			bool stacksWith = true;
			for (int i = 0; i < desiredContents.Count; ++i)
			{
				var cell = desiredContents[i];
				if (cell.thing.def != thing.def)
				{
					continue;
				}

				stacksWith &= cell.thing.CanStackWith(thing);

				// If our next slot is not pertaining to our current cell
				if (i + 1 == desiredContents.Count || cell.id != desiredContents[i + 1].id)
				{
					// If `thing` stacks with each thing for `cell.id` - we can add ontop of it.
					if (stacksWith)
					{
						return true;
					}

					stacksWith = true;
				}
			}

			return false;
		}
	}

	public static int CapacityAt(Thing thing, IntVec3 storeCell, Map map)
	{
		if (HoldMultipleThings_Support.CapacityAt(thing, storeCell, map, out var capacity))
		{
			Log.Message($"Found {storeCell} external capacity of {capacity}");
			return capacity;
		}

		return storeCell.GetItemStackSpaceLeftFor(map, thing.def);
	}

	public static bool Stackable(Thing nextThing, KeyValuePair<StoreTarget, CellAllocation> allocation)
		=> allocation.Value.IsStackable(nextThing)
		|| HoldMultipleThings_Support.StackableAt(nextThing, allocation.Key.cell, nextThing.Map);

	public static bool AllocateThingAtCell(Dictionary<StoreTarget, CellAllocation> storeCellCapacity, Pawn pawn, Thing nextThing, Job job)
	{
		var map = pawn.Map;
		var allocation = storeCellCapacity.FirstOrDefault(kvp =>
			kvp.Key is var storeTarget
			&& (storeTarget.container?.TryGetInnerInteractableThingOwner().CanAcceptAnyOf(nextThing)
			?? storeTarget.cell.GetSlotGroup(map).parent.Accepts(nextThing))
			&& Stackable(nextThing, kvp));
		var storeCell = allocation.Key;

		var currentPriority = StoreUtility.CurrentStoragePriorityOf(nextThing);

		//Can't stack with allocated cells, find a new cell:
		if (storeCell == default)
		{
			if (TryFindBestBetterStorageFor(nextThing, pawn, map, currentPriority, pawn.Faction, out var nextStoreCell, out var haulDestination, out var innerInteractableThingOwner))
			{
				if (innerInteractableThingOwner is null)
				{
					storeCell = new(nextStoreCell);
					job.targetQueueB.Add(nextStoreCell);

					storeCellCapacity[storeCell] = new(nextStoreCell, map);
					Log.Message($"{pawn} New cell for {nextThing} = {nextStoreCell}");
				}
				else
				{
					var destinationAsThing = (Thing)haulDestination;
					storeCell = new(destinationAsThing);
					job.targetQueueB.Add(destinationAsThing);

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

		job.targetQueueA.Add(nextThing);
		var count = nextThing.stackCount;
		Log.Message($"{pawn} allocating {nextThing}:{count}");

		var remaining = storeCellCapacity[storeCell].TryStoreThing(nextThing, count);
		Log.Message($"{pawn} allocated {nextThing}:{count - remaining} in {storeCell}");

		// If we have any remaining items we need to store, we need to look for a new location to store this item.
		while (remaining > 0)
		{
			// this customized variant skips cells/things which have been matched before as such any returned values are unique, but this is not per-type so later haulables can be under-delivered
			if (TryFindBestBetterStorageFor(nextThing, pawn, map, currentPriority, pawn.Faction, out var nextStoreCell, out var nextHaulDestination, out var innerInteractableThingOwner))
			{
				if (innerInteractableThingOwner is null)
				{
					storeCell = new(nextStoreCell);
					job.targetQueueB.Add(nextStoreCell);

					storeCellCapacity[storeCell] = new(nextStoreCell, map);
					Log.Message($"{pawn} found new cell for {nextThing}:{remaining} at {nextStoreCell} (extra)");
				}
				else
				{
					var destinationAsThing = (Thing)nextHaulDestination;
					storeCell = new(destinationAsThing);
					job.targetQueueB.Add(destinationAsThing);

					storeCellCapacity[storeCell] = new(innerInteractableThingOwner);
					Log.Message($"{pawn} found new haulDestination for {nextThing}:{remaining} at {storeCell.cell} (extra)");
				}

				var beforeCount = remaining;
				remaining = storeCellCapacity[storeCell].TryStoreThing(nextThing, remaining);
				Log.Message($"{pawn} allocated another {nextThing}:{beforeCount - remaining} in {storeCell}");
			}
			else
			{
				// We were able to haul `count - remaining`.
				job.countQueue.Add(count - remaining);
				Log.Message($"{pawn} could not find a place to store {nextThing.def.LabelCap}, was able to store {nextThing}:{count - remaining}");
				return false;
			}
		}
		job.countQueue.Add(count);
		Log.Message($"{pawn} {nextThing}:{count} allocated");
		return true;
	}

	//public static HashSet<StoreTarget> skipTargets;
	public static HashSet<IntVec3> skipCells;
	public static HashSet<Thing> skipThings;

	public static bool TryFindBestBetterStorageFor(Thing t, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell, out IHaulDestination haulDestination, out ThingOwner innerInteractableThingOwner)
	{
		var storagePriority = StoragePriority.Unstored;
		innerInteractableThingOwner = null;
		if (TryFindBestBetterStoreCellFor(t, carrier, map, currentPriority, faction, out var foundCell2))
		{
			storagePriority = foundCell2.GetSlotGroup(map).Settings.Priority;
		}

		if (!TryFindBestBetterNonSlotGroupStorageFor(t, carrier, map, currentPriority, faction, out var haulDestination2))
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

	public static bool TryFindBestBetterStoreCellFor(Thing thing, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell)
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
				if (skipCells.Contains(cell))
				{
					continue;
				}

				if (StoreUtility.IsGoodStoreCell(cell, map, thing, carrier, faction) && cell != default)
				{
					foundCell = cell;

					skipCells.Add(cell);

					return true;
				}
			}
		}
		foundCell = IntVec3.Invalid;
		return false;
	}

	public static float AddedEncumberance(Pawn pawn, Thing thing)
		=> thing.stackCount * thing.GetStatValue(StatDefOf.Mass) / MassUtility.Capacity(pawn);

	public static int CountPastCapacity(Pawn pawn, Thing thing, float encumberance)
		=> (int)Math.Ceiling((encumberance - 1) * MassUtility.Capacity(pawn) / thing.GetStatValue(StatDefOf.Mass));

	public static bool TryFindBestBetterNonSlotGroupStorageFor(Thing t, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IHaulDestination haulDestination, bool acceptSamePriority = false)
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
				if (skipThings.Contains(thing) || thing.Faction != faction)
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

				skipThings.Add(thing);
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
