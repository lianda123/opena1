using System;
using System.Collections.Generic;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace WoodCheck.Core
{
  internal enum CheckSeverity
  {
    Info,
    Warning,
    Error
  }

  internal enum CheckKind
  {
    InvalidGeometry,
    Collision,
    ShallowSlot,
    AxisMisalignment,
    WeakWall,
    DuplicateCurve,
    OpenCurve
  }

  internal sealed class CheckSettings
  {
    public double NominalBoardThicknessMm { get; set; } = 2.0;
    public double ShaftDiameterMm { get; set; } = 2.0;
    public double CollisionVolumeMm3 { get; set; } = 0.01;
    public double MinimumSlotDepthMm { get; set; } = 4.0;
    public double AxisToleranceMm { get; set; } = 0.15;
    public double AxisSearchRadiusMm { get; set; } = 3.0;
    public double MaximumAxisSpanMm { get; set; } = 100.0;
    public double MinimumWallMm { get; set; } = 1.0;
    public double MinimumFeatureMm { get; set; } = 0.5;
    public double OpenCurveGapMm { get; set; } = 0.2;
    public bool MarkIssues { get; set; } = true;
  }

  internal sealed class CheckIssue
  {
    public CheckKind Kind { get; set; }
    public CheckSeverity Severity { get; set; }
    public string Code { get; set; }
    public string Title { get; set; }
    public string Message { get; set; }
    public Point3d Location { get; set; }
    public List<Guid> SourceIds { get; } = new List<Guid>();
  }

  internal sealed class CheckReport
  {
    public List<CheckIssue> Issues { get; } = new List<CheckIssue>();

    public int ErrorCount
    {
      get { return Issues.FindAll(item => item.Severity == CheckSeverity.Error).Count; }
    }

    public int WarningCount
    {
      get { return Issues.FindAll(item => item.Severity == CheckSeverity.Warning).Count; }
    }

    public int InfoCount
    {
      get { return Issues.FindAll(item => item.Severity == CheckSeverity.Info).Count; }
    }
  }

  internal sealed class BoardInfo
  {
    public RhinoObject Source { get; set; }
    public Brep Brep { get; set; }
    public Plane Plane { get; set; }
    public double Thickness { get; set; }
    public Curve OuterCurve { get; set; }
    public BoundingBox Bounds { get; set; }
    public List<HoleInfo> Holes { get; } = new List<HoleInfo>();
  }

  internal sealed class HoleInfo
  {
    public RhinoObject Source { get; set; }
    public Point3d Center { get; set; }
    public Vector3d Axis { get; set; }
    public double Radius { get; set; }
    public Curve Boundary { get; set; }
  }
}
