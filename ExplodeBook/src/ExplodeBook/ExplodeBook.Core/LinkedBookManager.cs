using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace ExplodeBook.Core;

internal static class LinkedBookManager
{
	public static int LinkSources(RhinoDoc doc, IEnumerable<RhinoObject> sources, ExplodeSettings settings, bool generateOverview, bool generatePages)
	{
		if (doc == null || sources == null)
		{
			return 0;
		}
		List<RhinoObject> objects = (from item in sources
			where item != null && item.Geometry != null
			where item.Attributes.GetUserString("ExplodeBook.Generated") != "1"
			group item by item.Id into @group
			select @group.First()).ToList();
		using (AutoUpdateController.Suppress())
		{
			foreach (RhinoObject item in from item in LinkedSources(doc)
				where objects.All((RhinoObject source) => source.Id != item.Id)
				select item)
			{
				RemoveLink(doc, item);
			}
			string value = Serialize(settings);
			foreach (RhinoObject item2 in objects)
			{
				ObjectAttributes objectAttributes = item2.Attributes.Duplicate();
				objectAttributes.SetUserString("ExplodeBook.LinkedSource", "1");
				objectAttributes.SetUserString("ExplodeBook.Settings", value);
				objectAttributes.SetUserString("ExplodeBook.GenerateOverview", generateOverview ? "1" : "0");
				objectAttributes.SetUserString("ExplodeBook.GeneratePages", generatePages ? "1" : "0");
				doc.Objects.ModifyAttributes(item2.Id, objectAttributes, quiet: true);
			}
		}
		return objects.Count;
	}

	public static List<RhinoObject> LinkedSources(RhinoDoc doc)
	{
		if (doc == null)
		{
			return new List<RhinoObject>();
		}
		return (from item in doc.Objects.GetObjectList(ObjectType.AnyObject)
			where item != null && item.Geometry != null
			where !item.IsDeleted
			where item.Attributes.GetUserString("ExplodeBook.LinkedSource") == "1"
			where item.Attributes.GetUserString("ExplodeBook.Generated") != "1"
			select item).ToList();
	}

	public static bool TryLoad(RhinoDoc doc, ExplodeSettings settings, out List<RhinoObject> sources, out bool generateOverview, out bool generatePages)
	{
		sources = LinkedSources(doc);
		generateOverview = true;
		generatePages = true;
		if (sources.Count == 0)
		{
			return false;
		}
		RhinoObject rhinoObject = sources[0];
		Deserialize(rhinoObject.Attributes.GetUserString("ExplodeBook.Settings"), settings);
		generateOverview = rhinoObject.Attributes.GetUserString("ExplodeBook.GenerateOverview") != "0";
		generatePages = rhinoObject.Attributes.GetUserString("ExplodeBook.GeneratePages") != "0";
		return true;
	}

	public static void SaveSettings(RhinoDoc doc, ExplodeSettings settings)
	{
		string value = Serialize(settings);
		using (AutoUpdateController.Suppress())
		{
			foreach (RhinoObject item in LinkedSources(doc))
			{
				ObjectAttributes objectAttributes = item.Attributes.Duplicate();
				objectAttributes.SetUserString("ExplodeBook.Settings", value);
				doc.Objects.ModifyAttributes(item.Id, objectAttributes, quiet: true);
			}
		}
	}

	public static int UnlinkAll(RhinoDoc doc)
	{
		List<RhinoObject> list = LinkedSources(doc);
		using (AutoUpdateController.Suppress())
		{
			foreach (RhinoObject item in list)
			{
				RemoveLink(doc, item);
			}
		}
		return list.Count;
	}

	public static int SetMetadata(RhinoDoc doc, IEnumerable<RhinoObject> selection, string key, string value, bool clearSameKeyOnOtherLinkedSources)
	{
		if (doc == null || selection == null)
		{
			return 0;
		}
		List<List<RhinoObject>> source = AssemblyAnalyzer.BuildGroupedComponents(selection);
		HashSet<Guid> selectedIds = new HashSet<Guid>(from item in source.SelectMany((List<RhinoObject> item) => item)
			select item.Id);
		int num = 0;
		using (AutoUpdateController.Suppress())
		{
			if (clearSameKeyOnOtherLinkedSources)
			{
				foreach (RhinoObject item in from item in doc.Objects.GetObjectList(ObjectType.AnyObject)
                    where !item.IsDeleted && !item.IsInstanceDefinitionGeometry
                    where item.Attributes.GetUserString(AssemblyAnalyzer.GeneratedKey) != "1"
					where !selectedIds.Contains(item.Id) && item.Attributes.GetUserString(key) != null
					select item)
				{
					ObjectAttributes objectAttributes = item.Attributes.Duplicate();
					objectAttributes.DeleteUserString(key);
					doc.Objects.ModifyAttributes(item.Id, objectAttributes, quiet: true);
				}
			}
			foreach (RhinoObject item2 in from item in source.SelectMany((List<RhinoObject> item) => item)
				group item by item.Id into item
				select item.First())
			{
				ObjectAttributes objectAttributes2 = item2.Attributes.Duplicate();
				if (string.IsNullOrEmpty(value))
				{
					objectAttributes2.DeleteUserString(key);
				}
				else
				{
					objectAttributes2.SetUserString(key, value);
				}
				if (doc.Objects.ModifyAttributes(item2.Id, objectAttributes2, quiet: true))
				{
					num++;
				}
			}
		}
		AutoUpdateController.Schedule(doc);
		return num;
	}

	private static void RemoveLink(RhinoDoc doc, RhinoObject source)
	{
		ObjectAttributes objectAttributes = source.Attributes.Duplicate();
		objectAttributes.DeleteUserString("ExplodeBook.LinkedSource");
		objectAttributes.DeleteUserString("ExplodeBook.Settings");
		objectAttributes.DeleteUserString("ExplodeBook.GenerateOverview");
		objectAttributes.DeleteUserString("ExplodeBook.GeneratePages");
		doc.Objects.ModifyAttributes(source.Id, objectAttributes, quiet: true);
	}

	public static string Serialize(ExplodeSettings settings)
	{
		string[] value = new string[27]
		{
			((int)settings.Mode).ToString(CultureInfo.InvariantCulture),
			((int)settings.PageKind).ToString(CultureInfo.InvariantCulture),
			settings.Landscape ? "1" : "0",
			Format(settings.CustomPageWidthMillimeters),
			Format(settings.CustomPageHeightMillimeters),
			Format(settings.ExplodeDistanceMillimeters),
			Format(settings.ArrowHeadMillimeters),
			Format(settings.PageGapMillimeters),
			settings.MaximumStepPages.ToString(CultureInfo.InvariantCulture),
			settings.PdfDpi.ToString(CultureInfo.InvariantCulture),
			settings.AutoUpdate ? "1" : "0",
			settings.CreateVectorLinework ? "1" : "0",
			settings.IncludeCover ? "1" : "0",
			settings.IncludePartsList ? "1" : "0",
			settings.IncludeCompletionPage ? "1" : "0",
			settings.IncludeDetailInset ? "1" : "0",
			Uri.EscapeDataString(settings.ProductTitle ?? string.Empty),
			settings.IncludeModuleOverview ? "1" : "0",
			settings.IncludeModuleAssemblyPages ? "1" : "0",
			settings.IncludeModulePartPages ? "1" : "0",
			settings.ParallelViewLocked ? "1" : "0",
			Format(settings.ViewDirection.X),
			Format(settings.ViewDirection.Y),
			Format(settings.ViewDirection.Z),
			Format(settings.ViewUp.X),
			Format(settings.ViewUp.Y),
			Format(settings.ViewUp.Z)
		};
		return string.Join("|", value) + "|" + Format(settings.MeshToleranceMillimeters) + "|" + settings.AnalysisTimeoutSeconds;
	}

	public static void Deserialize(string raw, ExplodeSettings settings)
	{
		if (settings == null || string.IsNullOrWhiteSpace(raw))
		{
			return;
		}
		string[] array = raw.Split('|');
		if (TryInt(array, 0, out var value) && Enum.IsDefined(typeof(ExplodeMode), value))
		{
			settings.Mode = (ExplodeMode)value;
		}
		if (TryInt(array, 1, out value) && Enum.IsDefined(typeof(ManualPageKind), value))
		{
			settings.PageKind = (ManualPageKind)value;
		}
		if (array.Length > 2)
		{
			settings.Landscape = array[2] == "1";
		}
		if (TryDouble(array, 3, out var value2))
		{
			settings.CustomPageWidthMillimeters = value2;
		}
		if (TryDouble(array, 4, out value2))
		{
			settings.CustomPageHeightMillimeters = value2;
		}
		if (TryDouble(array, 5, out value2))
		{
			settings.ExplodeDistanceMillimeters = value2;
		}
		if (TryDouble(array, 6, out value2))
		{
			settings.ArrowHeadMillimeters = value2;
		}
		if (TryDouble(array, 7, out value2))
		{
			settings.PageGapMillimeters = value2;
		}
		if (TryInt(array, 8, out value))
		{
			settings.MaximumStepPages = value;
		}
		if (TryInt(array, 9, out value))
		{
			settings.PdfDpi = value;
		}
		if (array.Length > 10)
		{
			settings.AutoUpdate = array[10] == "1";
		}
		if (array.Length > 11)
		{
			settings.CreateVectorLinework = array[11] == "1";
		}
		if (array.Length > 12)
		{
			settings.IncludeCover = array[12] == "1";
		}
		if (array.Length > 13)
		{
			settings.IncludePartsList = array[13] == "1";
		}
		if (array.Length > 14)
		{
			settings.IncludeCompletionPage = array[14] == "1";
		}
		if (array.Length > 15)
		{
			settings.IncludeDetailInset = array[15] == "1";
		}
		if (array.Length > 16)
		{
			settings.ProductTitle = Uri.UnescapeDataString(array[16]);
		}
		if (array.Length > 17)
		{
			settings.IncludeModuleOverview = array[17] == "1";
		}
		if (array.Length > 18)
		{
			settings.IncludeModuleAssemblyPages = array[18] == "1";
		}
		if (array.Length > 19)
		{
			settings.IncludeModulePartPages = array[19] == "1";
		}
		if (array.Length > 20)
		{
			settings.ParallelViewLocked = array[20] == "1";
		}
		if (TryDouble(array, 21, out var value3) && TryDouble(array, 22, out var value4) && TryDouble(array, 23, out var value5))
		{
			Vector3d viewDirection = new Vector3d(value3, value4, value5);
			if (viewDirection.IsValid && viewDirection.Unitize())
			{
				settings.ViewDirection = viewDirection;
			}
		}
		if (TryDouble(array, 24, out value3) && TryDouble(array, 25, out value4) && TryDouble(array, 26, out value5))
		{
			Vector3d viewUp = new Vector3d(value3, value4, value5);
			if (viewUp.IsValid && viewUp.Unitize())
			{
				settings.ViewUp = viewUp;
			}
		}
        if (TryDouble(array, 27, out var meshTolerance) && meshTolerance > 0 && meshTolerance <= 1) settings.MeshToleranceMillimeters = meshTolerance;
        if (TryInt(array, 28, out var timeout) && timeout >= 5) settings.AnalysisTimeoutSeconds = timeout;
	}

	private static string Format(double value)
	{
		return value.ToString("R", CultureInfo.InvariantCulture);
	}

	private static bool TryInt(string[] values, int index, out int value)
	{
		value = 0;
		if (values.Length > index)
		{
			return int.TryParse(values[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
		}
		return false;
	}

	private static bool TryDouble(string[] values, int index, out double value)
	{
		value = 0.0;
		if (values.Length > index)
		{
			return double.TryParse(values[index], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
		}
		return false;
	}
}
