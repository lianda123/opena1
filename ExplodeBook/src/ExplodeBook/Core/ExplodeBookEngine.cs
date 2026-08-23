using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.DocObjects;

namespace ExplodeBook.Core
{
  internal static class ExplodeBookEngine
  {
    public static GeneratedBook Execute(
      RhinoDoc doc,
      IEnumerable<RhinoObject> selection,
      ExplodeSettings settings,
      bool createOverview,
      bool createPages,
      out AssemblyAnalysis analysis)
    {
      analysis = AssemblyAnalyzer.Analyze(doc, selection, settings);
      var result = new GeneratedBook { PartCount = analysis.Parts.Count };
      if (analysis.Parts.Count == 0)
        return result;

      ClearGenerated(doc);
      ApplySourceMetadata(doc, analysis);
      if (createOverview)
      {
        result.GeneratedObjectIds.AddRange(
          DrawingBuilder.CreateExplodedOverview(doc, analysis, settings));
      }
      if (createPages)
      {
        var pages = ManualPageBuilder.CreatePages(
          doc, analysis, settings, result.GeneratedObjectIds);
        result.LayoutNames.AddRange(pages.Select(item => item.LayoutName));
        result.StepCount = pages.Count - 1;
      }
      doc.Views.Redraw();
      return result;
    }

    public static int ClearGenerated(RhinoDoc doc)
    {
      if (doc == null)
        return 0;
      var ids = doc.Objects.GetObjectList(ObjectType.AnyObject)
        .Where(item => item.Attributes.GetUserString(AssemblyAnalyzer.GeneratedKey) ==
                       AssemblyAnalyzer.GeneratedValue)
        .Select(item => item.Id)
        .ToList();
      foreach (var id in ids)
        doc.Objects.Delete(id, true);

      var layouts = doc.Views.GetPageViews() ?? new Rhino.Display.RhinoPageView[0];
      foreach (var page in layouts.Where(item =>
        item.PageName.StartsWith("EB_", StringComparison.OrdinalIgnoreCase)).ToList())
        page.Close();
      doc.Views.Redraw();
      return ids.Count;
    }

    private static void ApplySourceMetadata(RhinoDoc doc, AssemblyAnalysis analysis)
    {
      foreach (var part in analysis.Parts)
      {
        foreach (var source in part.Objects)
        {
          var attributes = source.Attributes.Duplicate();
          attributes.SetUserString(AssemblyAnalyzer.PartNumberKey, part.PartNumber);
          attributes.SetUserString(
            AssemblyAnalyzer.OrderKey,
            part.AssemblyOrder.ToString(CultureInfo.InvariantCulture));
          doc.Objects.ModifyAttributes(source.Id, attributes, true);
        }
      }
    }
  }
}
