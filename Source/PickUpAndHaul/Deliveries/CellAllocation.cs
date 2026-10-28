namespace PickUpAndHaul.Deliveries;


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
