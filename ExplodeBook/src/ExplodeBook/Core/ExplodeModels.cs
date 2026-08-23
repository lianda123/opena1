using System.Collections.Generic;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace ExplodeBook.Core
{
  internal enum ExplodeMode
  {
    Radial,
    XAxis,
    YAxis,
    ZAxis
  }

  internal enum ManualPageKind
  {
    A4,
    A3
  }

  internal sealed class ExplodeSettings
  {
    public ExplodeMode Mode { get; set; } = ExplodeMode.Radial;
    public ManualPageKind PageKind { get; set; } = ManualPageKind.A4;
    public bool Landscape { get; set; } = true;
    public double ExplodeDistanceMillimeters { get; set; } = 25.0;
    public double ArrowHeadMillimeters { get; set; } = 4.0;
    public double PageGapMillimeters { get; set; } = 25.0;
    public int MaximumStepPages { get; set; } = 40;
    public double ModelUnitsPerMillimeter { get; set; } = 1.0;

    public double ExplodeDistance => ExplodeDistanceMillimeters * ModelUnitsPerMillimeter;
    public double ArrowHead => ArrowHeadMillimeters * ModelUnitsPerMillimeter;
    public double PageGap => PageGapMillimeters * ModelUnitsPerMillimeter;

    public double PageWidthMillimeters
    {
      get
      {
        var width = PageKind == ManualPageKind.A3 ? 420.0 : 297.0;
        var height = PageKind == ManualPageKind.A3 ? 297.0 : 210.0;
        return Landscape ? width : height;
      }
    }

    public double PageHeightMillimeters
    {
      get
      {
        var width = PageKind == ManualPageKind.A3 ? 420.0 : 297.0;
        var height = PageKind == ManualPageKind.A3 ? 297.0 : 210.0;
        return Landscape ? height : width;
      }
    }

    public double PageWidth => PageWidthMillimeters * ModelUnitsPerMillimeter;
    public double PageHeight => PageHeightMillimeters * ModelUnitsPerMillimeter;
  }

  internal sealed class AssemblyPart
  {
    public int Sequence { get; set; }
    public int AssemblyOrder { get; set; }
    public string PartNumber { get; set; }
    public string Name { get; set; }
    public bool IsBase { get; set; }
    public List<RhinoObject> Objects { get; } = new List<RhinoObject>();
    public BoundingBox Bounds { get; set; } = BoundingBox.Unset;
    public Point3d Center => Bounds.Center;
    public double SizeScore { get; set; }
  }

  internal sealed class AssemblyAnalysis
  {
    public List<AssemblyPart> Parts { get; } = new List<AssemblyPart>();
    public List<string> Warnings { get; } = new List<string>();
    public BoundingBox Bounds { get; set; } = BoundingBox.Unset;
    public AssemblyPart BasePart { get; set; }
    public bool UsedManualOrder { get; set; }
  }

  internal sealed class GeneratedBook
  {
    public List<System.Guid> GeneratedObjectIds { get; } = new List<System.Guid>();
    public List<string> LayoutNames { get; } = new List<string>();
    public int PartCount { get; set; }
    public int StepCount { get; set; }
  }

  internal sealed class PageZone
  {
    public int PageIndex { get; set; }
    public string LayoutName { get; set; }
    public string Title { get; set; }
    public BoundingBox Bounds { get; set; }
  }
}
