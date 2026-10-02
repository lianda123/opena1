using System.Collections.Generic;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace ExplodeBook.Core;

internal sealed class AssemblyPart
{
    public bool PathVerified { get; set; }
    public double PathTravel { get; set; }

	public int Sequence { get; set; }

	public int AssemblyOrder { get; set; }

	public string PartNumber { get; set; }

	public string Name { get; set; }

	public string Subassembly { get; set; }

	public int ModuleOrder { get; set; }

	public bool IsBase { get; set; }

	public bool IsForcedBase { get; set; }

	public bool HasCustomDirection { get; set; }

	public Vector3d CustomDirection { get; set; } = Vector3d.Unset;

	public Vector3d AutoDirection { get; set; } = Vector3d.Unset;

	public bool HasExactExplosionOffset { get; set; }

	public Vector3d ExactExplosionOffset { get; set; } = Vector3d.Unset;

	public bool HasLabelOffset { get; set; }

	public Vector3d LabelOffset { get; set; } = Vector3d.Unset;

	public bool HasArrowAnchorOffset { get; set; }

	public Vector3d ArrowAnchorOffset { get; set; } = Vector3d.Unset;

	public int Quantity { get; set; } = 1;

	public int ModulePartOrder { get; set; }

	public bool IsModuleBase { get; set; }

	public List<RhinoObject> Objects { get; } = new List<RhinoObject>();

	public BoundingBox Bounds { get; set; } = BoundingBox.Unset;

	public Point3d Center => Bounds.Center;

	public double SizeScore { get; set; }
}
