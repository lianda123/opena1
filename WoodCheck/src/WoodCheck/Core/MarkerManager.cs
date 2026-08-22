using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace WoodCheck.Core
{
  internal static class MarkerManager
  {
    public const string MarkerKey = "WoodCheckMarker";
    public const string MarkerValue = "1";
    public const string IssueCodeKey = "WoodCheckIssueCode";
    public const string SourceIdsKey = "WoodCheckSourceIds";

    public static void Clear(RhinoDoc doc)
    {
      if (doc == null)
        return;
      var markers = doc.Objects
        .GetObjectList(ObjectType.AnyObject)
        .Where(item => item.Attributes.GetUserString(MarkerKey) == MarkerValue)
        .Select(item => item.Id)
        .ToList();
      foreach (var id in markers)
        doc.Objects.Delete(id, true);
    }

    public static void Render(RhinoDoc doc, CheckReport report)
    {
      if (doc == null || report == null)
        return;

      var errorLayer = EnsureLayer(doc, "WoodCheck_错误", Color.FromArgb(230, 40, 40));
      var warningLayer = EnsureLayer(doc, "WoodCheck_警告", Color.FromArgb(255, 145, 0));
      var infoLayer = EnsureLayer(doc, "WoodCheck_提示", Color.FromArgb(255, 210, 0));

      foreach (var issue in report.Issues)
      {
        var layerIndex = issue.Severity == CheckSeverity.Error
          ? errorLayer
          : issue.Severity == CheckSeverity.Warning ? warningLayer : infoLayer;
        var color = issue.Severity == CheckSeverity.Error
          ? Color.FromArgb(230, 40, 40)
          : issue.Severity == CheckSeverity.Warning
            ? Color.FromArgb(255, 145, 0)
            : Color.FromArgb(255, 210, 0);
        var attributes = CreateAttributes(layerIndex, color, issue);
        doc.Objects.AddPoint(issue.Location, attributes);

        var dot = new TextDot(issue.Code + " " + issue.Title, issue.Location);
        doc.Objects.AddTextDot(dot, CreateAttributes(layerIndex, color, issue));
      }
    }

    public static List<Guid> FindSourceIds(RhinoDoc doc, string issueCode)
    {
      var result = new List<Guid>();
      if (doc == null || string.IsNullOrWhiteSpace(issueCode))
        return result;

      var marker = doc.Objects
        .GetObjectList(ObjectType.AnyObject)
        .FirstOrDefault(item =>
          item.Attributes.GetUserString(MarkerKey) == MarkerValue &&
          string.Equals(item.Attributes.GetUserString(IssueCodeKey), issueCode.Trim(),
            StringComparison.OrdinalIgnoreCase));
      if (marker == null)
        return result;

      var value = marker.Attributes.GetUserString(SourceIdsKey) ?? string.Empty;
      foreach (var token in value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
      {
        Guid id;
        if (Guid.TryParse(token, out id))
          result.Add(id);
      }
      return result;
    }

    private static ObjectAttributes CreateAttributes(int layerIndex, Color color, CheckIssue issue)
    {
      var attributes = new ObjectAttributes
      {
        LayerIndex = layerIndex,
        ColorSource = ObjectColorSource.ColorFromObject,
        ObjectColor = color,
        Name = issue.Code + " " + issue.Title
      };
      attributes.SetUserString(MarkerKey, MarkerValue);
      attributes.SetUserString(IssueCodeKey, issue.Code);
      attributes.SetUserString(SourceIdsKey, string.Join(";", issue.SourceIds.Select(item => item.ToString("D"))));
      return attributes;
    }

    private static int EnsureLayer(RhinoDoc doc, string name, Color color)
    {
      foreach (var layer in doc.Layers)
      {
        if (!layer.IsDeleted && string.Equals(layer.Name, name, StringComparison.OrdinalIgnoreCase))
          return layer.Index;
      }

      var newLayer = new Layer
      {
        Name = name,
        Color = color
      };
      return doc.Layers.Add(newLayer);
    }
  }
}
