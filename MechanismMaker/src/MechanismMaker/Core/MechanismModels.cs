using System;
using System.Collections.Generic;
using System.Drawing;
using Rhino.Geometry;

namespace MechanismMaker.Core
{
  internal enum CamKind
  {
    Eccentric,
    Pear,
    Heart,
    Snail
  }

  internal sealed class MechanismSettings
  {
    public double BoardThicknessMm { get; set; } = 2.0;
    public double FixedHoleMm { get; set; } = 1.95;
    public double RotatingHoleMm { get; set; } = 2.20;
    public double GuideHoleMm { get; set; } = 2.30;
    public double DefaultModuleMm { get; set; } = 1.0;
    public double PressureAngleDegrees { get; set; } = 20.0;
    public double BacklashMm { get; set; } = 0.15;
    public double SlotClearanceMm { get; set; } = 0.30;
  }

  internal sealed class GeneratedPart
  {
    public GeneratedPart(string name, string type, Color color)
    {
      Name = name;
      Type = type;
      Color = color;
    }

    public string Name { get; }
    public string Type { get; }
    public Color Color { get; }
    public List<Curve> Curves { get; } = new List<Curve>();
    public Dictionary<string, string> Metadata { get; } = new Dictionary<string, string>();
  }

  internal sealed class MechanismAssembly
  {
    public MechanismAssembly(string type)
    {
      Type = type;
      MechanismId = Guid.NewGuid();
    }

    public Guid MechanismId { get; }
    public string Type { get; }
    public List<GeneratedPart> Parts { get; } = new List<GeneratedPart>();
  }
}
