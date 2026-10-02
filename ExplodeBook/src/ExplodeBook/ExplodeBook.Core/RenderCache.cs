using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace ExplodeBook.Core;

internal static class RenderCache
{
    private static readonly Dictionary<string,int> Definitions=new Dictionary<string,int>();
    private static string generation=Guid.NewGuid().ToString("N");
    public static void Begin(){Definitions.Clear();generation=Guid.NewGuid().ToString("N");}
    public static Guid Add(RhinoDoc doc,AssemblyPart part,Transform transform,int group,bool highlight)
    {
        if(AnalysisCancellation.Check()) throw new OperationCanceledException();
        int index=PartDefinition(doc,part,highlight);
        return Instance(doc,index,transform,group,part.PartNumber);
    }
    private static int PartDefinition(RhinoDoc doc,AssemblyPart part,bool highlight)
    {
        string key="part:"+part.Sequence+":"+highlight;
        if(!Definitions.TryGetValue(key,out int index))
        {
            var geometry=new List<GeometryBase>();var attributes=new List<ObjectAttributes>();
            foreach(var source in part.Objects)
            {
                geometry.Add(source.Geometry.Duplicate());var a=source.Attributes.Duplicate();a.RemoveFromAllGroups();
                foreach(string k in new[]{"ExplodeBook.LinkedSource","ExplodeBook.Generated","ExplodeBook.Settings","ExplodeBook.GenerateOverview","ExplodeBook.GeneratePages"})a.DeleteUserString(k);
                if(a.ColorSource == ObjectColorSource.ColorFromLayer) { a.ObjectColor=doc.Layers[a.LayerIndex].Color; a.ColorSource=ObjectColorSource.ColorFromObject; }
                a.LayerIndex=DrawingBuilder.FindOrCreateLayer(doc,"ExplodeBook_板件",System.Drawing.Color.LightGray);
                if(highlight){a.ObjectColor=System.Drawing.Color.FromArgb(245,178,35);a.ColorSource=ObjectColorSource.ColorFromObject;}
                attributes.Add(a);
            }
            try{index=doc.InstanceDefinitions.Add("EB2_Render_"+generation+"_"+Definitions.Count,"ExplodeBook 2.0 shared original geometry",Point3d.Origin,geometry,attributes);}
            finally{foreach(var g in geometry)g.Dispose();}
            if(index<0)throw new InvalidOperationException("无法建立说明书零件共享定义："+part.PartNumber);
            Definitions[key]=index;
        }
        return index;
    }
    private static Guid Instance(RhinoDoc doc,int index,Transform transform,int group,string number)
    {
        var attr=new ObjectAttributes {LayerIndex=DrawingBuilder.FindOrCreateLayer(doc,"ExplodeBook_板件",System.Drawing.Color.LightGray)};attr.SetUserString(AssemblyAnalyzer.GeneratedKey,"1");attr.SetUserString("ExplodeBook.PartNumber",number);
        attr.Name="EB_"+number;if(group>=0)attr.AddToGroup(group);
        var id=doc.Objects.AddInstanceObject(index,transform,attr);
        if(id==Guid.Empty)throw new InvalidOperationException("无法生成说明书板件："+number);
        return id;
    }
    public static Guid AddInstalled(RhinoDoc doc,IList<AssemblyPart> parts,Transform transform,int group)
    {return Instance(doc,Prefix(doc,parts,0,parts.Count),transform,group,"阶段装配");}
    private static int Prefix(RhinoDoc doc,IList<AssemblyPart> parts,int start,int count)
    {
        if(count==1)return PartDefinition(doc,parts[start],false);
        string key="stage:"+string.Join(",",parts.Skip(start).Take(count).Select(p=>p.Sequence));
        if(Definitions.TryGetValue(key,out int existing))return existing;
        int left=1;while(left*2<count)left*=2;
        int a=Prefix(doc,parts,start,left),b=Prefix(doc,parts,start+left,count-left);
        var geometries=new GeometryBase[]{new InstanceReferenceGeometry(doc.InstanceDefinitions[a].Id,Transform.Identity),new InstanceReferenceGeometry(doc.InstanceDefinitions[b].Id,Transform.Identity)};
        int layer=DrawingBuilder.FindOrCreateLayer(doc,"ExplodeBook_板件",System.Drawing.Color.LightGray);
        var attrs=new[]{new ObjectAttributes{LayerIndex=layer},new ObjectAttributes{LayerIndex=layer}};
        int index;
        try{index=doc.InstanceDefinitions.Add("EB2_Render_"+generation+"_"+Definitions.Count,"Shared cumulative assembly stage",Point3d.Origin,geometries,attrs);}
        finally{foreach(var g in geometries)g.Dispose();}
        if(index<0)throw new InvalidOperationException("无法建立累计装配阶段。");Definitions[key]=index;return index;
    }
    public static void PurgeUnused(RhinoDoc doc)
    {
        foreach(var def in doc.InstanceDefinitions.Where(d=>d!=null&&!d.IsDeleted&&d.Name.StartsWith("EB2_Render_",StringComparison.Ordinal)).OrderByDescending(d=>d.Index).ToList())
            if((def.GetReferences(2)??new InstanceObject[0]).Length==0)doc.InstanceDefinitions.Delete(def.Index,false,true);
    }
}
