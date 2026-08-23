using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Rhino;

namespace WoodExport.Core
{
  internal static class PartNumbering
  {
    public static List<BomRow> Assign(IList<ExportPart> parts, ExportSettings settings)
    {
      var rows = new List<BomRow>();
      var counters = new Dictionary<string, int>(StringComparer.Ordinal);
      var groups = parts
        .GroupBy(item => item.ShapeSignature)
        .Select(group => group.ToList())
        .OrderBy(group => ThicknessKey(group[0].ThicknessMillimeters, settings))
        .ThenByDescending(group => group[0].WidthMillimeters * group[0].HeightMillimeters)
        .ThenBy(group => group[0].Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

      foreach (var group in groups)
      {
        var representative = group[0];
        var thicknessKey = ThicknessKey(representative.ThicknessMillimeters, settings);
        int counter;
        counters.TryGetValue(thicknessKey, out counter);
        counter++;
        counters[thicknessKey] = counter;
        var partNumber = "P" + FormatThickness(representative.ThicknessMillimeters) + "-" +
                         counter.ToString("000", CultureInfo.InvariantCulture);
        foreach (var part in group)
          part.PartNumber = partNumber;

        rows.Add(new BomRow
        {
          PartNumber = partNumber,
          Name = representative.Name,
          Quantity = group.Count,
          ThicknessMillimeters = group.Average(item => item.ThicknessMillimeters),
          WidthMillimeters = group.Max(item => item.WidthMillimeters),
          HeightMillimeters = group.Max(item => item.HeightMillimeters),
          BoundingAreaSquareMillimeters = representative.WidthMillimeters * representative.HeightMillimeters,
          SourceLayers = string.Join(";", group
            .SelectMany(item => (item.SourceLayers ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        });
      }
      return rows;
    }

    public static string FormatThickness(double millimeters)
    {
      var roundedInteger = Math.Round(millimeters);
      return Math.Abs(millimeters - roundedInteger) <= 0.05
        ? roundedInteger.ToString("0", CultureInfo.InvariantCulture)
        : millimeters.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string ThicknessKey(double millimeters, ExportSettings settings)
    {
      var bucket = Math.Round(
        millimeters / Math.Max(settings.ThicknessToleranceMillimeters, 0.001));
      return bucket.ToString(CultureInfo.InvariantCulture);
    }
  }
}
