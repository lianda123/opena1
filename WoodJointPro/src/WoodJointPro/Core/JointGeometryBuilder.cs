using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace WoodJointPro.Core
{
  internal static class JointGeometryBuilder
  {
    internal static bool TryCreateAutomaticFrame(
      BoardInfo first,
      BoardInfo second,
      Point3d firstPick,
      Point3d secondPick,
      JointSettings settings,
      double tolerance,
      out JointFrame frame,
      out string error)
    {
      frame = null;
      error = null;
      if (first == null || second == null)
      {
        error = "木板分析结果无效";
        return false;
      }

      var alignment = Math.Abs(Vector3d.Multiply(
        first.MidPlane.Normal,
        second.MidPlane.Normal));
      if (alignment > Math.Cos(RhinoMath.ToRadians(8.0)))
      {
        error = "两块木板主表面近似平行，当前榫槽需要相交或成角度的两块板";
        return false;
      }

      Line intersection;
      if (!Intersection.PlanePlane(first.MidPlane, second.MidPlane, out intersection))
      {
        error = "无法计算两块木板的主平面交线";
        return false;
      }
      var along = intersection.Direction;
      if (!along.Unitize())
      {
        error = "两块木板的交线方向无效";
        return false;
      }

      var requested = AverageValid(firstPick, secondPick, (first.Centroid + second.Centroid) * 0.5);
      var center = ProjectToLine(requested, intersection.From, along);
      double firstMin;
      double firstMax;
      double secondMin;
      double secondMax;
      ProjectRange(first.Bounds, intersection.From, along, out firstMin, out firstMax);
      ProjectRange(second.Bounds, intersection.From, along, out secondMin, out secondMax);
      var overlapMin = Math.Max(firstMin, secondMin);
      var overlapMax = Math.Min(firstMax, secondMax);
      var centerParameter = Vector3d.Multiply(center - intersection.From, along);
      if (overlapMax > overlapMin + tolerance)
      {
        centerParameter = Clamp(centerParameter, overlapMin, overlapMax);
        center = intersection.From + along * centerParameter;
      }

      var requestedLength = Math.Max(
        settings.JointLengthMillimeters * settings.ModelUnitsPerMillimeter,
        settings.ModelUnitsPerMillimeter * 0.5);
      var availableLength = overlapMax > overlapMin
        ? overlapMax - overlapMin
        : Math.Min(first.Bounds.Diagonal.Length, second.Bounds.Diagonal.Length);
      frame = new JointFrame
      {
        Center = center,
        Along = along,
        Length = Math.Max(settings.ModelUnitsPerMillimeter * 0.5,
          Math.Min(requestedLength, Math.Max(availableLength, settings.ModelUnitsPerMillimeter * 0.5)))
      };
      return true;
    }

    internal static bool TryAlignPickedFrame(
      BoardInfo first,
      BoardInfo second,
      JointFrame picked,
      double modelUnitsPerMillimeter,
      out JointFrame aligned,
      out string error)
    {
      aligned = null;
      error = null;
      if (first == null || second == null || picked == null)
      {
        error = "卡口长度线无效";
        return false;
      }
      Line intersection;
      if (!Intersection.PlanePlane(first.MidPlane, second.MidPlane, out intersection))
      {
        error = "两块木板主表面平行，无法把卡口长度线定位到共同边";
        return false;
      }
      var along = intersection.Direction;
      var pickedAlong = picked.Along;
      if (!along.Unitize() || !pickedAlong.Unitize())
      {
        error = "卡口长度方向无效";
        return false;
      }
      if (Vector3d.Multiply(along, pickedAlong) < 0.0)
        along = -along;
      var center = ProjectToLine(picked.Center, intersection.From, along);
      var maximumOffset = Math.Max(
        modelUnitsPerMillimeter * 5.0,
        Math.Max(first.Thickness, second.Thickness) * 2.0);
      if (center.DistanceTo(picked.Center) > maximumOffset)
      {
        error = "选择的长度线离两块木板共同边过远，请在实际接合位置重新取点";
        return false;
      }
      aligned = new JointFrame
      {
        Center = center,
        Along = along,
        Length = picked.Length
      };
      return true;
    }

    internal static bool TryBuild(
      BoardInfo first,
      BoardInfo second,
      JointFrame frame,
      JointSettings settings,
      JointCalibration calibration,
      double tolerance,
      out JointBuildResult result,
      out string error)
    {
      result = null;
      error = null;
      if (first == null || second == null || frame == null ||
          first.Object == null || second.Object == null || first.Object.Id == second.Object.Id)
      {
        error = "请选择两块不同的有效木板";
        return false;
      }
      var along = frame.Along;
      if (!along.Unitize() || !frame.Center.IsValid || frame.Length <= tolerance)
      {
        error = "榫槽方向或长度无效";
        return false;
      }

      var firstThickness = EffectiveThickness(first, settings);
      var secondThickness = EffectiveThickness(second, settings);
      var clearance = settings.ClearanceMillimeters(calibration) * settings.ModelUnitsPerMillimeter;
      var firstOpening = firstThickness + clearance;
      var secondOpening = secondThickness + clearance;
      if (firstOpening <= tolerance || secondOpening <= tolerance)
      {
        error = "公差设置使槽宽小于模型公差，请调整紧配值";
        return false;
      }

      var firstCutters = new List<Brep>();
      var secondCutters = new List<Brep>();
      if (settings.Kind == JointKind.CrossSlot)
      {
        Add(firstCutters, CreateCrossSlotCutter(first, frame.Center, along, secondOpening, tolerance));
        Add(secondCutters, CreateCrossSlotCutter(second, frame.Center, along, firstOpening, tolerance));
      }
      else if (settings.Kind == JointKind.TSlot)
      {
        Add(firstCutters, CreateThroughSlot(first, frame, secondOpening, tolerance));
      }
      else if (settings.Kind == JointKind.TabSlot || settings.Kind == JointKind.Snap)
      {
        Add(firstCutters, CreateThroughSlot(first, frame, secondOpening, tolerance));
        AddTabShoulderCutters(
          second,
          frame,
          firstThickness + Math.Max(clearance, 0.0),
          tolerance,
          secondCutters);
        if (settings.Kind == JointKind.Snap)
        {
          AddSnapReliefCutters(second, frame, settings, firstThickness, tolerance, secondCutters);
          AddSnapPocketCutters(first, frame, settings, secondOpening, tolerance, firstCutters);
        }
      }
      else if (settings.Kind == JointKind.Finger)
      {
        AddFingerCutters(
          first,
          second,
          frame,
          settings,
          firstOpening,
          secondOpening,
          tolerance,
          firstCutters,
          secondCutters);
      }

      if (firstCutters.Count == 0 && secondCutters.Count == 0)
      {
        error = "没有生成有效的榫槽切割体";
        return false;
      }

      Brep firstResult;
      Brep secondResult;
      if (!TryBooleanDifference(first.Brep, firstCutters, tolerance, out firstResult))
      {
        error = "第一块木板的布尔开槽失败，原件未修改";
        return false;
      }
      if (!TryBooleanDifference(second.Brep, secondCutters, tolerance, out secondResult))
      {
        error = "第二块木板的布尔开槽失败，原件未修改";
        return false;
      }

      result = new JointBuildResult
      {
        First = new BoardEdit { Board = first, Geometry = firstResult },
        Second = new BoardEdit { Board = second, Geometry = secondResult },
        Frame = frame,
        Description = Description(settings.Kind)
      };
      result.First.Cutters.AddRange(firstCutters);
      result.Second.Cutters.AddRange(secondCutters);
      if (settings.MaterialThicknessMillimeters > 0.0)
      {
        var measuredFirst = first.Thickness / settings.ModelUnitsPerMillimeter;
        var measuredSecond = second.Thickness / settings.ModelUnitsPerMillimeter;
        if (Math.Abs(measuredFirst - settings.MaterialThicknessMillimeters) > 0.15 ||
            Math.Abs(measuredSecond - settings.MaterialThicknessMillimeters) > 0.15)
          result.Warnings.Add("所选预设板厚与至少一块木板的实测厚度相差超过0.15mm");
      }
      return true;
    }

    private static Brep CreateCrossSlotCutter(
      BoardInfo target,
      Point3d center,
      Vector3d along,
      double openingWidth,
      double tolerance)
    {
      var plane = BoardCutPlane(target, center, along);
      if (!plane.IsValid)
        return null;
      double minimum;
      double maximum;
      ProjectRange(target.Bounds, center, plane.XAxis, out minimum, out maximum);
      var edge = Math.Abs(minimum) <= Math.Abs(maximum) ? minimum : maximum;
      var extra = Math.Max(tolerance * 5.0, openingWidth * 0.05);
      var x = edge < 0.0
        ? new Interval(edge - extra, extra)
        : new Interval(-extra, edge + extra);
      return CreateBox(
        plane,
        x,
        new Interval(-openingWidth * 0.5, openingWidth * 0.5),
        ThroughThickness(target, tolerance));
    }

    private static Brep CreateThroughSlot(
      BoardInfo target,
      JointFrame frame,
      double openingWidth,
      double tolerance)
    {
      var plane = BoardCutPlane(target, frame.Center, frame.Along);
      return CreateBox(
        plane,
        new Interval(-frame.Length * 0.5, frame.Length * 0.5),
        new Interval(-openingWidth * 0.5, openingWidth * 0.5),
        ThroughThickness(target, tolerance));
    }

    private static void AddTabShoulderCutters(
      BoardInfo target,
      JointFrame frame,
      double tabDepth,
      double tolerance,
      IList<Brep> cutters)
    {
      var plane = BoardCutPlane(target, frame.Center, frame.Along);
      if (!plane.IsValid)
        return;
      double xMinimum;
      double xMaximum;
      double yMinimum;
      double yMaximum;
      ProjectRange(target.Bounds, frame.Center, plane.XAxis, out xMinimum, out xMaximum);
      ProjectRange(target.Bounds, frame.Center, plane.YAxis, out yMinimum, out yMaximum);
      var y = EdgeDepthInterval(yMinimum, yMaximum, tabDepth, tolerance);
      var half = frame.Length * 0.5;
      if (xMinimum < -half - tolerance)
        Add(cutters, CreateBox(plane, new Interval(xMinimum - tolerance * 4.0, -half), y, ThroughThickness(target, tolerance)));
      if (xMaximum > half + tolerance)
        Add(cutters, CreateBox(plane, new Interval(half, xMaximum + tolerance * 4.0), y, ThroughThickness(target, tolerance)));
    }

    private static void AddSnapReliefCutters(
      BoardInfo target,
      JointFrame frame,
      JointSettings settings,
      double receiverThickness,
      double tolerance,
      IList<Brep> cutters)
    {
      var plane = BoardCutPlane(target, frame.Center, frame.Along);
      if (!plane.IsValid)
        return;
      double yMinimum;
      double yMaximum;
      ProjectRange(target.Bounds, frame.Center, plane.YAxis, out yMinimum, out yMaximum);
      var flexLength = Math.Max(receiverThickness * 3.0, settings.ModelUnitsPerMillimeter * 6.0);
      var y = EdgeDepthInterval(yMinimum, yMaximum, flexLength, tolerance);
      var relief = Math.Max(
        settings.SnapReliefMillimeters * settings.ModelUnitsPerMillimeter,
        tolerance * 3.0);
      var half = frame.Length * 0.5;
      Add(cutters, CreateBox(plane,
        new Interval(-half - relief * 0.5, -half + relief * 0.5),
        y,
        ThroughThickness(target, tolerance)));
      Add(cutters, CreateBox(plane,
        new Interval(half - relief * 0.5, half + relief * 0.5),
        y,
        ThroughThickness(target, tolerance)));
    }

    private static void AddSnapPocketCutters(
      BoardInfo target,
      JointFrame frame,
      JointSettings settings,
      double openingWidth,
      double tolerance,
      IList<Brep> cutters)
    {
      var plane = BoardCutPlane(target, frame.Center, frame.Along);
      if (!plane.IsValid)
        return;
      var pocket = Math.Max(settings.ModelUnitsPerMillimeter * 0.5, tolerance * 4.0);
      var half = frame.Length * 0.5;
      Add(cutters, CreateBox(plane,
        new Interval(-half - pocket, -half + pocket),
        new Interval(-openingWidth * 0.5 - pocket, openingWidth * 0.5 + pocket),
        ThroughThickness(target, tolerance)));
      Add(cutters, CreateBox(plane,
        new Interval(half - pocket, half + pocket),
        new Interval(-openingWidth * 0.5 - pocket, openingWidth * 0.5 + pocket),
        ThroughThickness(target, tolerance)));
    }

    private static void AddFingerCutters(
      BoardInfo first,
      BoardInfo second,
      JointFrame frame,
      JointSettings settings,
      double firstOpening,
      double secondOpening,
      double tolerance,
      IList<Brep> firstCutters,
      IList<Brep> secondCutters)
    {
      var desired = Math.Max(
        settings.FingerWidthMillimeters * settings.ModelUnitsPerMillimeter,
        settings.ModelUnitsPerMillimeter);
      var count = (int)Math.Floor(frame.Length / desired);
      count = Math.Max(3, Math.Min(31, count));
      if (count % 2 == 0)
        count--;
      var width = frame.Length / count;
      for (var index = 0; index < count; index++)
      {
        var start = -frame.Length * 0.5 + index * width;
        var end = start + width;
        if (index % 2 == 0)
          Add(firstCutters, CreateFingerEdgeCutter(first, frame, start, end, secondOpening, tolerance));
        else
          Add(secondCutters, CreateFingerEdgeCutter(second, frame, start, end, firstOpening, tolerance));
      }
    }

    private static Brep CreateFingerEdgeCutter(
      BoardInfo target,
      JointFrame frame,
      double start,
      double end,
      double depth,
      double tolerance)
    {
      var plane = BoardCutPlane(target, frame.Center, frame.Along);
      if (!plane.IsValid)
        return null;
      double minimum;
      double maximum;
      ProjectRange(target.Bounds, frame.Center, plane.YAxis, out minimum, out maximum);
      var y = EdgeDepthInterval(minimum, maximum, depth, tolerance);
      return CreateBox(
        plane,
        new Interval(start - tolerance, end + tolerance),
        y,
        ThroughThickness(target, tolerance));
    }

    private static Plane BoardCutPlane(BoardInfo board, Point3d center, Vector3d along)
    {
      var normal = board.MidPlane.Normal;
      if (!normal.Unitize())
        return Plane.Unset;
      var x = along - normal * Vector3d.Multiply(along, normal);
      if (!x.Unitize())
        x = board.MidPlane.XAxis;
      var y = Vector3d.CrossProduct(normal, x);
      if (!y.Unitize())
        return Plane.Unset;
      return new Plane(center, x, y);
    }

    private static Interval ThroughThickness(BoardInfo board, double tolerance)
    {
      var extra = Math.Max(tolerance * 5.0, board.Thickness * 0.05);
      return new Interval(-board.Thickness * 0.5 - extra, board.Thickness * 0.5 + extra);
    }

    private static Interval EdgeDepthInterval(
      double minimum,
      double maximum,
      double requestedDepth,
      double tolerance)
    {
      var useMinimumEdge = Math.Abs(minimum) <= Math.Abs(maximum);
      var edge = useMinimumEdge ? minimum : maximum;
      var available = Math.Max(0.0, maximum - minimum);
      var depth = Math.Min(available, Math.Max(requestedDepth, tolerance * 5.0));
      var extra = tolerance * 4.0;
      return useMinimumEdge
        ? new Interval(edge - extra, edge + depth)
        : new Interval(edge - depth, edge + extra);
    }

    private static Brep CreateBox(Plane plane, Interval x, Interval y, Interval z)
    {
      if (!plane.IsValid || x.Length <= 0.0 || y.Length <= 0.0 || z.Length <= 0.0)
        return null;
      var box = new Box(plane, x, y, z);
      return box.IsValid ? box.ToBrep() : null;
    }

    private static bool TryBooleanDifference(
      Brep source,
      IList<Brep> cutters,
      double tolerance,
      out Brep result)
    {
      result = source == null ? null : source.DuplicateBrep();
      if (result == null || !result.IsValid)
        return false;
      if (cutters == null || cutters.Count == 0)
        return true;
      Brep[] difference;
      try
      {
        difference = Brep.CreateBooleanDifference(
          new[] { result },
          cutters.Where(item => item != null && item.IsValid).ToArray(),
          tolerance);
      }
      catch
      {
        difference = null;
      }
      var best = (difference ?? new Brep[0])
        .Where(item => item != null && item.IsValid && item.IsSolid)
        .OrderByDescending(Volume)
        .FirstOrDefault();
      if (best == null)
        return false;
      result = best;
      return true;
    }

    private static double Volume(Brep brep)
    {
      var properties = brep == null ? null : VolumeMassProperties.Compute(brep);
      return properties == null ? 0.0 : properties.Volume;
    }

    private static double EffectiveThickness(BoardInfo board, JointSettings settings)
    {
      return settings.MaterialThicknessMillimeters > 0.0
        ? settings.MaterialThicknessMillimeters * settings.ModelUnitsPerMillimeter
        : board.Thickness;
    }

    private static void Add(ICollection<Brep> target, Brep brep)
    {
      if (target != null && brep != null && brep.IsValid)
        target.Add(brep);
    }

    private static string Description(JointKind kind)
    {
      if (kind == JointKind.CrossSlot)
        return "十字插槽";
      if (kind == JointKind.TSlot)
        return "T形槽";
      if (kind == JointKind.TabSlot)
        return "插片榫";
      if (kind == JointKind.Snap)
        return "简单卡扣";
      return "指接榫";
    }

    private static Point3d AverageValid(Point3d first, Point3d second, Point3d fallback)
    {
      if (first.IsValid && second.IsValid)
        return (first + second) * 0.5;
      if (first.IsValid)
        return first;
      return second.IsValid ? second : fallback;
    }

    private static Point3d ProjectToLine(Point3d point, Point3d origin, Vector3d direction)
    {
      return origin + direction * Vector3d.Multiply(point - origin, direction);
    }

    internal static void ProjectRange(
      BoundingBox bounds,
      Point3d origin,
      Vector3d direction,
      out double minimum,
      out double maximum)
    {
      minimum = double.MaxValue;
      maximum = double.MinValue;
      foreach (var corner in bounds.GetCorners())
      {
        var value = Vector3d.Multiply(corner - origin, direction);
        minimum = Math.Min(minimum, value);
        maximum = Math.Max(maximum, value);
      }
    }

    private static double Clamp(double value, double minimum, double maximum)
    {
      return Math.Max(minimum, Math.Min(maximum, value));
    }
  }
}
