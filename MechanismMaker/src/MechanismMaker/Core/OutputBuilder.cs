using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace MechanismMaker.Core
{
  internal static class OutputBuilder
  {
    public static List<Guid> AddAssembly(
      RhinoDoc doc,
      MechanismAssembly assembly,
      Plane placementPlane,
      MechanismSettings settings)
    {
      var added = new List<Guid>();
      if (doc == null || assembly == null || settings == null || !placementPlane.IsValid)
        return added;

      var unitsPerMm = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem);
      var scale = Transform.Scale(Point3d.Origin, unitsPerMm);
      var orient = Transform.PlaneToPlane(Plane.WorldXY, placementPlane);
      var undo = doc.BeginUndoRecord("MechanismMaker 生成机构");
      try
      {
        foreach (var part in assembly.Parts)
        {
          var layerIndex = EnsureLayer(doc, LayerName(part.Type), part.Color);
          var groupName = part.Name + "_" + assembly.MechanismId.ToString("N").Substring(0, 8);
          var groupIndex = doc.Groups.Add(groupName);
          var partIds = new List<Guid>();

          for (var curveIndex = 0; curveIndex < part.Curves.Count; curveIndex++)
          {
            var source = part.Curves[curveIndex];
            var curve = source == null ? null : source.DuplicateCurve();
            if (curve == null || !curve.Transform(scale) || !curve.Transform(orient))
              continue;

            var attributes = new ObjectAttributes
            {
              LayerIndex = layerIndex,
              ColorSource = ObjectColorSource.ColorFromLayer,
              Name = curveIndex == 0 ? part.Name : part.Name + "_孔槽_" + curveIndex.ToString("00")
            };
            if (groupIndex >= 0)
              attributes.AddToGroup(groupIndex);
            attributes.SetUserString("MM.Type", part.Type);
            attributes.SetUserString("MM.MechanismId", assembly.MechanismId.ToString("D"));
            attributes.SetUserString("MM.PartName", part.Name);
            attributes.SetUserString("MM.BoardThickness", settings.BoardThicknessMm.ToString("0.###"));
            foreach (var pair in part.Metadata)
              attributes.SetUserString(pair.Key, pair.Value);
            attributes.SetUserString("MECHANISM_INFO", BuildSummary(part, settings));

            var id = doc.Objects.AddCurve(curve, attributes);
            if (id != Guid.Empty)
            {
              added.Add(id);
              partIds.Add(id);
            }
          }

          if (partIds.Count > 0)
            RhinoApp.WriteLine("MechanismMaker：已生成 {0}（{1} 条轮廓）。", part.Name, partIds.Count);
        }
      }
      finally
      {
        if (undo > 0)
          doc.EndUndoRecord(undo);
      }

      doc.Views.Redraw();
      return added;
    }

    private static string BuildSummary(GeneratedPart part, MechanismSettings settings)
    {
      var values = new List<string>
      {
        "类型=" + part.Type,
        "板厚=" + settings.BoardThicknessMm.ToString("0.###") + "mm",
        "固定孔=" + settings.FixedHoleMm.ToString("0.###") + "mm",
        "活动孔=" + settings.RotatingHoleMm.ToString("0.###") + "mm"
      };
      values.AddRange(part.Metadata.Select(pair => pair.Key + "=" + pair.Value));
      return string.Join(";", values);
    }

    private static string LayerName(string type)
    {
      if (type.IndexOf("Gear", StringComparison.OrdinalIgnoreCase) >= 0 ||
          type.IndexOf("Rack", StringComparison.OrdinalIgnoreCase) >= 0)
        return "MechanismMaker_齿轮齿条";
      if (type.IndexOf("Cam", StringComparison.OrdinalIgnoreCase) >= 0)
        return "MechanismMaker_凸轮";
      if (type.IndexOf("FourBar", StringComparison.OrdinalIgnoreCase) >= 0 ||
          type.IndexOf("Crank", StringComparison.OrdinalIgnoreCase) >= 0)
        return "MechanismMaker_连杆";
      if (type.IndexOf("Ratchet", StringComparison.OrdinalIgnoreCase) >= 0)
        return "MechanismMaker_棘轮";
      if (type.IndexOf("Geneva", StringComparison.OrdinalIgnoreCase) >= 0)
        return "MechanismMaker_日内瓦";
      return "MechanismMaker_机构";
    }

    private static int EnsureLayer(RhinoDoc doc, string name, System.Drawing.Color color)
    {
      foreach (var layer in doc.Layers)
      {
        if (!layer.IsDeleted && string.Equals(layer.Name, name, StringComparison.OrdinalIgnoreCase))
          return layer.Index;
      }

      return doc.Layers.Add(new Layer
      {
        Name = name,
        Color = color
      });
    }
  }
}
