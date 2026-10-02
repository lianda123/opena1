using System;
using System.IO;
using ExplodeBook.Core;
using Rhino;
using Rhino.Commands;
using Rhino.UI;

namespace ExplodeBook.Commands;

public sealed class ExplodeBookExportPdfCommand : Command
{
	public override string EnglishName => "EBExportPDF";

	protected override Result RunCommand(RhinoDoc doc, RunMode mode)
	{
		try { PdfExporter.ValidateBook(doc); } catch(Exception ex) { RhinoApp.WriteLine(ex.Message); return Result.Failure; }
		string fileName = (string.IsNullOrWhiteSpace(doc.Name) ? "ExplodeBook_装配说明书.pdf" : (Path.GetFileNameWithoutExtension(doc.Name) + "_装配说明书.pdf"));
		SaveFileDialog saveFileDialog = new SaveFileDialog
		{
			Title = "导出 ExplodeBook 多页 PDF",
			Filter = "PDF 文档 (*.pdf)|*.pdf",
			DefaultExt = "pdf",
			FileName = fileName
		};
		if (!saveFileDialog.ShowSaveDialog())
		{
			return Result.Cancel;
		}
		try
		{
			int num = PdfExporter.ExportLayouts(doc, saveFileDialog.FileName, ExplodeBookPlugin.CurrentSettings.PdfDpi);
			if (num == 0)
			{
				RhinoApp.WriteLine("ExplodeBook：没有找到 EB_ 开头的说明书页面。");
				return Result.Nothing;
			}
			RhinoApp.WriteLine("ExplodeBook：已导出 {0} 页 PDF：{1}", num, saveFileDialog.FileName);
			return Result.Success;
		}
		catch (Exception ex)
		{
			RhinoApp.WriteLine("ExplodeBook：PDF导出失败：" + ex.Message);
			return Result.Failure;
		}
	}
}
