using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace WoodExport.Core
{
  internal static class BomWriter
  {
    public static void Write(string path, IEnumerable<BomRow> rows)
    {
      var directory = Path.GetDirectoryName(path);
      if (!string.IsNullOrWhiteSpace(directory))
        Directory.CreateDirectory(directory);

      using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
      {
        writer.WriteLine("零件编号,名称,数量,厚度(mm),宽(mm),高(mm),外接面积(mm²),源图层");
        foreach (var row in rows)
        {
          writer.WriteLine(string.Join(",", new[]
          {
            Csv(row.PartNumber),
            Csv(row.Name),
            row.Quantity.ToString(CultureInfo.InvariantCulture),
            row.ThicknessMillimeters.ToString("0.###", CultureInfo.InvariantCulture),
            row.WidthMillimeters.ToString("0.###", CultureInfo.InvariantCulture),
            row.HeightMillimeters.ToString("0.###", CultureInfo.InvariantCulture),
            row.BoundingAreaSquareMillimeters.ToString("0.###", CultureInfo.InvariantCulture),
            Csv(row.SourceLayers)
          }));
        }
      }
    }

    private static string Csv(string value)
    {
      value = value ?? string.Empty;
      return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
  }
}
