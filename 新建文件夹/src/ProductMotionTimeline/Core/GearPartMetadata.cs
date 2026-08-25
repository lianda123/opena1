using System;
using System.Globalization;
using Rhino;
using Rhino.DocObjects;

namespace ProductMotionTimeline.Core
{
  internal enum GearPartType
  {
    Spur = 0,
    Internal = 1,
    Helical = 2,
    Bevel = 3,
    Rack = 4
  }

  internal sealed class GearParameters
  {
    public GearPartType Type { get; set; }
    public int Teeth { get; set; }
    public double Module { get; set; } = 1.0;
    public double PressureAngleDegrees { get; set; } = 20.0;
    public double Thickness { get; set; } = 3.0;
    public double BoreDiameter { get; set; } = 2.0;
    public double HelixAngleDegrees { get; set; } = 15.0;
    public double ConeAngleDegrees { get; set; } = 90.0;
    public double RackLength { get; set; }

    public string DisplayName
    {
      get
      {
        switch (Type)
        {
          case GearPartType.Internal: return "内齿轮";
          case GearPartType.Helical: return "斜齿轮";
          case GearPartType.Bevel: return "锥齿轮";
          case GearPartType.Rack: return "齿条";
          default: return "渐开线直齿轮";
        }
      }
    }
  }

  internal static class GearPartMetadata
  {
    private const string Prefix = "ProductMotionTimeline.Gear.";

    public static void Write(RhinoDoc doc, Guid objectId, GearParameters parameters)
    {
      var obj = doc?.Objects.FindId(objectId);
      if (obj == null || parameters == null)
        return;
      var attributes = obj.Attributes.Duplicate();
      attributes.SetUserString(Prefix + "Type", parameters.Type.ToString());
      attributes.SetUserString(Prefix + "Teeth", parameters.Teeth.ToString(CultureInfo.InvariantCulture));
      attributes.SetUserString(Prefix + "Module", Format(parameters.Module));
      attributes.SetUserString(Prefix + "PressureAngle", Format(parameters.PressureAngleDegrees));
      attributes.SetUserString(Prefix + "Thickness", Format(parameters.Thickness));
      attributes.SetUserString(Prefix + "BoreDiameter", Format(parameters.BoreDiameter));
      attributes.SetUserString(Prefix + "HelixAngle", Format(parameters.HelixAngleDegrees));
      attributes.SetUserString(Prefix + "ConeAngle", Format(parameters.ConeAngleDegrees));
      attributes.SetUserString(Prefix + "RackLength", Format(parameters.RackLength));
      doc.Objects.ModifyAttributes(objectId, attributes, true);
    }

    public static bool TryRead(InstanceObject instance, out GearParameters parameters)
    {
      parameters = null;
      if (instance == null)
        return false;
      var typeText = instance.Attributes.GetUserString(Prefix + "Type");
      GearPartType type;
      if (!Enum.TryParse(typeText, true, out type))
        return false;
      parameters = new GearParameters
      {
        Type = type,
        Teeth = ReadInt(instance, "Teeth", 0),
        Module = ReadDouble(instance, "Module", 1.0),
        PressureAngleDegrees = ReadDouble(instance, "PressureAngle", 20.0),
        Thickness = ReadDouble(instance, "Thickness", 3.0),
        BoreDiameter = ReadDouble(instance, "BoreDiameter", 2.0),
        HelixAngleDegrees = ReadDouble(instance, "HelixAngle", 15.0),
        ConeAngleDegrees = ReadDouble(instance, "ConeAngle", 90.0),
        RackLength = ReadDouble(instance, "RackLength", 0.0)
      };
      return parameters.Type == GearPartType.Rack || parameters.Teeth >= 4;
    }

    public static MechanicalConstraintType InferConstraintType(
      GearParameters driver,
      GearParameters driven)
    {
      if (driven?.Type == GearPartType.Rack)
        return MechanicalConstraintType.RackPinion;
      if (driver?.Type == GearPartType.Bevel && driven?.Type == GearPartType.Bevel)
        return MechanicalConstraintType.BevelGear;
      if (driver?.Type == GearPartType.Helical || driven?.Type == GearPartType.Helical)
        return MechanicalConstraintType.HelicalGear;
      if (driver?.Type == GearPartType.Internal || driven?.Type == GearPartType.Internal)
        return MechanicalConstraintType.InternalGear;
      return MechanicalConstraintType.ExternalGear;
    }

    private static string Format(double value)
    {
      return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static double ReadDouble(InstanceObject instance, string key, double fallback)
    {
      double value;
      return double.TryParse(
        instance.Attributes.GetUserString(Prefix + key),
        NumberStyles.Float,
        CultureInfo.InvariantCulture,
        out value)
        ? value
        : fallback;
    }

    private static int ReadInt(InstanceObject instance, string key, int fallback)
    {
      int value;
      return int.TryParse(
        instance.Attributes.GetUserString(Prefix + key),
        NumberStyles.Integer,
        CultureInfo.InvariantCulture,
        out value)
        ? value
        : fallback;
    }
  }
}
