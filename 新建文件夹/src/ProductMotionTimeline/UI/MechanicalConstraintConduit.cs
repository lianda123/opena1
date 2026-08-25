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

        var start = TimelineEngine.PivotOrigin(driver);
        var end = TimelineEngine.PivotOrigin(driven);
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
          string.Format("{0} → {1}  {2:0.###}", driver.Name, driven.Name, constraint.SignedRatio),
          color,
          Color.Black);
      }
    }
  }
}
