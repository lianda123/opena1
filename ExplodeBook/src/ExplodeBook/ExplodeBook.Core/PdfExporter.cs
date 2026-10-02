using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Rhino;
using Rhino.Display;
using Rhino.FileIO;

namespace ExplodeBook.Core;

internal static class PdfExporter
{
	public static void ValidateBook(RhinoDoc doc)
    {
        PathDiagnostics.Restore(doc);
        var settings=new ExplodeSettings();
        if(!LinkedBookManager.TryLoad(doc,settings,out var sources,out _,out _)) throw new InvalidOperationException("没有关联模型，请先运行 ExplodeBook。");
        var analysis=AssemblyAnalyzer.Analyze(doc,sources,settings);
        if(!analysis.PlanVerified || analysis.ValidationErrors.Count>0) throw new InvalidOperationException("装配检查未通过："+string.Join("；",analysis.ValidationErrors)+"。请修正后运行 EBUpdate。");
        if(doc.Strings.GetValue(ExplodeBookEngine.SignatureKey)!=analysis.Signature) throw new InvalidOperationException("说明书与当前模型或设置不一致，请运行 EBUpdate 后导出。");
    }

	public static int ExportLayouts(RhinoDoc doc, string path, int dpi)
	{
		if (doc == null || string.IsNullOrWhiteSpace(path))
		{
			return 0;
		}
		ValidateBook(doc);
        var owned=new HashSet<string>((doc.Strings.GetValue(ExplodeBookEngine.LayoutManifest)??"").Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries));
        List<RhinoPageView> list = (from item in doc.Views.GetPageViews() ?? new RhinoPageView[0]
			where owned.Contains(item.PageName)
			orderby item.PageNumber
			select item).ToList();
        if(list.Count==0||list.Count!=owned.Count)throw new InvalidOperationException("说明页缺失；请运行 EBUpdate 重建完整说明书后导出。");
        double pageMillimeters=RhinoMath.UnitScale(doc.PageUnitSystem,UnitSystem.Millimeters);
		string filename = Path.ChangeExtension(path, ".pdf");
		FilePdf filePdf = FilePdf.Create();
		foreach (RhinoPageView item in list)
		{
            int width = Math.Max(1, (int)Math.Round(item.PageWidth * pageMillimeters / 25.4 * (double)dpi));
            int height = Math.Max(1, (int)Math.Round(item.PageHeight * pageMillimeters / 25.4 * (double)dpi));
			ViewCaptureSettings settings = new ViewCaptureSettings(item, new Size(width, height), dpi)
			{
				RasterMode = true,
				DrawBackground = false,
				DrawGrid = false,
				DrawAxis = false
			};
			filePdf.AddPage(settings);
		}
		string staged=filename+"."+Guid.NewGuid().ToString("N")+".pdf";
        try {
            filePdf.Write(staged);
            if(!File.Exists(staged)||new FileInfo(staged).Length==0) throw new IOException("PDF写入失败。");
            if(File.Exists(filename)) File.Replace(staged,filename,null); else File.Move(staged,filename);
        } finally {if(File.Exists(staged)) File.Delete(staged); }
		return list.Count;
	}
}
