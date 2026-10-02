using System;
using System.Collections.Generic;
using Rhino.Geometry;

namespace ExplodeBook.Core;

internal sealed class AssemblyAnalysis
{
    public bool PlanVerified { get; set; }
    public string Signature { get; set; }
    public long PairTests, CacheHits, MeshBuilds, MeshHits, AnalysisMilliseconds;
    public List<string> ValidationErrors { get; } = new List<string>();
    public Dictionary<AssemblyPart,List<AssemblyPart>> Blockers { get; } = new Dictionary<AssemblyPart,List<AssemblyPart>>();
	public List<AssemblyPart> Parts { get; } = new List<AssemblyPart>();

	public List<AssemblyModule> Modules { get; } = new List<AssemblyModule>();

	public List<string> Warnings { get; } = new List<string>();

	public Dictionary<string, int> QuantityByPartNumber { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	public BoundingBox Bounds { get; set; } = BoundingBox.Unset;

	public AssemblyPart BasePart { get; set; }

	public bool UsedManualOrder { get; set; }

	public bool UsedManualModuleOrder { get; set; }

	public bool UsedManualModulePartOrder { get; set; }
}
