using System;
using System.Drawing;
using ProductMotionTimeline.Core;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

namespace ProductMotionTimeline.UI
{
  internal sealed class MechanicalConstraintConduit : DisplayConduit
  {
    protected override void DrawForeground(DrawEventArgs e)
    {
      var doc = RhinoDoc.ActiveDoc;
      var model = TimelineEngine.Model(doc);
      if (doc == null || model == null)
        return;

      foreach (var constraint in model.Constraints)
      {
        if (!constraint.Enabled)
          continue;
        var driver = model.FindTrack(constraint.DriverTrackId);
        var driven = model.FindTrack(constraint.DrivenTrackId);
        if (driver == null || driven == null)
          continue;

        var start = TimelineEngine.DisplayedPivotOrigin(doc, driver);
        var end = TimelineEngine.DisplayedPivotOrigin(doc, driven);
        var validation = TimelineEngine.ValidateMechanicalConstraint(doc, constraint);
        var color = validation.Severity == ValidationSeverity.Error
          ? Color.FromArgb(230, 78, 73)
          : validation.Severity == ValidationSeverity.Warning
            ? Color.FromArgb(255, 174, 54)
            : Color.FromArgb(70, 190, 126);
        e.Display.DrawLine(new Line(start, end), color, 3);
        e.Display.DrawPoint(start, PointStyle.RoundSimple, 6, Color.FromArgb(64, 205, 255));
        e.Display.DrawPoint(end, PointStyle.RoundSimple, 6, color);
        var midpoint = (start + end) * 0.5;
        e.Display.DrawDot(
          midpoint,
          string.Format(
            "{0}  {1} → {2}  {3}T:{4}T  比例 {5:0.###}",
            TypeName(constraint.Type),
            driver.Name,
            driven.Name,
            constraint.DriverTeeth,
            constraint.DrivenTeeth,
            constraint.SignedRatio),
          color,
          Color.Black);
      }
    }

    private static string TypeName(MechanicalConstraintType type)
    {
      switch (type)
      {
        case MechanicalConstraintType.InternalGear: return "内啮合";
        case MechanicalConstraintType.Belt: return "皮带";
        case MechanicalConstraintType.HelicalGear: return "斜齿";
        case MechanicalConstraintType.BevelGear: return "锥齿";
        case MechanicalConstraintType.RackPinion: return "齿轮-齿条";
        case MechanicalConstraintType.SameShaft: return "同轴";
        case MechanicalConstraintType.PlanetaryCarrier: return "行星架";
        case MechanicalConstraintType.PlanetaryPlanetExternalInput: return "行星轮(太阳输入)";
        case MechanicalConstraintType.PlanetaryPlanetInternalInput: return "行星轮(齿圈输入)";
        case MechanicalConstraintType.PlanetaryRingFixedCarrier: return "齿圈(行星架固定)";
        default: return "外啮合";
      }
    }
  }
}
