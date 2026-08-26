using Rhino.Geometry;

namespace WoodThicknessAdjuster.Core
{
  internal enum ThicknessAnchorMode
  {
    ClickedFace,
    Center
  }

  internal enum ThicknessContactMode
  {
    AutoFit,
    Off
  }

  internal sealed class ThicknessAnalysis
  {
    public int FirstFaceIndex { get; set; }
    public int SecondFaceIndex { get; set; }
    public int PreferredAnchorFaceIndex { get; set; } = -1;
    public Plane FirstPlane { get; set; }
    public Plane SecondPlane { get; set; }
    public Point3d FirstCentroid { get; set; }
    public Point3d SecondCentroid { get; set; }
    public double ThicknessModelUnits { get; set; }
    public double Score { get; set; }
  }

  internal sealed class ThicknessContact
  {
    public System.Guid NeighborObjectId { get; set; }
    public int TargetFaceIndex { get; set; }
    public Plane TargetPlane { get; set; }
    public Plane NeighborPlane { get; set; }
    public Point3d TargetCentroid { get; set; }
    public double SeparationModelUnits { get; set; }
    public double ContactToleranceModelUnits { get; set; }
    public double OverlapRatio { get; set; }
    public bool IsPreferredNeighbor { get; set; }
    public bool WasExactContact { get; set; }

    public bool NeedsSnap =>
      SeparationModelUnits > ContactToleranceModelUnits;
  }
}
