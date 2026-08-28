using System;
using System.Collections.Generic;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace WoodJointPro.Core
{
  internal enum JointKind
  {
    CrossSlot,
    TSlot,
    TabSlot,
    Snap,
    Finger
  }

  internal enum FitClass
  {
    Tight,
    Normal,
    Loose
  }

  internal enum AxisHoleKind
  {
    Fixed,
    Rotating,
    Guide
  }

  internal sealed class JointSettings
  {
    public JointKind Kind { get; set; } = JointKind.CrossSlot;
    public FitClass Fit { get; set; } = FitClass.Normal;
    public double MaterialThicknessMillimeters { get; set; }
    public double CustomClearanceMillimeters { get; set; } = double.NaN;
    public double JointLengthMillimeters { get; set; } = 10.0;
    public double FingerWidthMillimeters { get; set; } = 4.0;
    public double SnapReliefMillimeters { get; set; } = 0.8;
    public double ModelUnitsPerMillimeter { get; set; } = 1.0;

    public double ClearanceMillimeters(JointCalibration calibration)
    {
      if (!double.IsNaN(CustomClearanceMillimeters))
        return CustomClearanceMillimeters;
      if (Fit == FitClass.Tight)
        return calibration.TightClearanceMillimeters;
      if (Fit == FitClass.Loose)
        return calibration.LooseClearanceMillimeters;
      return calibration.NormalClearanceMillimeters;
    }
  }

  internal sealed class JointCalibration
  {
    public double TightClearanceMillimeters { get; set; } = -0.05;
    public double NormalClearanceMillimeters { get; set; } = 0.10;
    public double LooseClearanceMillimeters { get; set; } = 0.20;
    public double FixedHoleMillimeters { get; set; } = 1.95;
    public double RotatingHoleMillimeters { get; set; } = 2.10;
    public double GuideHoleMillimeters { get; set; } = 2.30;

    public double HoleDiameter(AxisHoleKind kind)
    {
      if (kind == AxisHoleKind.Fixed)
        return FixedHoleMillimeters;
      return kind == AxisHoleKind.Guide
        ? GuideHoleMillimeters
        : RotatingHoleMillimeters;
    }
  }

  internal sealed class BoardInfo
  {
    public RhinoObject Object { get; set; }
    public Brep Brep { get; set; }
    public int FirstFaceIndex { get; set; }
    public int SecondFaceIndex { get; set; }
    public Plane FirstPlane { get; set; }
    public Plane SecondPlane { get; set; }
    public Plane MidPlane { get; set; }
    public Point3d Centroid { get; set; }
    public double Thickness { get; set; }
    public double Score { get; set; }
    public BoundingBox Bounds { get; set; }
  }

  internal sealed class JointFrame
  {
    public Point3d Center { get; set; }
    public Vector3d Along { get; set; }
    public double Length { get; set; }
  }

  internal sealed class BoardEdit
  {
    public BoardInfo Board { get; set; }
    public Brep Geometry { get; set; }
    public List<Brep> Cutters { get; } = new List<Brep>();
  }

  internal sealed class JointBuildResult
  {
    public BoardEdit First { get; set; }
    public BoardEdit Second { get; set; }
    public JointFrame Frame { get; set; }
    public string Description { get; set; }
    public List<string> Warnings { get; } = new List<string>();
  }

  internal sealed class FlatBoardLink
  {
    public Guid ObjectId { get; set; }
    public Transform SourceToFlat { get; set; }
  }
}
