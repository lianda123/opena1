using System;
using System.Collections.Generic;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace WoodSheetLayout.Core
{
  internal enum SheetKind
  {
    A3,
    A4,
    Custom
  }

  internal enum FlattenKind
  {
    Planar,
    DevelopableMidSurface
  }

  internal enum LayoutPartMode
  {
    PlanarOnly,
    BentOnly
  }

  internal enum IssueSeverity
  {
    Warning,
    Error
  }

  internal sealed class LayoutSettings
  {
    public LayoutPartMode PartMode { get; set; } = LayoutPartMode.PlanarOnly;
    public SheetKind Sheet { get; set; } = SheetKind.A3;
    public double CustomWidthMillimeters { get; set; } = 420.0;
    public double CustomHeightMillimeters { get; set; } = 297.0;
    public double PartGapMillimeters { get; set; } = 4.0;
    public double FrameMarginMillimeters { get; set; } = 4.0;
    public double ThicknessToleranceMillimeters { get; set; } = 0.15;
    public bool Landscape { get; set; } = true;
    public bool GrainDirectionLocked { get; set; }
    public double NeutralFactor { get; set; } = 0.5;
    public double OutlineChordToleranceMillimeters { get; set; } = 0.5;
    public double ModelUnitsPerMillimeter { get; set; } = 1.0;

    public double PartGap => PartGapMillimeters * ModelUnitsPerMillimeter;
    public double FrameMargin => FrameMarginMillimeters * ModelUnitsPerMillimeter;
    public double SheetGap => 20.0 * ModelUnitsPerMillimeter;
    public double OutlineChordTolerance => OutlineChordToleranceMillimeters * ModelUnitsPerMillimeter;

    public string SheetDescription
    {
      get
      {
        if (Sheet != SheetKind.Custom)
          return Sheet.ToString();
        return string.Format("Custom {0:0.##}×{1:0.##}mm", CustomWidthMillimeters, CustomHeightMillimeters);
      }
    }

    public double SheetWidth
    {
      get
      {
        double widthMillimeters;
        double heightMillimeters;
        GetBaseSheetSize(out widthMillimeters, out heightMillimeters);
        return (Landscape ? Math.Max(widthMillimeters, heightMillimeters) : Math.Min(widthMillimeters, heightMillimeters)) *
               ModelUnitsPerMillimeter;
      }
    }

    public double SheetHeight
    {
      get
      {
        double widthMillimeters;
        double heightMillimeters;
        GetBaseSheetSize(out widthMillimeters, out heightMillimeters);
        return (Landscape ? Math.Min(widthMillimeters, heightMillimeters) : Math.Max(widthMillimeters, heightMillimeters)) *
               ModelUnitsPerMillimeter;
      }
    }

    public IEnumerable<double> RotationAnglesRadians()
    {
      yield return 0.0;
      if (GrainDirectionLocked)
        yield break;
      yield return Math.PI * 0.5;
    }

    private void GetBaseSheetSize(out double widthMillimeters, out double heightMillimeters)
    {
      switch (Sheet)
      {
        case SheetKind.A4:
          widthMillimeters = 297.0;
          heightMillimeters = 210.0;
          break;
        case SheetKind.Custom:
          widthMillimeters = Math.Max(1.0, CustomWidthMillimeters);
          heightMillimeters = Math.Max(1.0, CustomHeightMillimeters);
          break;
        default:
          widthMillimeters = 420.0;
          heightMillimeters = 297.0;
          break;
      }
    }
  }

  internal sealed class FlatGeometryItem
  {
    public GeometryBase Geometry { get; set; }
    public ObjectAttributes SourceAttributes { get; set; }
    public Guid SourceObjectId { get; set; }
    public string Name { get; set; }
  }

  internal sealed class PolygonLoop2d
  {
    public List<Point2d> Points { get; } = new List<Point2d>();
    public double SignedArea { get; set; }
    public BoundingBox Bounds { get; set; } = BoundingBox.Unset;
  }

  internal sealed class PartOutline
  {
    public PolygonLoop2d Outer { get; set; }
    public List<PolygonLoop2d> Holes { get; } = new List<PolygonLoop2d>();
    public double NetArea { get; set; }
    public BoundingBox Bounds { get; set; } = BoundingBox.Unset;
  }

  internal sealed class BoardPart
  {
    public int Sequence { get; set; }
    public string Name { get; set; } = "木板";
    public List<RhinoObject> Objects { get; } = new List<RhinoObject>();
    public List<FlatGeometryItem> FlatGeometry { get; } = new List<FlatGeometryItem>();
    public RhinoObject BoardObject { get; set; }
    public Plane SourcePlane { get; set; } = Plane.WorldXY;
    public Transform FlattenTransform { get; set; } = Transform.Identity;
    public FlattenKind FlattenKind { get; set; } = FlattenKind.Planar;
    public PartOutline Outline { get; set; }
    public BoundingBox FlatBounds { get; set; } = BoundingBox.Unset;
    public BoundingBox SourceBounds { get; set; } = BoundingBox.Unset;
    public double ThicknessModelUnits { get; set; }
    public double ThicknessMillimeters { get; set; }
    public bool AnnotationSideCorrected { get; set; }
    public bool TextMirrorCorrected { get; set; }
    public List<string> Notes { get; } = new List<string>();
  }

  internal sealed class PositionedOutline
  {
    public PolygonLoop2d Outer { get; set; }
    public List<PolygonLoop2d> Holes { get; } = new List<PolygonLoop2d>();
    public BoundingBox Bounds { get; set; } = BoundingBox.Unset;
  }

  internal sealed class PartPlacement
  {
    public BoardPart Part { get; set; }
    public double RotationRadians { get; set; }
    public double TranslationX { get; set; }
    public double TranslationY { get; set; }
    public BoundingBox OrientedBounds { get; set; }
    public PositionedOutline PositionedOutline { get; set; }
    public bool NestedInsideHole { get; set; }
  }

  internal sealed class PackedSheet
  {
    public int GlobalIndex { get; set; }
    public int IndexWithinThickness { get; set; }
    public double ThicknessMillimeters { get; set; }
    public Point2d Origin { get; set; }
    public double UsedPartArea { get; set; }
    public List<PartPlacement> Placements { get; } = new List<PartPlacement>();
  }

  internal sealed class LayoutIssue
  {
    public int Number { get; set; }
    public int PartSequence { get; set; }
    public string PartName { get; set; }
    public string Message { get; set; }
    public IssueSeverity Severity { get; set; }
    public BoundingBox SourceBounds { get; set; } = BoundingBox.Unset;
  }

  internal sealed class LayoutResult
  {
    public List<PackedSheet> Sheets { get; } = new List<PackedSheet>();
    public List<BoardPart> OversizedParts { get; } = new List<BoardPart>();
    public List<LayoutIssue> Issues { get; } = new List<LayoutIssue>();
    public List<string> SkippedParts { get; } = new List<string>();
    public List<string> Warnings { get; } = new List<string>();
  }
}
