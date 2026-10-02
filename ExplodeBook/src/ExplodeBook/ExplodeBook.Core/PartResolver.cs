using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace ExplodeBook.Core;

internal static class PartResolver
{
    public static bool Physical(RhinoObject o) => o?.Geometry is Brep || o?.Geometry is Extrusion || o?.Geometry is Mesh || o?.Geometry is SubD || o is InstanceObject;
    public static List<List<RhinoObject>> Resolve(IEnumerable<RhinoObject> source,out List<string> warnings)
    {
        warnings=new List<string>();
        var objects=source.Where(o=>o!=null&&o.Geometry!=null&&!o.IsDeleted&&!o.IsInstanceDefinitionGeometry)
            .Where(o=>o.Attributes.GetUserString(AssemblyAnalyzer.GeneratedKey)!="1"&&o.Attributes.GetUserString("WoodSheetLayout.FlatCopy")!="1")
            .Where(o=>!(o.Document?.Layers[o.Attributes.LayerIndex]?.FullPath??"").StartsWith("WoodSheetLayout_",StringComparison.OrdinalIgnoreCase))
            .GroupBy(o=>o.Id).Select(g=>g.First()).ToList();
        var bodies=objects.Where(Physical).ToList();
        var result=new List<List<RhinoObject>>();var owners=new Dictionary<Guid,List<RhinoObject>>();
        foreach(var o in bodies)
        {
            string rigid=o.Attributes.GetUserString("EB2.RigidPart");
            List<RhinoObject> group=!string.IsNullOrEmpty(rigid)?result.FirstOrDefault(g=>g[0].Attributes.GetUserString("EB2.RigidPart")==rigid):null;
            if(group==null){group=new List<RhinoObject>();result.Add(group);}group.Add(o);owners[o.Id]=group;
        }
        foreach(var decoration in objects.Where(o=>!Physical(o)))
        {
            var groups=new HashSet<int>(decoration.Attributes.GetGroupList()??new int[0]);
            var candidates=bodies.Where(o=>(o.Attributes.GetGroupList()??new int[0]).Any(groups.Contains)).Select(o=>owners[o.Id]).Distinct().ToList();
            string parent=decoration.Attributes.GetUserString("WAB.ParentId")??decoration.Attributes.GetUserString("WoodFlex.SourceId");
            if(Guid.TryParse(parent,out Guid id)&&owners.TryGetValue(id,out var exact))candidates=new List<List<RhinoObject>>{exact};
            if(candidates.Count==1)candidates[0].Add(decoration);
            else warnings.Add("附属曲线/文字归属"+(candidates.Count==0?"缺失":"不唯一")+"："+(decoration.Attributes.Name??decoration.Id.ToString())+"；请与唯一板件单独打组或补充所属板件。");
        }
        return result;
    }
}
