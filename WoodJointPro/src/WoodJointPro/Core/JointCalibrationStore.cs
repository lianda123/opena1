using System;
using System.Globalization;
using Rhino;

namespace WoodJointPro.Core
{
  internal static class JointCalibrationStore
  {
    private const string DocumentKey = "WoodJointPro.Calibration.v1";

    internal static JointCalibration Load(RhinoDoc doc)
    {
      var result = new JointCalibration();
      if (doc == null)
        return result;
      var value = doc.Strings.GetValue(DocumentKey);
      if (string.IsNullOrWhiteSpace(value))
        return result;
      var parts = value.Split('|');
      if (parts.Length != 6)
        return result;
      double parsed;
      if (Try(parts[0], out parsed)) result.TightClearanceMillimeters = parsed;
      if (Try(parts[1], out parsed)) result.NormalClearanceMillimeters = parsed;
      if (Try(parts[2], out parsed)) result.LooseClearanceMillimeters = parsed;
      if (Try(parts[3], out parsed)) result.FixedHoleMillimeters = parsed;
      if (Try(parts[4], out parsed)) result.RotatingHoleMillimeters = parsed;
      if (Try(parts[5], out parsed)) result.GuideHoleMillimeters = parsed;
      return result;
    }

    internal static void Save(RhinoDoc doc, JointCalibration calibration)
    {
      if (doc == null || calibration == null)
        return;
      doc.Strings.SetString(DocumentKey, string.Join("|", new[]
      {
        Format(calibration.TightClearanceMillimeters),
        Format(calibration.NormalClearanceMillimeters),
        Format(calibration.LooseClearanceMillimeters),
        Format(calibration.FixedHoleMillimeters),
        Format(calibration.RotatingHoleMillimeters),
        Format(calibration.GuideHoleMillimeters)
      }));
    }

    private static bool Try(string value, out double result)
    {
      return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    private static string Format(double value)
    {
      return value.ToString("0.###", CultureInfo.InvariantCulture);
    }
  }
}
