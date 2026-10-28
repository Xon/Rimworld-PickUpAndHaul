namespace PickUpAndHaul.Deliveries;

public record struct StoreTarget(IntVec3 Cell, Thing Container)
{
	public override int GetHashCode() => Container?.GetHashCode() ?? Cell.GetHashCode();
	public static implicit operator LocalTargetInfo(StoreTarget target) => target.Container != null ? target.Container : target.Cell;
}
