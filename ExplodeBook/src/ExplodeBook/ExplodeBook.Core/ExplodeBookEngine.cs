using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;

namespace ExplodeBook.Core;

internal static class ExplodeBookEngine
{
    internal const string LayoutManifest="ExplodeBook2.Layouts";
    internal const string SignatureKey="ExplodeBook2.BookSignature";
    private static List<Guid> Generated(RhinoDoc doc)=>doc.Objects.GetObjectList(ObjectType.AnyObject).Where(o=>!o.IsInstanceDefinitionGeometry&&o.Attributes.GetUserString(AssemblyAnalyzer.GeneratedKey)=="1").Select(o=>o.Id).ToList();
    public static GeneratedBook Execute(RhinoDoc doc,IEnumerable<RhinoObject> selection,ExplodeSettings settings,bool createOverview,bool createPages,bool linkSources,out AssemblyAnalysis analysis)
    {
        using(AnalysisCancellation.Begin())
        using(AutoUpdateController.Suppress())
        {
            PathDiagnostics.Restore(doc);
            var selected=selection?.Where(o=>o!=null).ToList()??new List<RhinoObject>();
            analysis=AssemblyAnalyzer.Analyze(doc,selected,settings);
            var book=new GeneratedBook();
            if(!analysis.PlanVerified||analysis.ValidationErrors.Count>0){PathDiagnostics.Draw(doc,analysis);doc.Strings.SetString(SignatureKey,"");return book;}
            if(createPages&&settings.IncludeModulePartPages&&settings.MaximumStepPages<analysis.Parts.Count)
            {analysis.ValidationErrors.Add("步骤上限小于零件数，停止生成以避免缺失步骤；请在 EBSettings 中提高上限。");return book;}
            var savedAttributes=analysis.Parts.SelectMany(p=>p.Objects).Concat(LinkedBookManager.LinkedSources(doc)).GroupBy(o=>o.Id).Select(g=>g.First()).ToDictionary(o=>o.Id,o=>o.Attributes.Duplicate());
            var oldObjects=new HashSet<Guid>(Generated(doc));
            var oldViews=new HashSet<Guid>((doc.Views.GetPageViews()??new RhinoPageView[0]).Select(v=>v.MainViewport.Id));
            var oldManifest=doc.Strings.GetValue(LayoutManifest)??"";
            var ownedNames=new HashSet<string>(oldManifest.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries));
            bool committed=false;
            var oldOwned=(doc.Views.GetPageViews()??new RhinoPageView[0]).Where(v=>ownedNames.Contains(v.PageName)).ToList();
            try
            {
                RenderCache.Begin();
                if(createOverview)book.GeneratedObjectIds.AddRange(DrawingBuilder.CreateExplodedOverview(doc,analysis,settings));
                if(createPages)
                {
                    var pages=ManualPageBuilder.CreatePages(doc,analysis,settings,book.GeneratedObjectIds);
                    if(pages.Count==0)throw new InvalidOperationException("没有生成说明页。");
                    book.LayoutNames.AddRange(pages.Select(p=>p.LayoutName));
                    book.StepCount=settings.IncludeModulePartPages?analysis.Parts.Count:0;
                }
                // Source links/metadata and previous output change only after new pages succeed.
                ApplySourceMetadata(doc,analysis,settings);
                if(linkSources)LinkedBookManager.LinkSources(doc,analysis.Parts.SelectMany(p=>p.Objects),settings,createOverview,createPages);
                doc.Strings.SetString(LayoutManifest,string.Join("\n",book.LayoutNames));
                doc.Strings.SetString(SignatureKey,analysis.Signature);
                IntegratedAssemblyPlanner.Session(doc).BookSignature=analysis.Signature;
                committed=true;
                foreach(var id in oldObjects)doc.Objects.Delete(id,true);
                foreach(var view in oldOwned)view.Close();
                RenderCache.PurgeUnused(doc);
                book.PartCount=analysis.Parts.Count;book.IsLinked=true;
                doc.Views.Redraw();return book;
            }
            catch(Exception e)
            {
                if(committed){book.PartCount=analysis.Parts.Count;book.IsLinked=true;analysis.Warnings.Add("新说明书已完成；旧结果清理未完成："+e.Message);return book;}
                foreach(var saved in savedAttributes)doc.Objects.ModifyAttributes(saved.Key,saved.Value,true);
                foreach(var id in Generated(doc).Where(id=>!oldObjects.Contains(id)))doc.Objects.Delete(id,true);
                foreach(var view in (doc.Views.GetPageViews()??new RhinoPageView[0]).Where(v=>!oldViews.Contains(v.MainViewport.Id)).ToList())view.Close();
                doc.Strings.SetString(LayoutManifest,oldManifest);
                RenderCache.PurgeUnused(doc);
                analysis.ValidationErrors.Add("说明书生成未完成："+e.Message+"；前一版说明页已保留。");
                doc.Strings.SetString(SignatureKey,"");doc.Views.Redraw();return new GeneratedBook();
            }
        }
    }
    public static GeneratedBook UpdateLinked(RhinoDoc doc,out AssemblyAnalysis analysis)
    {
        var settings=new ExplodeSettings();
        if(!LinkedBookManager.TryLoad(doc,settings,out var sources,out var overview,out var pages)){analysis=new AssemblyAnalysis();return new GeneratedBook();}
        return Execute(doc,sources,settings,overview,pages,false,out analysis);
    }
    public static int ClearGenerated(RhinoDoc doc)
    {
        if(doc==null)return 0;PathDiagnostics.Restore(doc);PathDiagnostics.Clear(doc);var ids=Generated(doc);
        foreach(var id in ids)doc.Objects.Delete(id,true);
        var names=new HashSet<string>((doc.Strings.GetValue(LayoutManifest)??"").Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries));
        foreach(var page in (doc.Views.GetPageViews()??new RhinoPageView[0]).Where(p=>names.Contains(p.PageName)).ToList())page.Close();
        doc.Strings.SetString(LayoutManifest,"");doc.Strings.SetString(SignatureKey,"");RenderCache.PurgeUnused(doc);doc.Views.Redraw();return ids.Count;
    }
    private static void ApplySourceMetadata(RhinoDoc doc,AssemblyAnalysis analysis,ExplodeSettings settings)
    {
        foreach(var part in analysis.Parts)foreach(var source in part.Objects)
        {
            var a=source.Attributes.Duplicate();a.SetUserString(AssemblyAnalyzer.PartNumberKey,part.PartNumber);
            a.SetUserString("EB2.CalculatedOrder",part.AssemblyOrder.ToString(CultureInfo.InvariantCulture));
            a.SetUserString("EB2.CalculatedModuleOrder",part.ModuleOrder.ToString(CultureInfo.InvariantCulture));
            a.SetUserString("EB2.CalculatedPartOrder",part.ModulePartOrder.ToString(CultureInfo.InvariantCulture));
            a.SetUserString(AssemblyAnalyzer.SettingsKey,LinkedBookManager.Serialize(settings));doc.Objects.ModifyAttributes(source.Id,a,true);
        }
    }
}
