using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace ExplodeBook.Core
{
  internal static class AssemblyAnalyzer
  {
    public const string GeneratedKey = "ExplodeBook.Generated";
    public const string GeneratedValue = "1";
    public const string OrderKey = "ExplodeBook.Order";
    public const string PartNumberKey = "ExplodeBook.PartNumber";
    public const string WoodExportPartNumberKey = "WoodExport.PartNumber";

    public static AssemblyAnalysis Analyze(
      RhinoDoc doc,
      IEnumerable<RhinoObject> selection,
      ExplodeSettings settings)
    {
      var result = new AssemblyAnalysis();
      if (doc == null || selection == null)
        return result;

      settings.ModelUnitsPerMillimeter = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem);
      if (!IsFinitePositive(settings.ModelUnitsPerMillimeter))
      {
        settings.ModelUnitsPerMillimeter = 1.0;
        result.Warnings.Add("文档单位无效，说明书尺寸暂按毫米计算。建议先把 Rhino 文档单位设为 mm。");
      }

      var objects = selection
        .Where(item => item != null && item.Geometry != null)
        .Where(item => item.Attributes.GetUserString(GeneratedKey) != GeneratedValue)
        .GroupBy(item => item.Id)
        .Select(group => group.First())
        .ToList();
      var components = BuildGroupedComponents(objects);
      var sequence = 0;
      foreach (var component in components)
      {
        var bounds = CombinedBounds(component);
        if (!bounds.IsValid)
        {
          result.Warnings.Add("第 " + (sequence + 1) + " 组边界无效，已跳过。");
          continue;
        }
        sequence++;
        var manualOrder = ReadOrder(component);
        var partNumber = ReadPartNumber(component);
        var name = component.Select(item => item.Attributes.Name)
          .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
        var diagonal = bounds.Diagonal;
        var sizeScore = Math.Max(Math.Abs(diagonal.X * diagonal.Y * diagonal.Z),
          Math.Max(Math.Abs(diagonal.X * diagonal.Y),
            Math.Max(Math.Abs(diagonal.X * diagonal.Z), Math.Abs(diagonal.Y * diagonal.Z))));
        var part = new AssemblyPart
        {
          Sequence = sequence,
          AssemblyOrder = manualOrder,
          PartNumber = string.IsNullOrWhiteSpace(partNumber)
            ? "B-" + sequence.ToString("00", CultureInfo.InvariantCulture)
            : partNumber,
          Name = string.IsNullOrWhiteSpace(name) ? "零件_" + sequence.ToString("00") : name,
          Bounds = bounds,
          SizeScore = sizeScore
        };
        part.Objects.AddRange(component);
        result.Parts.Add(part);
        result.Bounds = result.Bounds.IsValid ? BoundingBox.Union(result.Bounds, bounds) : bounds;
      }

      if (result.Parts.Count == 0)
        return result;
      ApplyOrder(result);
      if (result.Parts.Count > settings.MaximumStepPages)
      {
        result.Warnings.Add(string.Format(
          "识别到 {0} 个装配单元；说明书步骤页只生成前 {1} 页，完整爆炸总览仍包含全部零件。",
          result.Parts.Count,
          settings.MaximumStepPages));
      }
      return result;
    }

    public static List<List<RhinoObject>> BuildGroupedComponents(IEnumerable<RhinoObject> sourceObjects)
    {
      var objects = sourceObjects
        .Where(item => item != null && item.Geometry != null)
        .GroupBy(item => item.Id)
        .Select(group => group.First())
        .ToList();
      var parent = Enumerable.Range(0, objects.Count).ToArray();
      var firstByGroup = new Dictionary<int, int>();
      for (var index = 0; index < objects.Count; index++)
      {
        foreach (var groupIndex in objects[index].Attributes.GetGroupList() ?? new int[0])
        {
          int first;
          if (firstByGroup.TryGetValue(groupIndex, out first))
            Union(parent, index, first);
          else
            firstByGroup[groupIndex] = index;
        }
      }
      return objects
        .Select((item, index) => new { Item = item, Root = Find(parent, index) })
        .GroupBy(item => item.Root)
        .Select(group => group.Select(item => item.Item).ToList())
        .ToList();
    }

    public static int SetManualOrder(RhinoDoc doc, IEnumerable<RhinoObject> orderedSelection)
    {
      if (doc == null || orderedSelection == null)
        return 0;
      var components = BuildGroupedComponents(orderedSelection);
      var order = 0;
      foreach (var component in components)
      {
        order++;
        var existingNumber = ReadPartNumber(component);
        var partNumber = string.IsNullOrWhiteSpace(existingNumber)
          ? "B-" + order.ToString("00", CultureInfo.InvariantCulture)
          : existingNumber;
        foreach (var source in component)
        {
          var attributes = source.Attributes.Duplicate();
          attributes.SetUserString(OrderKey, order.ToString(CultureInfo.InvariantCulture));
          attributes.SetUserString(PartNumberKey, partNumber);
          doc.Objects.ModifyAttributes(source.Id, attributes, true);
        }
      }
      return order;
    }

    public static int ClearManualOrder(RhinoDoc doc, IEnumerable<RhinoObject> selection)
    {
      if (doc == null || selection == null)
        return 0;
      var count = 0;
      foreach (var source in selection.Where(item => item != null).GroupBy(item => item.Id).Select(group => group.First()))
      {
        var attributes = source.Attributes.Duplicate();
        attributes.DeleteUserString(OrderKey);
        if (doc.Objects.ModifyAttributes(source.Id, attributes, true))
          count++;
      }
      return count;
    }

    private static void ApplyOrder(AssemblyAnalysis analysis)
    {
      var manual = analysis.Parts.All(item => item.AssemblyOrder > 0) &&
                   analysis.Parts.Select(item => item.AssemblyOrder).Distinct().Count() == analysis.Parts.Count;
      if (manual)
      {
        analysis.UsedManualOrder = true;
        analysis.Parts.Sort((left, right) => left.AssemblyOrder.CompareTo(right.AssemblyOrder));
        analysis.BasePart = analysis.Parts[0];
        analysis.BasePart.IsBase = true;
        return;
      }

      foreach (var part in analysis.Parts)
        part.AssemblyOrder = 0;
      var remaining = analysis.Parts.ToList();
      var basePart = remaining
        .OrderByDescending(item => item.SizeScore)
        .ThenBy(item => item.Center.DistanceTo(analysis.Bounds.Center))
        .First();
      basePart.IsBase = true;
      basePart.AssemblyOrder = 1;
      analysis.BasePart = basePart;
      remaining.Remove(basePart);
      var installed = new List<AssemblyPart> { basePart };
      var order = 1;
      while (remaining.Count > 0)
      {
        var next = remaining
          .OrderBy(candidate => installed.Min(item => BoxDistance(candidate.Bounds, item.Bounds)))
          .ThenBy(candidate => installed.Min(item => candidate.Center.DistanceTo(item.Center)))
          .ThenByDescending(candidate => candidate.SizeScore)
          .First();
        next.AssemblyOrder = ++order;
        installed.Add(next);
        remaining.Remove(next);
      }
      analysis.Parts.Sort((left, right) => left.AssemblyOrder.CompareTo(right.AssemblyOrder));
    }

    private static int ReadOrder(IEnumerable<RhinoObject> objects)
    {
      foreach (var source in objects)
      {
        int order;
        if (int.TryParse(source.Attributes.GetUserString(OrderKey), out order) && order > 0)
          return order;
      }
      return 0;
    }

    private static string ReadPartNumber(IEnumerable<RhinoObject> objects)
    {
      foreach (var source in objects)
      {
        var number = source.Attributes.GetUserString(WoodExportPartNumberKey);
        if (!string.IsNullOrWhiteSpace(number))
          return number;
        number = source.Attributes.GetUserString(PartNumberKey);
        if (!string.IsNullOrWhiteSpace(number))
          return number;
      }
      return null;
    }

    private static BoundingBox CombinedBounds(IEnumerable<RhinoObject> objects)
    {
      var result = BoundingBox.Unset;
      foreach (var source in objects)
      {
        var bounds = source.Geometry.GetBoundingBox(true);
        if (!bounds.IsValid)
          continue;
        result = result.IsValid ? BoundingBox.Union(result, bounds) : bounds;
      }
      return result;
    }

    private static double BoxDistance(BoundingBox left, BoundingBox right)
    {
      var dx = AxisGap(left.Min.X, left.Max.X, right.Min.X, right.Max.X);
      var dy = AxisGap(left.Min.Y, left.Max.Y, right.Min.Y, right.Max.Y);
      var dz = AxisGap(left.Min.Z, left.Max.Z, right.Min.Z, right.Max.Z);
      return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static double AxisGap(double leftMin, double leftMax, double rightMin, double rightMax)
    {
      if (leftMax < rightMin) return rightMin - leftMax;
      if (rightMax < leftMin) return leftMin - rightMax;
      return 0.0;
    }

    private static bool IsFinitePositive(double value)
    {
      return value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static int Find(int[] parent, int index)
    {
      while (parent[index] != index)
      {
        parent[index] = parent[parent[index]];
        index = parent[index];
      }
      return index;
    }

    private static void Union(int[] parent, int left, int right)
    {
      var leftRoot = Find(parent, left);
      var rightRoot = Find(parent, right);
      if (leftRoot != rightRoot)
        parent[rightRoot] = leftRoot;
    }
  }
}
