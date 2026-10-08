using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace ExplodeBook.Core;

internal static class AssemblyAnalyzer
{
	public const string GeneratedKey = "ExplodeBook.Generated";

	public const string GeneratedValue = "1";

	public const string OrderKey = "ExplodeBook.Order";

	public const string PartNumberKey = "ExplodeBook.PartNumber";

	public const string WoodExportPartNumberKey = "WoodExport.PartNumber";

	public const string LinkedKey = "ExplodeBook.LinkedSource";

	public const string SettingsKey = "ExplodeBook.Settings";

	public const string GenerateOverviewKey = "ExplodeBook.GenerateOverview";

	public const string GeneratePagesKey = "ExplodeBook.GeneratePages";

	public const string DirectionKey = "ExplodeBook.Direction";

	public const string ExactOffsetKey = "ExplodeBook.ExactOffset";

	public const string LabelOffsetKey = "ExplodeBook.LabelOffset";

	public const string ArrowAnchorKey = "ExplodeBook.ArrowAnchor";

	public const string SubassemblyKey = "ExplodeBook.Subassembly";

	public const string ModuleOrderKey = "ExplodeBook.ModuleOrder";

	public const string ModulePartOrderKey = "ExplodeBook.ModulePartOrder";

	public const string ForcedBaseKey = "ExplodeBook.ForcedBase";

	public static AssemblyAnalysis Analyze(RhinoDoc doc, IEnumerable<RhinoObject> selection, ExplodeSettings settings)
	{
		AssemblyAnalysis assemblyAnalysis = new AssemblyAnalysis();
		if (doc == null || selection == null)
		{
			return assemblyAnalysis;
		}
		settings.ModelUnitsPerMillimeter = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem);
		if (!IsFinitePositive(settings.ModelUnitsPerMillimeter))
		{
			settings.ModelUnitsPerMillimeter = 1.0;
			assemblyAnalysis.Warnings.Add("文档单位无效，说明书尺寸暂按毫米计算。建议先把 Rhino 文档单位设为 mm。");
		}
		var resolved = PartResolver.Resolve(selection, out var resolverWarnings);
        assemblyAnalysis.Warnings.AddRange(resolverWarnings);
        List<List<RhinoObject>> list = resolved;
		int num = 0;
		foreach (List<RhinoObject> item in list)
		{
			BoundingBox boundingBox = CombinedBounds(item);
			if (!boundingBox.IsValid)
			{
                assemblyAnalysis.ValidationErrors.Add("第 " + (num + 1) + " 个零件边界无效；请先修复实体。");
				continue;
			}
			num++;
			int assemblyOrder = ReadOrder(item);
			string text = ReadPartNumber(item);
			string text2 = item.Select((RhinoObject item) => item.Attributes.Name).FirstOrDefault((string item) => !string.IsNullOrWhiteSpace(item));
			Vector3d diagonal = boundingBox.Diagonal;
			double sizeScore = Math.Max(Math.Abs(diagonal.X * diagonal.Y * diagonal.Z), Math.Max(Math.Abs(diagonal.X * diagonal.Y), Math.Max(Math.Abs(diagonal.X * diagonal.Z), Math.Abs(diagonal.Y * diagonal.Z))));
			Vector3d vector;
			Vector3d vector2;
			Vector3d vector3;
			Vector3d vector4;
			AssemblyPart assemblyPart = new AssemblyPart
			{
				Sequence = num,
				AssemblyOrder = assemblyOrder,
				PartNumber = (string.IsNullOrWhiteSpace(text) ? ("B-" + num.ToString("00", CultureInfo.InvariantCulture)) : text),
				Name = (string.IsNullOrWhiteSpace(text2) ? ("零件_" + num.ToString("00")) : text2),
				Subassembly = ReadString(item, "ExplodeBook.Subassembly"),
				ModuleOrder = ReadPositiveInteger(item, "ExplodeBook.ModuleOrder"),
				ModulePartOrder = ReadPositiveInteger(item, "ExplodeBook.ModulePartOrder"),
				IsForcedBase = (ReadString(item, "ExplodeBook.ForcedBase") == "1"),
				HasCustomDirection = TryReadVector(item, "ExplodeBook.Direction", out vector),
				CustomDirection = vector,
				HasExactExplosionOffset = TryReadVector(item, "ExplodeBook.ExactOffset", out vector2),
				ExactExplosionOffset = vector2,
				HasLabelOffset = TryReadVector(item, "ExplodeBook.LabelOffset", out vector3),
				LabelOffset = vector3,
				HasArrowAnchorOffset = TryReadVector(item, "ExplodeBook.ArrowAnchor", out vector4),
				ArrowAnchorOffset = vector4,
				Bounds = boundingBox,
				SizeScore = sizeScore
			};
			assemblyPart.Objects.AddRange(item);
			assemblyAnalysis.Parts.Add(assemblyPart);
			assemblyAnalysis.Bounds = (assemblyAnalysis.Bounds.IsValid ? BoundingBox.Union(assemblyAnalysis.Bounds, boundingBox) : boundingBox);
		}
        if (assemblyAnalysis.Parts.Count == 0)
        {
            assemblyAnalysis.ValidationErrors.Add("没有可分析的独立实体；请选择原装配模型的封闭板件。");
            return assemblyAnalysis;
		}
		foreach (IGrouping<string, AssemblyPart> item2 in assemblyAnalysis.Parts.GroupBy((AssemblyPart item) => item.PartNumber, StringComparer.OrdinalIgnoreCase))
		{
			int num2 = item2.Count();
			assemblyAnalysis.QuantityByPartNumber[item2.Key] = num2;
			foreach (AssemblyPart item3 in item2)
			{
				item3.Quantity = num2;
			}
		}
		IntegratedAssemblyPlanner.Apply(doc, assemblyAnalysis, settings);
		if (assemblyAnalysis.Parts.Count > settings.MaximumStepPages)
		{
			assemblyAnalysis.Warnings.Add($"识别到 {assemblyAnalysis.Parts.Count} 个装配单元；步骤上限为 {settings.MaximumStepPages}；正式出图会停止，请先提高上限。");
		}
		return assemblyAnalysis;
	}

    public static List<List<RhinoObject>> BuildGroupedComponents(IEnumerable<RhinoObject> sourceObjects)
    { return PartResolver.Resolve(sourceObjects, out _); }

	public static int SetManualOrder(RhinoDoc doc, IEnumerable<RhinoObject> orderedSelection)
	{
		if (doc == null || orderedSelection == null)
		{
			return 0;
		}
		List<List<RhinoObject>> list = BuildGroupedComponents(orderedSelection);
		int num = 0;
		foreach (List<RhinoObject> item in list)
		{
			num++;
			string text = ReadPartNumber(item);
			string value = (string.IsNullOrWhiteSpace(text) ? ("B-" + num.ToString("00", CultureInfo.InvariantCulture)) : text);
			foreach (RhinoObject item2 in item)
			{
				ObjectAttributes objectAttributes = item2.Attributes.Duplicate();
				objectAttributes.SetUserString("ExplodeBook.Order", num.ToString(CultureInfo.InvariantCulture));
				objectAttributes.SetUserString("ExplodeBook.PartNumber", value);
				doc.Objects.ModifyAttributes(item2.Id, objectAttributes, quiet: true);
			}
		}
		return num;
	}

	public static int ClearManualOrder(RhinoDoc doc, IEnumerable<RhinoObject> selection)
	{
		if (doc == null || selection == null)
		{
			return 0;
		}
		int num = 0;
		foreach (RhinoObject item in from item in selection
			where item != null
			group item by item.Id into @group
			select @group.First())
		{
			ObjectAttributes objectAttributes = item.Attributes.Duplicate();
			objectAttributes.DeleteUserString("ExplodeBook.Order");
            objectAttributes.DeleteUserString("ExplodeBook.ModuleOrder");
            objectAttributes.DeleteUserString("ExplodeBook.ModulePartOrder");
			if (doc.Objects.ModifyAttributes(item.Id, objectAttributes, quiet: true))
			{
				num++;
			}
		}
		return num;
	}

	public static int SetModuleOrder(RhinoDoc doc, IEnumerable<RhinoObject> orderedSelection, out List<string> orderedModules)
	{
		orderedModules = new List<string>();
		if (doc == null || orderedSelection == null)
		{
			return 0;
		}
        var orderedObjects = orderedSelection.Where(o => o != null).ToList();
        var selectedIds = new HashSet<Guid>(orderedObjects.Select(o => o.Id));
		foreach (List<RhinoObject> item in BuildGroupedComponents(orderedObjects))
		{
			string text = NormalizeModuleName(ReadString(item, "ExplodeBook.Subassembly"));
			if (!orderedModules.Contains(text, StringComparer.OrdinalIgnoreCase))
			{
				orderedModules.Add(text);
			}
		}
		if (orderedModules.Count == 0)
		{
			return 0;
		}
		Dictionary<string, int> dictionary = orderedModules.Select((string name, int index) => new
		{
			name = name,
			order = index + 1
		}).ToDictionary(item => item.name, item => item.order, StringComparer.OrdinalIgnoreCase);
		int num = 0;
		using (AutoUpdateController.Suppress())
		{
			// Modules are defined before the first successful book. Restricting
			// this to linked sources silently wrote zero ranks in that workflow.
			foreach (RhinoObject item2 in doc.Objects.GetObjectList(ObjectType.AnyObject)
                .Where(o => !o.IsDeleted && !o.IsInstanceDefinitionGeometry &&
                    o.Attributes.GetUserString(GeneratedKey) != GeneratedValue &&
                    o.Attributes.GetUserString("WoodSheetLayout.FlatCopy") != "1"))
			{
				string key = NormalizeModuleName(item2.Attributes.GetUserString("ExplodeBook.Subassembly"));
				if ((!string.IsNullOrWhiteSpace(item2.Attributes.GetUserString(SubassemblyKey)) ||
                    selectedIds.Contains(item2.Id)) && dictionary.TryGetValue(key, out var value))
				{
					ObjectAttributes objectAttributes = item2.Attributes.Duplicate();
					objectAttributes.SetUserString("ExplodeBook.ModuleOrder", value.ToString(CultureInfo.InvariantCulture));
					if (doc.Objects.ModifyAttributes(item2.Id, objectAttributes, quiet: true))
					{
						num++;
					}
				}
			}
		}
		AutoUpdateController.Schedule(doc);
		return num;
	}

	public static int SetModulePartOrder(RhinoDoc doc, IEnumerable<RhinoObject> orderedSelection, out string moduleName)
	{
		moduleName = null;
		if (doc == null || orderedSelection == null)
		{
			return 0;
		}
		List<List<RhinoObject>> list = BuildGroupedComponents(orderedSelection);
		List<string> list2 = list.Select((List<RhinoObject> item) => NormalizeModuleName(ReadString(item, "ExplodeBook.Subassembly"))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		if (list2.Count != 1)
		{
			return 0;
		}
		moduleName = list2[0];
		int num = 0;
		using (AutoUpdateController.Suppress())
		{
			for (int num2 = 0; num2 < list.Count; num2++)
			{
				foreach (RhinoObject item in list[num2])
				{
					ObjectAttributes objectAttributes = item.Attributes.Duplicate();
					objectAttributes.SetUserString("ExplodeBook.ModulePartOrder", (num2 + 1).ToString(CultureInfo.InvariantCulture));
					if (doc.Objects.ModifyAttributes(item.Id, objectAttributes, quiet: true))
					{
						num++;
					}
				}
			}
		}
		AutoUpdateController.Schedule(doc);
		return num;
	}

	public static int AutoAssignModules(RhinoDoc doc, IEnumerable<RhinoObject> selection, out int moduleCount)
	{
		moduleCount = 0;
		if (doc == null || selection == null)
		{
			return 0;
		}
		List<List<RhinoObject>> list = BuildGroupedComponents(selection);
		if (list.Count == 0)
		{
			return 0;
		}
		List<string> list2 = list.Select((List<RhinoObject> component) => SuggestedModuleFromLayer(doc, component)).ToList();
		if (list2.Where((string item) => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
			.Count < 2)
		{
			for (int num = 0; num < list2.Count; num++)
			{
				list2[num] = "主体模块";
			}
		}
		moduleCount = list2.Distinct(StringComparer.OrdinalIgnoreCase).Count();
		int num2 = 0;
		using (AutoUpdateController.Suppress())
		{
			for (int num3 = 0; num3 < list.Count; num3++)
			{
				foreach (RhinoObject item in list[num3])
				{
					ObjectAttributes objectAttributes = item.Attributes.Duplicate();
					objectAttributes.SetUserString("ExplodeBook.Subassembly", list2[num3]);
					objectAttributes.DeleteUserString("ExplodeBook.ModuleOrder");
					objectAttributes.DeleteUserString("ExplodeBook.ModulePartOrder");
					if (doc.Objects.ModifyAttributes(item.Id, objectAttributes, quiet: true))
					{
						num2++;
					}
				}
			}
		}
		AutoUpdateController.Schedule(doc);
		return num2;
	}





	private static int ReadPositiveInteger(IEnumerable<RhinoObject> objects, string key)
	{
		foreach (RhinoObject @object in objects)
		{
			if (int.TryParse(@object.Attributes.GetUserString(key), out var result) && result > 0)
			{
				return result;
			}
		}
		return 0;
	}

	private static string NormalizeModuleName(string value)
	{
		if (!string.IsNullOrWhiteSpace(value))
		{
			return value.Trim();
		}
		return "主体模块";
	}

	private static string SuggestedModuleFromLayer(RhinoDoc doc, IList<RhinoObject> component)
	{
		foreach (RhinoObject item in component)
		{
			if (item.Attributes.LayerIndex < 0 || item.Attributes.LayerIndex >= doc.Layers.Count)
			{
				continue;
			}
			Layer layer = doc.Layers[item.Attributes.LayerIndex];
			if (layer == null)
			{
				continue;
			}
			string text = layer.FullPath ?? layer.Name;
			if (!string.IsNullOrWhiteSpace(text))
			{
				string[] array = text.Split(new string[1] { "::" }, StringSplitOptions.RemoveEmptyEntries);
				if (array.Length > 1 && !string.IsNullOrWhiteSpace(array[0]))
				{
					return array[0].Trim();
				}
			}
		}
		return null;
	}

	private static int ReadOrder(IEnumerable<RhinoObject> objects)
	{
		foreach (RhinoObject @object in objects)
		{
			if (int.TryParse(@object.Attributes.GetUserString("ExplodeBook.Order"), out var result) && result > 0)
			{
				return result;
			}
		}
		return 0;
	}

	private static string ReadPartNumber(IEnumerable<RhinoObject> objects)
	{
		foreach (RhinoObject @object in objects)
		{
			string userString = @object.Attributes.GetUserString("WoodExport.PartNumber");
			if (!string.IsNullOrWhiteSpace(userString))
			{
				return userString;
			}
			userString = @object.Attributes.GetUserString("ExplodeBook.PartNumber");
			if (!string.IsNullOrWhiteSpace(userString))
			{
				return userString;
			}
		}
		return null;
	}

	public static string ReadString(IEnumerable<RhinoObject> objects, string key)
	{
		foreach (RhinoObject @object in objects)
		{
			string userString = @object.Attributes.GetUserString(key);
			if (!string.IsNullOrWhiteSpace(userString))
			{
				return userString;
			}
		}
		return null;
	}

	public static bool TryReadVector(IEnumerable<RhinoObject> objects, string key, out Vector3d vector)
	{
		vector = Vector3d.Unset;
		string text = ReadString(objects, key);
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		string[] array = text.Split(',');
		if (array.Length != 3 || !double.TryParse(array[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var result) || !double.TryParse(array[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var result2) || !double.TryParse(array[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var result3))
		{
			return false;
		}
		vector = new Vector3d(result, result2, result3);
		if (vector.IsValid)
		{
			return !vector.IsTiny();
		}
		return false;
	}

	public static string VectorToString(Vector3d vector)
	{
		return string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R}", vector.X, vector.Y, vector.Z);
	}

	private static BoundingBox CombinedBounds(IEnumerable<RhinoObject> objects)
	{
		BoundingBox boundingBox = BoundingBox.Unset;
		foreach (RhinoObject @object in objects)
		{
			BoundingBox boundingBox2 = @object.Geometry.GetBoundingBox(accurate: true);
			if (boundingBox2.IsValid)
			{
				boundingBox = (boundingBox.IsValid ? BoundingBox.Union(boundingBox, boundingBox2) : boundingBox2);
			}
		}
		return boundingBox;
	}

	private static bool IsFinitePositive(double value)
	{
		if (value > 0.0 && !double.IsNaN(value))
		{
			return !double.IsInfinity(value);
		}
		return false;
	}

	private static int Find(int[] parent, int index)
	{
		while (parent[index] != index)
		{
			parent[index] = parent[parent[index]];
			index = parent[index];
		}
		return index;
	}

	private static void Union(int[] parent, int left, int right)
	{
		int num = Find(parent, left);
		int num2 = Find(parent, right);
		if (num != num2)
		{
			parent[num2] = num;
		}
	}
}
