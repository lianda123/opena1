using System;
using System.Collections.Generic;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace ProductMotionTimeline.Core
{
  internal static class TrackFactory
  {
    public static InstanceObject GetOrCreateAnimationPart(RhinoDoc doc)
    {
      var getter = new GetObject();
      getter.SetCommandPrompt("选择一个完整运动部件（可多选，插件会合并为动画块）");
      getter.GroupSelect = true;
      getter.SubObjectSelect = false;
      getter.GeometryFilter = ObjectType.AnyObject;
      getter.GetMultiple(1, 0);
      if (getter.CommandResult() != Result.Success)
        return null;

      if (getter.ObjectCount == 1)
      {
        var existingInstance = getter.Object(0).Object() as InstanceObject;
        if (existingInstance != null)
          return existingInstance;
      }

      var geometries = new List<GeometryBase>();
      var attributes = new List<ObjectAttributes>();
      var originalIds = new List<Guid>();
      for (var i = 0; i < getter.ObjectCount; i++)
      {
        var rhinoObject = getter.Object(i).Object();
        if (rhinoObject == null || rhinoObject.Geometry == null)
          continue;
        var geometry = rhinoObject.Geometry.Duplicate();
        var objectAttributes = rhinoObject.Attributes.Duplicate();
        if (geometry == null || objectAttributes == null)
          continue;
        geometries.Add(geometry);
        attributes.Add(objectAttributes);
        originalIds.Add(rhinoObject.Id);
      }

      if (geometries.Count == 0)
      {
        RhinoApp.WriteLine("ProductMotion：所选对象不能转换为动画部件。");
        return null;
      }

      var undo = doc.BeginUndoRecord("创建 ProductMotion 动画部件");
      try
      {
        var definitionName = NextDefinitionName(doc);
        var definitionIndex = doc.InstanceDefinitions.Add(
          definitionName,
          "ProductMotion 自动创建的动画部件",
          Point3d.Origin,
          geometries,
          attributes);
        if (definitionIndex < 0)
          return null;

        var instanceId = doc.Objects.AddInstanceObject(definitionIndex, Transform.Identity);
        if (instanceId == Guid.Empty)
          return null;

        foreach (var id in originalIds)
          doc.Objects.Delete(id, true);

        var instance = doc.Objects.FindId(instanceId) as InstanceObject;
        if (instance != null)
        {
          instance.Select(true);
          doc.Views.Redraw();
        }
        return instance;
      }
      finally
      {
        if (undo > 0)
          doc.EndUndoRecord(undo);
      }
    }

    private static string NextDefinitionName(RhinoDoc doc)
    {
      for (var i = 1; i < 10000; i++)
      {
        var name = "ProductMotionPart_" + i.ToString("000");
        if (doc.InstanceDefinitions.Find(name) == null)
          return name;
      }
      return "ProductMotionPart_" + Guid.NewGuid().ToString("N");
    }
  }
}
