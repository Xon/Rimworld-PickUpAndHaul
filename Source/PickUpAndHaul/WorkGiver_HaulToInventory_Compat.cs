using System.Linq;

namespace PickUpAndHaul;
public partial class WorkGiver_HaulToInventory
{
	[Obsolete]
	public static Thing FindClosestThing(List<Thing> searchSet, IntVec3 center, out int index)
	{
		index = 0;
		return null;
	}

	[Obsolete]
	public static bool IsNotCorpseOrAllowed(Thing t) => Settings.AllowCorpses || t is not Corpse;

	[Obsolete]
	private static ThingPositionComparer Comparer { get; } = new();
	[Obsolete]
	public class ThingPositionComparer : IComparer<Thing>
	{
		public IntVec3 rootCell;
		public int Compare(Thing x, Thing y) => (x.Position - rootCell).LengthHorizontalSquared.CompareTo((y.Position - rootCell).LengthHorizontalSquared);
	}
}

[Obsolete]
public static class PickUpAndHaulDesignationDefOf
{
	[Obsolete]
	public static DesignationDef haulUrgently = DefDatabase<DesignationDef>.GetNamedSilentFail("HaulUrgentlyDesignation");
}
