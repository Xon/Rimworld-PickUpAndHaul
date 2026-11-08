namespace PickUpAndHaul;

public static class Bill_Count
{
	public static int AddCountForPending(RecipeWorkerCounter worker, Bill_Production bill, bool onlyMechs)
	{
		var count = 0;
		var thingDef = worker.recipe.products[0].thingDef;
		foreach (var pawn in bill.Map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer))
		{
			if (onlyMechs && !pawn.IsColonyMech)
			{
				continue;
			}

			var comp = pawn.HauledToInventoryComp();
			foreach (var thing in pawn.inventory.innerContainer)
			{
				if (comp.Contains(thing) && worker.CountValidThing(thing.GetInnerIfMinified(), bill, thingDef))
				{
					count += thing.stackCount;
				}
			}
		}
		return count;
	}
}
