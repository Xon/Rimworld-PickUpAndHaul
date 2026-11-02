namespace PickUpAndHaul;


public class FloatMenuOptionProvider_UnloadInventory: FloatMenuOptionProvider
{
	// Publicizer causes these to be public
	public override bool Drafted => false;
	public override bool Undrafted => true;
	public override bool Multiselect => true;
	public override bool RequiresManipulation => true;
	public override bool MechanoidCanDo => true;

	public override FloatMenuOption GetSingleOption(FloatMenuContext context)
	{
		var hasInventoryToUnload = false;
		foreach (var pawn in context.ValidSelectedPawns)
		{
			if (!pawn.isAutoUnloadInventoryAllowed() || (pawn.HauledToInventoryComp()?.IsEmpty() ?? true))
			{
				continue;
			}

			hasInventoryToUnload = true;
			break;
		}
		if (!hasInventoryToUnload)
		{
			return null;
		}

		return new FloatMenuOption("PUAH.UnloadInventory".Translate(), () =>
		{
			foreach (var pawn in context.ValidSelectedPawns)
			{
				if (!pawn.isAutoUnloadInventoryAllowed() || (pawn.HauledToInventoryComp()?.IsEmpty() ?? true))
				{
					continue;
				}

				var job = JobMaker.MakeJob(JobDefOfs.UnloadYourHauledInventory, pawn);
				pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
			}
		}, MenuOptionPriority.High);
	}
}
