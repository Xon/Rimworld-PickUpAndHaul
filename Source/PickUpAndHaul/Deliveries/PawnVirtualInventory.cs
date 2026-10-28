namespace PickUpAndHaul.Deliveries;

public class PawnVirtualInventory(Pawn pawn)
{
	public readonly Pawn Pawn = pawn;
	public readonly float StartingMassUsage = MassUtility.GearAndInventoryMass(pawn);
	public readonly float Capacity = MassUtility.CanEverCarryAnything(pawn) ? MassUtility.Capacity(pawn) : 0;
	public readonly float HandsCapacity = 0;
	public readonly HashSet<ThingDef> ThingDefsToSkip = new();

	private readonly List<(Thing, int, StoreTarget)> _inventory = new ();
	private float _runningTotal = 0f;

	public void AddToInventory(Thing thing, int count, StoreTarget deliveryTarget)
	{
		if (count <= 0)
		{
			return;
		}

		_inventory.Add((thing, count, deliveryTarget));
		_runningTotal += count * thing.GetStatValue(StatDefOf.Mass);
	}

	public List<(Thing, int)> GetPickupList()
	{
		var list = new List<(Thing, int)>(_inventory.Count);
		foreach (var (thing, count, _) in _inventory)
		{
			list.Add((thing, count));
		}

		return list;
	}

	public float FreeSpace()
	{
		return Mathf.Max(Capacity - StartingMassUsage - _runningTotal, 0f);
	}

	public bool IsOverEncumbered()
	{
		return FreeSpace() <= 0f;
	}

	public bool WillBeOverEncumberedAfterPickingUp(Thing thing, int count)
	{
		return FreeSpace() < count * thing.GetStatValue(StatDefOf.Mass);
	}

	public int CountToPickUpUntilOverEncumbered(Thing thing)
	{
		return Mathf.FloorToInt(FreeSpace() / thing.GetStatValue(StatDefOf.Mass));
	}
}
