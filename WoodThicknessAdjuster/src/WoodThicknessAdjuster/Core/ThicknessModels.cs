using Rhino.Geometry;

namespace WoodThicknessAdjuster.Core
{
  internal enum ThicknessAnchorMode
  {
    ClickedFace,
    Center
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
}
