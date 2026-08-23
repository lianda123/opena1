using System.Collections.Generic;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace WoodExport.Core
{
  internal enum ExportFormat
  {
    Dxf,
    Dwg,
    Both,
    BomOnly
  }

  internal sealed class ExportSettings
  {
    public double LabelHeightMillimeters { get; set; } = 4.0;
    public double LabelInsetMillimeters { get; set; } = 2.0;
    public double SpacingMillimeters { get; set; } = 4.0;
    public double ThicknessToleranceMillimeters { get; set; } = 0.15;
    public double ShapeToleranceMillimeters { get; set; } = 0.10;
    public double ModelUnitsPerMillimeter { get; set; } = 1.0;

    public double LabelHeight => LabelHeightMillimeters * ModelUnitsPerMillimeter;
    public double LabelInset => LabelInsetMillimeters * ModelUnitsPerMillimeter;
    public double Spacing => SpacingMillimeters * ModelUnitsPerMillimeter;
  }

  internal sealed class ExportCurve
  {
    public Curve Geometry { get; set; }
    public ObjectAttributes Attributes { get; set; }
    public bool IsOutline { get; set; }
  }

  internal sealed class ExportPart
  {
    public int Sequence { get; set; }
    public string Name { get; set; }
    public string PartNumber { get; set; }
    public string ShapeSignature { get; set; }
    public RhinoObject BoardObject { get; set; }
    public List<RhinoObject> SourceObjects { get; } = new List<RhinoObject>();
    public List<ExportCurve> FlatCurves { get; } = new List<ExportCurve>();
    public Plane SourcePlane { get; set; }
    public Transform FlattenTransform { get; set; }
    public BoundingBox FlatBounds { get; set; }
    public double ThicknessModelUnits { get; set; }
    public double ThicknessMillimeters { get; set; }
    public double WidthMillimeters { get; set; }
    public double HeightMillimeters { get; set; }
    public string SourceLayers { get; set; }
  }

  internal sealed class BomRow
  {
    public string PartNumber { get; set; }
    public string Name { get; set; }
    public int Quantity { get; set; }
    public double ThicknessMillimeters { get; set; }
    public double WidthMillimeters { get; set; }
    public double HeightMillimeters { get; set; }
    public double BoundingAreaSquareMillimeters { get; set; }
    public string SourceLayers { get; set; }
  }

  internal sealed class ExportRunResult
  {
    public List<ExportPart> Parts { get; } = new List<ExportPart>();
    public List<BomRow> BomRows { get; } = new List<BomRow>();
    public List<string> CadFiles { get; } = new List<string>();
    public List<string> Warnings { get; } = new List<string>();
    public string BomFile { get; set; }
  }
}
