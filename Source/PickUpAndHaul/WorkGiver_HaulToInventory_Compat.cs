namespace PickUpAndHaul;

public partial class WorkGiver_HaulToInventory
{
	//public static HashSet<StoreTarget> skipTargets;
	[Obsolete("Not used")]
	public static HashSet<IntVec3> skipCells;
	[Obsolete("Not used")]
	public static HashSet<Thing> skipThings;

	[Obsolete("Use DeliveryPlanner.TryFindBestBetterStorageFor instead")]
	public static bool TryFindBestBetterStorageFor(Thing t, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell, out IHaulDestination haulDestination, out ThingOwner innerInteractableThingOwner)
	{
		foundCell = IntVec3.Invalid;
		haulDestination = null;
		innerInteractableThingOwner = null;
		return false;
	}

	[Obsolete("Use DeliveryPlanner.TryFindBestBetterStoreCellFor instead")]
	public static bool TryFindBestBetterStoreCellFor(Thing thing, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell)
	{
		foundCell = IntVec3.Invalid;
		return false;
	}

	[Obsolete("Use DeliveryPlanner.TryFindBestBetterNonSlotGroupStorageFor instead")]
	public static bool TryFindBestBetterNonSlotGroupStorageFor(Thing t, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IHaulDestination haulDestination, bool acceptSamePriority = false)
	{
		haulDestination = null;
		return false;
	}

	[Obsolete("Use DeliveryPlanner.Stackable instead")]
	public static bool Stackable(Thing nextThing, KeyValuePair<StoreTarget, CellAllocation> allocation) => false;

	[Obsolete]
	private static ThingPositionComparer Comparer { get; } = new();

	//[Obsolete]
	public static int CapacityAt(Thing thing, IntVec3 storeCell, Map map)
	{
		if (HoldMultipleThings_Support.CapacityAt(thing, storeCell, map, out var capacity))
		{
			Log.Message($"Found {storeCell} external capacity of {capacity}");
			return capacity;
		}

		return storeCell.GetItemStackSpaceLeftFor(map, thing.def);
	}

	[Obsolete]
	public class CellAllocation
	{
		public Thing allocated;
		public int capacity;

		public CellAllocation(Thing a, int c)
		{
			allocated = a;
			capacity = c;
		}
	}

	[Obsolete]
	public static bool AllocateThingAtCell(Dictionary<StoreTarget, CellAllocation> storeCellCapacity, Pawn pawn, Thing nextThing, Job job)
	{
		return false;
	}
}
