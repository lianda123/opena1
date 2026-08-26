using System;
using System.Collections.Generic;
using System.Linq;
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
      return GetOrCreateAnimationPart(doc, false, null, true);
    }

    public static InstanceObject GetOrCreateGroupPart(RhinoDoc doc)
    {
      return GetOrCreateAnimationPart(doc, true, null, true);
    }

    public static InstanceObject GetOrCreateGroupPart(
      RhinoDoc doc,
      string prompt,
      bool enablePreSelect)
    {
      return GetOrCreateAnimationPart(doc, true, prompt, enablePreSelect);
    }

    public static InstanceObject CreateGeneratedPart(
      RhinoDoc doc,
      GeometryBase geometry,
      string name,
      GearParameters gearParameters)
    {
      return CreateGeneratedPart(
        doc,
        geometry == null ? null : new[] { geometry },
        name,
        gearParameters);
    }

    public static InstanceObject CreateGeneratedPart(
      RhinoDoc doc,
      IEnumerable<GeometryBase> sourceGeometry,
      string name,
      GearParameters gearParameters)
    {
      var geometries = sourceGeometry?.Where(geometry => geometry != null).ToList();
      if (doc == null || geometries == null || geometries.Count == 0)
        return null;
      var definitionName = NextDefinitionName(doc);
      var geometryAttributes = new List<ObjectAttributes>();
      for (var index = 0; index < geometries.Count; index++)
      {
        var attributes = new ObjectAttributes
        {
          Name = index == 0 ? name : "分度圆或分度线（辅助）"
        };
        if (index > 0)
        {
          attributes.ColorSource = ObjectColorSource.ColorFromObject;
          attributes.ObjectColor = System.Drawing.Color.FromArgb(0, 190, 255);
          attributes.SetUserString("ProductMotionTimeline.Auxiliary", "PitchReference");
        }
        geometryAttributes.Add(attributes);
      }
      var definitionIndex = doc.InstanceDefinitions.Add(
        definitionName,
        "ProductMotion 自动生成的机构部件",
        Point3d.Origin,
        geometries,
        geometryAttributes);
      if (definitionIndex < 0)
        return null;
      var instanceId = doc.Objects.AddInstanceObject(definitionIndex, Transform.Identity);
      if (instanceId == Guid.Empty)
        return null;
      var attributes = new ObjectAttributes { Name = name };
      doc.Objects.ModifyAttributes(instanceId, attributes, true);
      if (gearParameters != null)
        GearPartMetadata.Write(doc, instanceId, gearParameters);
      var instance = doc.Objects.FindId(instanceId) as InstanceObject;
      instance?.Select(true);
      doc.Views.Redraw();
      return instance;
    }

    private static InstanceObject GetOrCreateAnimationPart(
      RhinoDoc doc,
      bool selectInsideGroup,
      string prompt,
      bool enablePreSelect)
    {
      var getter = new GetObject();
      getter.SetCommandPrompt(string.IsNullOrWhiteSpace(prompt)
        ? (selectInsideGroup
          ? "选择组内需要单独运动的零件（不会自动选中整组，可多选）"
          : "选择一个完整运动部件（可多选，插件会合并为动画块）")
        : prompt);
      getter.GroupSelect = !selectInsideGroup;
      getter.SubObjectSelect = false;
      getter.GeometryFilter = ObjectType.AnyObject;
      getter.EnablePreSelect(enablePreSelect, true);
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
      HashSet<int> sharedGroupIndices = null;
      string selectedName = null;
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
        if (string.IsNullOrWhiteSpace(selectedName) && !string.IsNullOrWhiteSpace(rhinoObject.Attributes.Name))
          selectedName = rhinoObject.Attributes.Name;

        var groups = rhinoObject.Attributes.GetGroupList() ?? new int[0];
        if (sharedGroupIndices == null)
          sharedGroupIndices = new HashSet<int>(groups);
        else
          sharedGroupIndices.IntersectWith(groups);
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

        var instanceAttributes = new ObjectAttributes
        {
          Name = string.IsNullOrWhiteSpace(selectedName) ? definitionName : selectedName
        };
        if (sharedGroupIndices != null)
        {
          foreach (var groupIndex in sharedGroupIndices.Where(index => index >= 0))
            instanceAttributes.AddToGroup(groupIndex);
        }
        doc.Objects.ModifyAttributes(instanceId, instanceAttributes, true);

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
