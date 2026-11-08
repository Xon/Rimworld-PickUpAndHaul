using System.Linq;

namespace PickUpAndHaul;

public static class Bill_Count
{
	public static int AddCountForPending(RecipeWorkerCounter worker, Bill_Production bill)
	{
		var count = 0;
		foreach (var pawn in bill.Map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer))
		{
			var comp = pawn.HauledToInventoryComp();
			var inventoryItems = pawn.inventory.innerContainer
				.Where(thing => comp.Contains(thing) && worker.CountValidThing(thing, bill, worker.recipe.products[0].thingDef))
				.Sum(x =>
				{
					Verse.Log.Message(x.Label);
					return x.stackCount;
				});
			count += inventoryItems;
		}
		
		return count;
	}
}
