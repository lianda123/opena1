using System.Collections.Generic;
using Rhino.Geometry;

namespace ExplodeBook.Core;

internal sealed class AssemblyModule
{
    public bool PathVerified { get; set; }
    public double PathTravel { get; set; }

	public int Sequence { get; set; }

	public int AssemblyOrder { get; set; }

	public string Number { get; set; }

	public string Name { get; set; }

	public bool IsBase { get; set; }

	public Vector3d AutoDirection { get; set; } = Vector3d.Unset;

	public List<AssemblyPart> Parts { get; } = new List<AssemblyPart>();

	public BoundingBox Bounds { get; set; } = BoundingBox.Unset;

	public Point3d Center => Bounds.Center;

	public double SizeScore { get; set; }
}
