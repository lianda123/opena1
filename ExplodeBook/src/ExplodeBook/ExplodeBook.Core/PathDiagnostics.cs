using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace ExplodeBook.Core;

internal static class PathDiagnostics
{
    private sealed class Markers:DisplayConduit
    {
        public RhinoDoc Doc;public AssemblyAnalysis Analysis;
        protected override void DrawForeground(DrawEventArgs e)
        {
            if(e.RhinoDoc!=Doc)return;
            // A global failure never proves that every selected part is faulty.
            foreach(var part in Analysis.PartIssues.Keys.Concat(Analysis.Blockers.Keys).Distinct())
            {
                string reason=Analysis.PartIssues.TryGetValue(part,out var issue)?issue:"路径受阻";
                e.Display.DrawDot(part.Center,part.PartNumber+" ["+reason+"]",Color.OrangeRed,Color.White);
            }
            string summary=Analysis.ValidationErrors.FirstOrDefault()??"";
            if(summary.Length>72)summary=summary.Substring(0,72)+"…";
            e.Display.Draw2dText("装配分析未完成："+summary,Color.DarkRed,new Point2d(12,24),false,14);
            e.Display.Draw2dText("只有已定位的问题件显示标记；完整原因见 F2 或 EBReport。",Color.DarkRed,new Point2d(12,46),false,13);
        }
    }
    private static readonly Dictionary<uint,Markers> Displays=new Dictionary<uint,Markers>();
    private static readonly Dictionary<uint,List<Guid>> Hidden=new Dictionary<uint,List<Guid>>();
    public static void Draw(RhinoDoc doc,AssemblyAnalysis analysis)
    {
        Clear(doc);
        if(analysis.ValidationErrors.Count>0){var m=new Markers{Doc=doc,Analysis=analysis,Enabled=true};Displays[doc.RuntimeSerialNumber]=m;}
        doc.Views.Redraw();
    }
    public static void Clear(RhinoDoc doc)
    {if(Displays.TryGetValue(doc.RuntimeSerialNumber,out var m)){m.Enabled=false;Displays.Remove(doc.RuntimeSerialNumber);doc.Views.Redraw();}}
    public static bool Focus(RhinoDoc doc,Guid source)
    {
        Restore(doc);var analysis=IntegratedAssemblyPlanner.Session(doc).Last;if(analysis==null)return false;
        var part=analysis.Parts.FirstOrDefault(p=>p.Objects.Any(o=>o.Id==source));if(part==null)return false;
        var keep=new HashSet<Guid>(part.Objects.Select(o=>o.Id));
        if(analysis.Blockers.TryGetValue(part,out var blockers))foreach(var o in blockers.SelectMany(p=>p.Objects))keep.Add(o.Id);
        var hidden=new List<Guid>();
        foreach(var o in doc.Objects.GetObjectList(ObjectType.AnyObject).Where(o=>!o.IsHidden&&!o.IsLocked&&!o.IsInstanceDefinitionGeometry&&o.Attributes.GetUserString(AssemblyAnalyzer.GeneratedKey)!="1"))
            if(!keep.Contains(o.Id)&&doc.Objects.Hide(o.Id,true))hidden.Add(o.Id);
        Hidden[doc.RuntimeSerialNumber]=hidden;doc.Views.Redraw();return true;
    }
    public static void Restore(RhinoDoc doc)
    {
        if(doc==null)return;
        if(Hidden.TryGetValue(doc.RuntimeSerialNumber,out var list)){foreach(var id in list)doc.Objects.Show(id,true);Hidden.Remove(doc.RuntimeSerialNumber);doc.Views.Redraw();}
    }
    public static void Close(RhinoDoc doc){Restore(doc);Clear(doc);IntegratedAssemblyPlanner.Close(doc.RuntimeSerialNumber);}
}
