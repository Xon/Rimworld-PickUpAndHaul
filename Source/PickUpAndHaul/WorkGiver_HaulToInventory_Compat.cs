using System.Linq;

namespace PickUpAndHaul;
public partial class WorkGiver_HaulToInventory
{
	[Obsolete]
	private static ThingPositionComparer Comparer { get; } = new();
	[Obsolete]
	public class ThingPositionComparer : IComparer<Thing>
	{
		public IntVec3 rootCell;
		public int Compare(Thing x, Thing y) => (x.Position - rootCell).LengthHorizontalSquared.CompareTo((y.Position - rootCell).LengthHorizontalSquared);
	}
}
