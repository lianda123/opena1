using Rhino.Geometry;

namespace ExplodeBook.Core;

internal sealed class ProjectedPart
{
	public bool Highlight { get; set; }

	public AssemblyPart Part { get; set; }

	public Transform Transform { get; set; }
}
