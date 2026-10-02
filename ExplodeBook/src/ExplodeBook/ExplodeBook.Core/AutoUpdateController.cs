using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.DocObjects;

namespace ExplodeBook.Core;

internal static class AutoUpdateController
{
	private sealed class SuppressionScope : IDisposable
	{
		private bool _disposed;

		public void Dispose()
		{
			if (!_disposed)
			{
				_disposed = true;
				_suppressionDepth = Math.Max(0, _suppressionDepth - 1);
			}
		}
	}

	private static readonly Dictionary<uint, RhinoDoc> Pending = new Dictionary<uint, RhinoDoc>();

	private static readonly Dictionary<uint,DateTime> Due=new Dictionary<uint,DateTime>();

	private static int _suppressionDepth;

	private static bool _processing;

	private static bool _subscribed;

	public static bool IsSuppressed
	{
		get
		{
			if (_suppressionDepth <= 0)
			{
				return _processing;
			}
			return true;
		}
	}

	public static void Subscribe()
	{
		if (!_subscribed)
		{
			RhinoDoc.AddRhinoObject += OnObjectChanged;
			RhinoDoc.DeleteRhinoObject += OnObjectChanged;
			RhinoDoc.UndeleteRhinoObject += OnObjectChanged;
			RhinoDoc.ModifyObjectAttributes += OnAttributesChanged;
			RhinoDoc.ReplaceRhinoObject += OnObjectReplaced;
			RhinoApp.Idle += OnIdle;
            RhinoDoc.CloseDocument += OnClose;
            RhinoDoc.BeginSaveDocument += OnSave;
			_subscribed = true;
		}
	}

	public static void Unsubscribe()
	{
		if (_subscribed)
		{
			RhinoDoc.AddRhinoObject -= OnObjectChanged;
			RhinoDoc.DeleteRhinoObject -= OnObjectChanged;
			RhinoDoc.UndeleteRhinoObject -= OnObjectChanged;
			RhinoDoc.ModifyObjectAttributes -= OnAttributesChanged;
			RhinoDoc.ReplaceRhinoObject -= OnObjectReplaced;
			RhinoApp.Idle -= OnIdle;
            RhinoDoc.CloseDocument -= OnClose;
            RhinoDoc.BeginSaveDocument -= OnSave;
			Pending.Clear();
			_subscribed = false;
		}
	}

	public static IDisposable Suppress()
	{
		_suppressionDepth++;
		return new SuppressionScope();
	}

	public static void Schedule(RhinoDoc doc)
	{
		if (doc != null && !IsSuppressed)
		{
			Pending[doc.RuntimeSerialNumber] = doc;
            Due[doc.RuntimeSerialNumber]=DateTime.UtcNow.AddMilliseconds(900);
		}
	}

	private static void OnObjectChanged(object sender, RhinoObjectEventArgs args)
	{
		if (!IsSuppressed && args != null)
		{
			ScheduleIfLinked(args.TheObject);
		}
	}

	private static void OnAttributesChanged(object sender, RhinoModifyObjectAttributesEventArgs args)
	{
		if (!IsSuppressed && args != null)
		{
			ScheduleIfLinked(args.RhinoObject);
		}
	}

	private static void OnObjectReplaced(object sender, RhinoReplaceObjectEventArgs args)
	{
		if (!IsSuppressed && args != null)
		{
			ScheduleIfLinked(args.OldRhinoObject);
			ScheduleIfLinked(args.NewRhinoObject);
		}
	}

	private static void ScheduleIfLinked(RhinoObject source)
	{
		if (source != null && source.Attributes != null && !(source.Attributes.GetUserString("ExplodeBook.Generated") == "1") && !(source.Attributes.GetUserString("ExplodeBook.LinkedSource") != "1"))
		{
			Schedule(source.Document);
		}
	}

    private static void OnClose(object sender, DocumentEventArgs e){Pending.Remove(e.Document.RuntimeSerialNumber);Due.Remove(e.Document.RuntimeSerialNumber);PathDiagnostics.Close(e.Document);}
    private static void OnSave(object sender, DocumentSaveEventArgs e)=>PathDiagnostics.Restore(e.Document);

	private static void OnIdle(object sender, EventArgs args)
	{
		if (_processing || _suppressionDepth > 0 || Pending.Count == 0)
		{
			return;
		}
		if (Rhino.Commands.Command.InCommand()) return;
        List<RhinoDoc> list = Pending.Where(pair=>Due.TryGetValue(pair.Key,out var when)&&DateTime.UtcNow>=when).Select(pair=>pair.Value).ToList();
        if (list.Count==0) return;
        foreach(var document in list){Pending.Remove(document.RuntimeSerialNumber);Due.Remove(document.RuntimeSerialNumber);}
		_processing = true;
		try
		{
			foreach (RhinoDoc item in list)
			{
				if (item != null)
				{
					ExplodeSettings explodeSettings = new ExplodeSettings();
					if (!LinkedBookManager.TryLoad(item, explodeSettings, out var sources, out var generateOverview, out var generatePages))
					{
						ExplodeBookEngine.ClearGenerated(item);
					}
					else if (explodeSettings.AutoUpdate)
					{
						AssemblyAnalysis analysis;
						GeneratedBook generatedBook = ExplodeBookEngine.Execute(item, sources, explodeSettings, generateOverview, generatePages, linkSources: false, out analysis);
						if (generatedBook.PartCount>0) RhinoApp.WriteLine("ExplodeBook：原模型已改变，已自动更新 {0} 个关联零件和 {1} 张说明页。", generatedBook.PartCount, generatedBook.LayoutNames.Count);
                        else foreach(var error in analysis.ValidationErrors) RhinoApp.WriteLine("自动更新暂停："+error);
					}
				}
			}
		}
		catch (Exception ex)
		{
			RhinoApp.WriteLine("ExplodeBook 自动更新失败：" + ex.Message + "。可运行 EBUpdate 手动重建。");
		}
		finally
		{
			_processing = false;
		}
	}
}
