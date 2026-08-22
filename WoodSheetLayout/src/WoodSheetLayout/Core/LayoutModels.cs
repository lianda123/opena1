using System;
using System.Collections.Generic;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace WoodSheetLayout.Core
{
  internal enum SheetKind
  {
    A3,
    A4
  }

  internal sealed class LayoutSettings
  {
    public SheetKind Sheet { get; set; } = SheetKind.A3;
    public double SpacingMillimeters { get; set; } = 4.0;
    public double ThicknessToleranceMillimeters { get; set; } = 0.15;
    public bool Landscape { get; set; } = true;
    public double ModelUnitsPerMillimeter { get; set; } = 1.0;

    public double Spacing => SpacingMillimeters * ModelUnitsPerMillimeter;
    public double SheetGap => 20.0 * ModelUnitsPerMillimeter;

    public double SheetWidth
    {
      get
      {
        var widthMillimeters = Sheet == SheetKind.A3 ? 420.0 : 297.0;
        var heightMillimeters = Sheet == SheetKind.A3 ? 297.0 : 210.0;
        return (Landscape ? widthMillimeters : heightMillimeters) * ModelUnitsPerMillimeter;
      }
    }

    public double SheetHeight
    {
      get
      {
        var widthMillimeters = Sheet == SheetKind.A3 ? 420.0 : 297.0;
        var heightMillimeters = Sheet == SheetKind.A3 ? 297.0 : 210.0;
        return (Landscape ? heightMillimeters : widthMillimeters) * ModelUnitsPerMillimeter;
      }
    }
  }

  internal sealed class BoardPart
  {
    public int Sequence { get; set; }
    public string Name { get; set; } = "木板";
    public List<RhinoObject> Objects { get; } = new List<RhinoObject>();
    public RhinoObject BoardObject { get; set; }
    public Plane SourcePlane { get; set; } = Plane.WorldXY;
    public Transform FlattenTransform { get; set; } = Transform.Identity;
    public BoundingBox FlatBounds { get; set; } = BoundingBox.Unset;
    public double ThicknessModelUnits { get; set; }
    public double ThicknessMillimeters { get; set; }
  }

  internal sealed class PartPlacement
  {
    public BoardPart Part { get; set; }
    public bool RotatedNinetyDegrees { get; set; }
    public double LocalX { get; set; }
    public double LocalY { get; set; }
    public BoundingBox OrientedBounds { get; set; }
  }

  internal sealed class PackedSheet
  {
    public int GlobalIndex { get; set; }
    public int IndexWithinThickness { get; set; }
    public double ThicknessMillimeters { get; set; }
    public Point2d Origin { get; set; }
    public List<PartPlacement> Placements { get; } = new List<PartPlacement>();
  }

  internal sealed class LayoutResult
  {
    public List<PackedSheet> Sheets { get; } = new List<PackedSheet>();
    public List<BoardPart> OversizedParts { get; } = new List<BoardPart>();
    public List<string> Warnings { get; } = new List<string>();
  }
}
