using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace ExplodeBook.Core;

internal static class ManualPageBuilder
{
	public static List<PageZone> CreatePages(RhinoDoc doc, AssemblyAnalysis analysis, ExplodeSettings settings, ICollection<Guid> generatedIds)
	{
		List<PageZone> list = new List<PageZone>();
		double startX = analysis.Bounds.Max.X + Math.Max(analysis.Bounds.Diagonal.X * 3.0, 150.0 * settings.ModelUnitsPerMillimeter);
		double y = analysis.Bounds.Min.Y;
		int pageIndex = 0;
		if (settings.IncludeCover)
		{
			list.Add(CreateCoverPage(doc, analysis, settings, PageOrigin(startX, y, pageIndex++, settings), list.Count, generatedIds));
		}
		if (settings.IncludePartsList)
		{
			foreach (PageZone item in CreatePartsListPages(doc, analysis, settings, startX, y, ref pageIndex, list.Count, generatedIds))
			{
				list.Add(item);
			}
		}
		if (settings.IncludeModuleOverview)
		{
			list.Add(CreateOverviewPage(doc, analysis, settings, PageOrigin(startX, y, pageIndex++, settings), list.Count, generatedIds));
		}

		int maximumStepPages = settings.MaximumStepPages;
		foreach (AssemblyModule module2 in analysis.Modules)
		{
			list.Add(CreateSubassemblyPage(doc, analysis, module2, settings, PageOrigin(startX, y, pageIndex++, settings), list.Count, generatedIds));
			if (!settings.IncludeModulePartPages || maximumStepPages <= 0)
			{
				continue;
			}
			foreach (AssemblyPart item2 in module2.Parts.OrderBy((AssemblyPart item) => item.ModulePartOrder))
			{
				if (maximumStepPages-- <= 0)
				{
					break;
				}
				list.Add(CreateStepPage(doc, analysis, module2, item2, settings, PageOrigin(startX, y, pageIndex++, settings), list.Count, generatedIds));
			}
		}
		if (settings.IncludeModuleAssemblyPages)
		{
			foreach (AssemblyModule module in analysis.Modules)
			{
				list.Add(CreateModuleAssemblyPage(doc, analysis, module, settings, PageOrigin(startX, y, pageIndex++, settings), list.Count, generatedIds));
			}
		}
		if (settings.IncludeCompletionPage)
		{
			list.Add(CreateCompletionPage(doc, analysis, settings, PageOrigin(startX, y, pageIndex++, settings), list.Count, generatedIds));
		}
		foreach (PageZone item3 in list)
		{
            if (AnalysisCancellation.Check()) throw new OperationCanceledException();
			CreateLayout(doc, item3, settings);
		}
		return list;
	}

	private static PageZone CreateCoverPage(RhinoDoc doc, AssemblyAnalysis analysis, ExplodeSettings settings, Point2d origin, int pageIndex, ICollection<Guid> generatedIds)
	{
		BoundingBox boundingBox = PageBounds(origin, settings);
		int groupIndex = NewPageGroup(doc, pageIndex, "封面");
		AddId(generatedIds, DrawingBuilder.AddPageFrame(doc, boundingBox, groupIndex));
		Point3d center = boundingBox.Center;
		AddId(generatedIds, DrawingBuilder.AddPageText(doc, settings.ProductTitle, new Point3d(center.X, boundingBox.Max.Y - 34.0 * settings.ModelUnitsPerMillimeter, 0.0), 11.0 * settings.ModelUnitsPerMillimeter, groupIndex, TextJustification.MiddleCenter));
		AddId(generatedIds, DrawingBuilder.AddPageText(doc, "ASSEMBLY MANUAL / 木质拼装说明书", new Point3d(center.X, boundingBox.Max.Y - 49.0 * settings.ModelUnitsPerMillimeter, 0.0), 5.0 * settings.ModelUnitsPerMillimeter, groupIndex, TextJustification.MiddleCenter));
		Transform orientation = ViewProjectionBuilder.Orientation(settings);
		List<ProjectedPart> projected = analysis.Parts.Select((AssemblyPart part) => new ProjectedPart
		{
			Part = part,
			Transform = orientation
		}).ToList();
		BoundingBox target = new BoundingBox(new Point3d(boundingBox.Min.X + 24.0 * settings.ModelUnitsPerMillimeter, boundingBox.Min.Y + 35.0 * settings.ModelUnitsPerMillimeter, 0.0), new Point3d(boundingBox.Max.X - 24.0 * settings.ModelUnitsPerMillimeter, boundingBox.Max.Y - 60.0 * settings.ModelUnitsPerMillimeter, 0.0));
		Transform fit = FitToBox(BoundsOf(projected), target);
		AddVisual(doc, projected, fit, settings, groupIndex, highlight: false, generatedIds, "封面模型");
		AddId(generatedIds, DrawingBuilder.AddPageText(doc, string.Format(CultureInfo.InvariantCulture, "装配单元：{0}    不同零件：{1}", analysis.Parts.Count, analysis.QuantityByPartNumber.Count), new Point3d(center.X, boundingBox.Min.Y + 18.0 * settings.ModelUnitsPerMillimeter, 0.0), 4.0 * settings.ModelUnitsPerMillimeter, groupIndex, TextJustification.MiddleCenter));
		return NewZone(pageIndex, "封面", boundingBox, settings.ProductTitle);
	}

	private static List<PageZone> CreatePartsListPages(RhinoDoc doc, AssemblyAnalysis analysis, ExplodeSettings settings, double startX, double startY, ref int pageIndex, int logicalStart, ICollection<Guid> generatedIds)
	{
		List<PageZone> list = new List<PageZone>();
		var list2 = (from @group in analysis.Parts.GroupBy((AssemblyPart item) => item.PartNumber, StringComparer.OrdinalIgnoreCase)
			select new
			{
				Number = @group.Key,
				Name = @group.First().Name,
				Quantity = @group.Count(),
				Subassembly = (@group.Select((AssemblyPart item) => item.Subassembly).FirstOrDefault((string item) => !string.IsNullOrWhiteSpace(item)) ?? "—")
			}).OrderBy(item => item.Number, StringComparer.OrdinalIgnoreCase).ToList();
		int num = Math.Max(8, (int)Math.Floor(settings.PageHeightMillimeters / 7.0) - 10);
		int num2 = Math.Max(1, (int)Math.Ceiling((double)list2.Count / (double)num));
		for (int num3 = 0; num3 < num2; num3++)
		{
			BoundingBox boundingBox = PageBounds(PageOrigin(startX, startY, pageIndex++, settings), settings);
			int pageIndex2 = logicalStart + num3;
			int groupIndex = NewPageGroup(doc, pageIndex2, "零件清单");
			AddId(generatedIds, DrawingBuilder.AddPageFrame(doc, boundingBox, groupIndex));
			AddPageHeadings(doc, settings, boundingBox, groupIndex, "PARTS LIST / 零件清单 " + (num3 + 1) + "/" + num2, "编号与 WoodExport 保持一致；数量为相同编号在装配体中的总数。", generatedIds);
			double x = boundingBox.Min.X + 14.0 * settings.ModelUnitsPerMillimeter;
			double num4 = boundingBox.Max.Y - 31.0 * settings.ModelUnitsPerMillimeter;
			AddId(generatedIds, DrawingBuilder.AddPageText(doc, "编号                 数量     子装配                 零件名称", new Point3d(x, num4, 0.0), 3.8 * settings.ModelUnitsPerMillimeter, groupIndex, TextJustification.MiddleLeft));
			num4 -= 7.0 * settings.ModelUnitsPerMillimeter;
			foreach (var item in list2.Skip(num3 * num).Take(num))
			{
				string text = string.Format(CultureInfo.InvariantCulture, "{0,-18}  ×{1,-4}   {2,-18}   {3}", item.Number, item.Quantity, Trim(item.Subassembly, 16), Trim(item.Name, 30));
				AddId(generatedIds, DrawingBuilder.AddPageText(doc, text, new Point3d(x, num4, 0.0), 3.4 * settings.ModelUnitsPerMillimeter, groupIndex, TextJustification.MiddleLeft));
				num4 -= 6.0 * settings.ModelUnitsPerMillimeter;
			}
			list.Add(NewZone(pageIndex2, "零件清单_" + (num3 + 1), boundingBox, "零件清单"));
		}
		return list;
	}

	private static PageZone CreateOverviewPage(RhinoDoc doc, AssemblyAnalysis analysis, ExplodeSettings settings, Point2d origin, int pageIndex, ICollection<Guid> generatedIds)
	{
		BoundingBox boundingBox = PageBounds(origin, settings);
		int groupIndex = NewPageGroup(doc, pageIndex, "装配总览");
		AddId(generatedIds, DrawingBuilder.AddPageFrame(doc, boundingBox, groupIndex));
		Transform orientation = ViewProjectionBuilder.Orientation(settings);
		List<ProjectedPart> list = new List<ProjectedPart>();
		foreach (AssemblyModule module in analysis.Modules)
		{
			Vector3d moduleOffset = DrawingBuilder.ModuleExplosionOffset(module, analysis, settings);
			list.AddRange(module.Parts.Select((AssemblyPart part) => new ProjectedPart
			{
				Part = part,
				Transform = orientation * Transform.Translation(moduleOffset)
			}));
		}
		Transform transform = FitToPage(BoundsOf(list), boundingBox, settings, 34.0, 18.0);
		AddVisual(doc, list, transform, settings, groupIndex, highlight: false, generatedIds, "爆炸总览线稿");
		foreach (AssemblyModule module2 in analysis.Modules)
		{
			Vector3d vector3d = DrawingBuilder.ModuleExplosionOffset(module2, analysis, settings);
			Transform transform2 = transform * orientation * Transform.Translation(vector3d);
			Transform transform3 = transform * orientation;
			Point3d point3d = TransformPoint(module2.Center, transform2);
			Point3d end = TransformPoint(module2.Center, transform3);
			if (!module2.IsBase)
			{
				foreach (Guid item in DrawingBuilder.AddArrow(doc, point3d, end, settings.ArrowHead, DrawingBuilder.FindOrCreateArrowLayer(doc), groupIndex))
				{
					generatedIds.Add(item);
				}
			}
			Vector3d vector3d2 = TransformVector(vector3d, transform * orientation);
			vector3d2.Z = 0.0;
			if (!vector3d2.Unitize())
			{
				vector3d2 = Vector3d.XAxis;
			}
			Point3d center = point3d + vector3d2 * (12.0 * settings.ModelUnitsPerMillimeter);
			foreach (Guid item2 in DrawingBuilder.AddNumberBubble(doc, module2.Number, center, settings, DrawingBuilder.FindOrCreateNumberLayer(doc), groupIndex))
			{
				generatedIds.Add(item2);
			}
		}
		AddPageHeadings(doc, settings, boundingBox, groupIndex, "MODULE PREVIEW / 模块总览", "先完成各模块内部零件，再按 M01、M02… 的顺序完成总装。", generatedIds);
		return NewZone(pageIndex, "模块总览", boundingBox, "模块总览");
	}

	private static PageZone CreateModuleAssemblyPage(RhinoDoc doc, AssemblyAnalysis analysis, AssemblyModule module, ExplodeSettings settings, Point2d origin, int pageIndex, ICollection<Guid> generatedIds)
	{
		BoundingBox boundingBox = PageBounds(origin, settings);
		int groupIndex = NewPageGroup(doc, pageIndex, "模块总装_" + module.Number);
		AddId(generatedIds, DrawingBuilder.AddPageFrame(doc, boundingBox, groupIndex));
		Transform orientation = ViewProjectionBuilder.Orientation(settings);
		Vector3d vector3d = DrawingBuilder.ModuleExplosionOffset(module, analysis, settings);
		List<AssemblyModule> list = analysis.Modules.Where((AssemblyModule item) => item.AssemblyOrder <= module.AssemblyOrder).ToList();
		List<ProjectedPart> list2 = new List<ProjectedPart>();
		foreach (AssemblyModule item in list)
		{
			Vector3d offset = ((item == module) ? vector3d : Vector3d.Zero);
			list2.AddRange(item.Parts.Select((AssemblyPart part) => new ProjectedPart
			{
				Part = part,
				Transform = orientation * Transform.Translation(offset)
			}));
		}
		Transform transform = FitToPage(BoundsOf(list2), boundingBox, settings, 38.0, 22.0);
		AddVisual(doc, list2, transform, settings, groupIndex, highlight: false, generatedIds, "模块总装板件", useOriginalBoards: true);
		if (!module.IsBase)
		{
			foreach (Guid item2 in DrawingBuilder.AddArrow(doc, TransformPoint(module.Center, transform * orientation * Transform.Translation(vector3d)), TransformPoint(module.Center, transform * orientation), settings.ArrowHead, DrawingBuilder.FindOrCreateArrowLayer(doc), groupIndex))
			{
				generatedIds.Add(item2);
			}
		}
		Point3d point3d = TransformPoint(module.Center, transform * orientation * Transform.Translation(vector3d));
		foreach (Guid item3 in DrawingBuilder.AddNumberBubble(doc, module.Number, point3d + new Vector3d(12.0 * settings.ModelUnitsPerMillimeter, 0.0, 0.0), settings, DrawingBuilder.FindOrCreateNumberLayer(doc), groupIndex))
		{
			generatedIds.Add(item3);
		}
		string text = (module.IsBase ? ("总装 01：以模块 " + module.Number + "（" + module.Name + "）作为总装基准。") : ("总装 " + module.AssemblyOrder.ToString("00", CultureInfo.InvariantCulture) + "：将模块 " + module.Number + "（" + module.Name + "）沿红色箭头装入。"));
		AddPageHeadings(doc, settings, boundingBox, groupIndex, string.Format(CultureInfo.InvariantCulture, "MODULE {0:00}/{1:00}  {2}", module.AssemblyOrder, analysis.Modules.Count, module.Number), text, generatedIds);
		return NewZone(pageIndex, "模块总装_" + module.Number, boundingBox, text);
	}

	private static PageZone CreateSubassemblyPage(RhinoDoc doc, AssemblyAnalysis analysis, AssemblyModule module, ExplodeSettings settings, Point2d origin, int pageIndex, ICollection<Guid> generatedIds)
	{
		List<AssemblyPart> list = module.Parts.OrderBy((AssemblyPart item) => item.ModulePartOrder).ToList();
		string name = module.Name;
		BoundingBox boundingBox = PageBounds(origin, settings);
		int groupIndex = NewPageGroup(doc, pageIndex, "子装配_" + name);
		AddId(generatedIds, DrawingBuilder.AddPageFrame(doc, boundingBox, groupIndex));
		BoundingBox a = BoundingBox.Unset;
		foreach (AssemblyPart item in list)
		{
			a = (a.IsValid ? BoundingBox.Union(a, item.Bounds) : item.Bounds);
		}
		Transform orientation = ViewProjectionBuilder.Orientation(settings);
		Dictionary<AssemblyPart, Vector3d> offsets = new Dictionary<AssemblyPart, Vector3d>();
		foreach (AssemblyPart item2 in list)
		{
			offsets[item2] = DrawingBuilder.ModulePartExplosionOffset(item2, module, settings);
		}
		List<ProjectedPart> projected = list.Select((AssemblyPart part) => new ProjectedPart
		{
			Part = part,
			Transform = orientation * Transform.Translation(offsets[part])
		}).ToList();
		Transform transform = FitToPage(BoundsOf(projected), boundingBox, settings, 38.0, 22.0);
		AddVisual(doc, projected, transform, settings, groupIndex, highlight: false, generatedIds, "子装配板件", useOriginalBoards: true);
		List<BoundingBox> list2 = new List<BoundingBox>();
		foreach (AssemblyPart item3 in list)
		{
			Transform transform2 = transform * orientation * Transform.Translation(offsets[item3]);
			Transform transform3 = transform * orientation;
			Point3d point = item3.Center + (item3.HasArrowAnchorOffset ? item3.ArrowAnchorOffset : Vector3d.Zero);
			foreach (Guid item4 in DrawingBuilder.AddArrow(doc, TransformPoint(point, transform2), TransformPoint(point, transform3), settings.ArrowHead, DrawingBuilder.FindOrCreateArrowLayer(doc), groupIndex))
			{
				generatedIds.Add(item4);
			}
			Point3d center = TransformPoint(item3.Center, transform2);
			Point3d point3d = PageLabelPoint(item3, center, offsets[item3], transform * orientation, settings, list2);
			string text = DrawingBuilder.PartLabel(item3);
			foreach (Guid item5 in DrawingBuilder.AddNumberBubble(doc, text, point3d, settings, DrawingBuilder.FindOrCreateNumberLayer(doc), groupIndex))
			{
				generatedIds.Add(item5);
			}
			list2.Add(LabelBox(point3d, text, settings));
		}
		AddPageHeadings(doc, settings, boundingBox, groupIndex, "MODULE DETAIL / 模块拆解：" + module.Number + " " + name, "先按后续页面完成本模块内部零件，再进入模块总装。", generatedIds);
		return NewZone(pageIndex, "子装配_" + name, boundingBox, "子装配：" + name);
	}

	private static PageZone CreateStepPage(RhinoDoc doc, AssemblyAnalysis analysis, AssemblyModule module, AssemblyPart current, ExplodeSettings settings, Point2d origin, int pageIndex, ICollection<Guid> generatedIds)
	{
		int step = current.ModulePartOrder;
		List<AssemblyPart> source = module.Parts.Where((AssemblyPart item) => item.ModulePartOrder <= step).ToList();
		BoundingBox boundingBox = PageBounds(origin, settings);
		int groupIndex = NewPageGroup(doc, pageIndex, module.Number + "_" + current.PartNumber);
		AddId(generatedIds, DrawingBuilder.AddPageFrame(doc, boundingBox, groupIndex));
		Transform orientation = ViewProjectionBuilder.Orientation(settings);
		List<ProjectedPart> projected = source.Select((AssemblyPart part) => new ProjectedPart
		{
			Part = part,
			Highlight = part == current,
            Transform = orientation * Transform.Translation((part == current) ? DrawingBuilder.ModulePartExplosionOffset(part, module, settings) : Vector3d.Zero)
		}).ToList();
		BoundingBox page = boundingBox;
		if (settings.IncludeDetailInset)
		{
			page = new BoundingBox(boundingBox.Min, new Point3d(boundingBox.Max.X - 30.0 * settings.ModelUnitsPerMillimeter, boundingBox.Max.Y, boundingBox.Max.Z));
		}
		Transform transform = FitToPage(BoundsOf(projected), page, settings, 38.0, 22.0);
		AddVisual(doc, projected, transform, settings, groupIndex, highlight: false, generatedIds, "步骤板件", useOriginalBoards: true);
		Vector3d vector3d = DrawingBuilder.ModulePartExplosionOffset(current, module, settings);
		Transform transform2 = transform * orientation * Transform.Translation(vector3d);
		Transform transform3 = transform * orientation;
		Point3d point = current.Center + (current.HasArrowAnchorOffset ? current.ArrowAnchorOffset : Vector3d.Zero);
		Point3d start = TransformPoint(point, transform2);
		Point3d end = TransformPoint(point, transform3);
		if (!current.IsModuleBase)
		{
			foreach (Guid item in DrawingBuilder.AddArrow(doc, start, end, settings.ArrowHead, DrawingBuilder.FindOrCreateArrowLayer(doc), groupIndex))
			{
				generatedIds.Add(item);
			}
		}
		Point3d center = TransformPoint(current.Center, transform2);
		Point3d center2 = PageLabelPoint(current, center, vector3d, transform * orientation, settings, new List<BoundingBox>());
		foreach (Guid item2 in DrawingBuilder.AddNumberBubble(doc, DrawingBuilder.PartLabel(current), center2, settings, DrawingBuilder.FindOrCreateNumberLayer(doc), groupIndex))
		{
			generatedIds.Add(item2);
		}
		if (settings.IncludeDetailInset)
		{
			AddDetailInset(doc, current, orientation, boundingBox, settings, groupIndex, generatedIds);
		}
		string text = "，模块：" + module.Number + " " + module.Name;
		string text2 = ((current.Quantity > 1) ? ("，同编号共 ×" + current.Quantity) : string.Empty);
		string text3 = (current.IsModuleBase ? ("模块内步骤 01：以 " + current.PartNumber + " 作为本模块基准件" + text + "。") : ("模块内步骤 " + step.ToString("00", CultureInfo.InvariantCulture) + "：将 " + current.PartNumber + "（" + current.Name + "）沿红色箭头安装" + text + text2 + "。"));
		AddPageHeadings(doc, settings, boundingBox, groupIndex, $"{module.Number}  PART {step:00}/{module.Parts.Count:00}  {current.PartNumber}", text3, generatedIds);
		return NewZone(pageIndex, module.Number + "_步骤_" + step.ToString("00") + "_" + current.PartNumber, boundingBox, text3);
	}

	private static PageZone CreateCompletionPage(RhinoDoc doc, AssemblyAnalysis analysis, ExplodeSettings settings, Point2d origin, int pageIndex, ICollection<Guid> generatedIds)
	{
		BoundingBox boundingBox = PageBounds(origin, settings);
		int groupIndex = NewPageGroup(doc, pageIndex, "装配完成");
		AddId(generatedIds, DrawingBuilder.AddPageFrame(doc, boundingBox, groupIndex));
		Transform orientation = ViewProjectionBuilder.Orientation(settings);
		List<ProjectedPart> projected = analysis.Parts.Select((AssemblyPart part) => new ProjectedPart
		{
			Part = part,
			Transform = orientation
		}).ToList();
		Transform fit = FitToPage(BoundsOf(projected), boundingBox, settings, 42.0, 25.0);
		AddVisual(doc, projected, fit, settings, groupIndex, highlight: false, generatedIds, "完成模型线稿");
		AddPageHeadings(doc, settings, boundingBox, groupIndex, "ASSEMBLY COMPLETE / 装配完成", settings.ProductTitle + " 已完成装配。请检查卡口、转轴与运动部件是否顺畅。", generatedIds);
		return NewZone(pageIndex, "装配完成", boundingBox, "装配完成");
	}

	private static void AddDetailInset(RhinoDoc doc, AssemblyPart current, Transform orientation, BoundingBox pageBounds, ExplodeSettings settings, int groupIndex, ICollection<Guid> generatedIds)
	{
		double num = 10.0 * settings.ModelUnitsPerMillimeter;
		BoundingBox pageBounds2 = new BoundingBox(new Point3d(pageBounds.Max.X - pageBounds.Diagonal.X * 0.29, pageBounds.Max.Y - pageBounds.Diagonal.Y * 0.36, 0.0), new Point3d(pageBounds.Max.X - num, pageBounds.Max.Y - 27.0 * settings.ModelUnitsPerMillimeter, 0.0));
		AddId(generatedIds, DrawingBuilder.AddPageFrame(doc, pageBounds2, groupIndex));
		AddId(generatedIds, DrawingBuilder.AddPageText(doc, "DETAIL / 零件局部", new Point3d(pageBounds2.Min.X + 3.0 * settings.ModelUnitsPerMillimeter, pageBounds2.Max.Y - 5.0 * settings.ModelUnitsPerMillimeter, 0.0), 3.2 * settings.ModelUnitsPerMillimeter, groupIndex, TextJustification.MiddleLeft));
		List<ProjectedPart> projected = new List<ProjectedPart>
		{
			new ProjectedPart
			{
				Part = current,
				Transform = orientation
			}
		};
		Vector3d vector3d = new Vector3d(4.0, 4.0, 0.0) * settings.ModelUnitsPerMillimeter;
		Vector3d vector3d2 = new Vector3d(4.0, 9.0, 0.0) * settings.ModelUnitsPerMillimeter;
		Transform fit = FitToBox(target: new BoundingBox(pageBounds2.Min + vector3d, pageBounds2.Max - vector3d2), content: BoundsOf(projected));
		AddVisual(doc, projected, fit, settings, groupIndex, highlight: false, generatedIds, "局部放大板件", useOriginalBoards: true);
	}

	private static void AddVisual(RhinoDoc doc, IList<ProjectedPart> projected, Transform fit, ExplodeSettings settings, int groupIndex, bool highlight, ICollection<Guid> generatedIds, string name, bool useOriginalBoards = false)
	{
		if (settings.CreateVectorLinework && !useOriginalBoards)
		{
			int layerIndex = (highlight ? DrawingBuilder.FindOrCreateCurrentLayer(doc) : DrawingBuilder.FindOrCreateVectorLayer(doc));
			{
				foreach (Guid item in VectorDrawingBuilder.AddProjection(doc, projected, fit, layerIndex, groupIndex, name))
				{
					generatedIds.Add(item);
				}
				return;
			}
		}
        foreach(var group in projected.GroupBy(p=>new {p.Transform,p.Highlight}))
        {
            if(!highlight && !group.Key.Highlight && group.Count()>1)
                generatedIds.Add(RenderCache.AddInstalled(doc,group.Select(p=>p.Part).ToList(),fit*group.Key.Transform,groupIndex));
            else foreach(var item in group)
                foreach(var id in DrawingBuilder.AddPartCopy(doc,item.Part,fit*item.Transform,groupIndex,highlight || item.Highlight))generatedIds.Add(id);
        }
	}

	private static Point3d PageLabelPoint(AssemblyPart part, Point3d center, Vector3d explosionOffset, Transform visualTransform, ExplodeSettings settings, IList<BoundingBox> used)
	{
		if (part.HasLabelOffset && part.LabelOffset.IsValid)
		{
			return center + TransformVector(part.LabelOffset, visualTransform);
		}
		Vector3d vector3d = TransformVector(explosionOffset, visualTransform);
		vector3d.Z = 0.0;
		if (!vector3d.Unitize())
		{
			vector3d = Vector3d.XAxis;
		}
		Vector3d[] obj = new Vector3d[9]
		{
			vector3d,
			Vector3d.XAxis,
			Vector3d.YAxis,
			-Vector3d.XAxis,
			-Vector3d.YAxis,
			new Vector3d(1.0, 1.0, 0.0),
			new Vector3d(-1.0, 1.0, 0.0),
			new Vector3d(1.0, -1.0, 0.0),
			new Vector3d(-1.0, -1.0, 0.0)
		};
		double num = 12.0 * settings.ModelUnitsPerMillimeter;
		Vector3d[] array = obj;
		foreach (Vector3d vector3d2 in array)
		{
			Vector3d vector3d3 = vector3d2;
			vector3d3.Unitize();
			Point3d point3d = center + vector3d3 * num;
			BoundingBox box = LabelBox(point3d, DrawingBuilder.PartLabel(part), settings);
			if (used.All((BoundingBox item) => !Overlaps(item, box)))
			{
				return point3d;
			}
		}
		return center + vector3d * (num * 2.0);
	}

	private static BoundingBox LabelBox(Point3d point, string text, ExplodeSettings settings)
	{
		double num = Math.Max(4.5, 0.65 * (double)(text ?? string.Empty).Length) * settings.ModelUnitsPerMillimeter;
		return new BoundingBox(point - new Vector3d(num, num, 0.0), point + new Vector3d(num, num, 0.0));
	}

	private static bool Overlaps(BoundingBox left, BoundingBox right)
	{
		if (left.Min.X <= right.Max.X && left.Max.X >= right.Min.X && left.Min.Y <= right.Max.Y)
		{
			return left.Max.Y >= right.Min.Y;
		}
		return false;
	}

	private static void AddPageHeadings(RhinoDoc doc, ExplodeSettings settings, BoundingBox pageBounds, int groupIndex, string title, string instruction, ICollection<Guid> generatedIds)
	{
		Point3d point = new Point3d(pageBounds.Min.X + 12.0 * settings.ModelUnitsPerMillimeter, pageBounds.Max.Y - 15.0 * settings.ModelUnitsPerMillimeter, 0.0);
		Point3d point2 = new Point3d(pageBounds.Min.X + 12.0 * settings.ModelUnitsPerMillimeter, pageBounds.Min.Y + 10.0 * settings.ModelUnitsPerMillimeter, 0.0);
		AddId(generatedIds, DrawingBuilder.AddPageText(doc, title, point, 6.0 * settings.ModelUnitsPerMillimeter, groupIndex, TextJustification.MiddleLeft));
		AddId(generatedIds, DrawingBuilder.AddPageText(doc, instruction, point2, 4.0 * settings.ModelUnitsPerMillimeter, groupIndex, TextJustification.MiddleLeft));
	}

	private static Point2d PageOrigin(double startX, double startY, int pageIndex, ExplodeSettings settings)
	{
		return new Point2d(startX + (double)pageIndex * (settings.PageWidth + settings.PageGap), startY);
	}

	private static BoundingBox PageBounds(Point2d origin, ExplodeSettings settings)
	{
		return new BoundingBox(new Point3d(origin.X, origin.Y, 0.0), new Point3d(origin.X + settings.PageWidth, origin.Y + settings.PageHeight, 0.0));
	}

	private static BoundingBox BoundsOf(IEnumerable<ProjectedPart> projected)
	{
		BoundingBox boundingBox = BoundingBox.Unset;
		foreach (ProjectedPart item in projected)
		{
			BoundingBox boundingBox2 = DrawingBuilder.TransformedPartBounds(item.Part, item.Transform);
			boundingBox = (boundingBox.IsValid ? BoundingBox.Union(boundingBox, boundingBox2) : boundingBox2);
		}
		return boundingBox;
	}

	private static Transform FitToPage(BoundingBox content, BoundingBox page, ExplodeSettings settings, double topMarginMillimeters, double bottomMarginMillimeters)
	{
		BoundingBox target = new BoundingBox(new Point3d(page.Min.X + 14.0 * settings.ModelUnitsPerMillimeter, page.Min.Y + bottomMarginMillimeters * settings.ModelUnitsPerMillimeter, 0.0), new Point3d(page.Max.X - 14.0 * settings.ModelUnitsPerMillimeter, page.Max.Y - topMarginMillimeters * settings.ModelUnitsPerMillimeter, 0.0));
		return FitToBox(content, target);
	}

	private static Transform FitToBox(BoundingBox content, BoundingBox target)
	{
		double num = Math.Max(target.Diagonal.X, 0.001);
		double num2 = Math.Max(target.Diagonal.Y, 0.001);
		double num3 = Math.Max(content.Diagonal.X, 0.001);
		double num4 = Math.Max(content.Diagonal.Y, 0.001);
		double scaleFactor = Math.Max(0.001, Math.Min(num / num3, num2 / num4));
		Transform transform = Transform.Scale(Point3d.Origin, scaleFactor);
		Point3d center = content.Center;
		center.Transform(transform);
		return Transform.Translation(target.Center - center) * transform;
	}

	private static Point3d TransformPoint(Point3d point, Transform transform)
	{
		point.Transform(transform);
		return point;
	}

	private static Vector3d TransformVector(Vector3d vector, Transform transform)
	{
		Point3d origin = Point3d.Origin;
		Point3d point3d = origin + vector;
		origin.Transform(transform);
		point3d.Transform(transform);
		return point3d - origin;
	}

	private static int NewPageGroup(RhinoDoc doc, int pageIndex, string suffix)
	{
		return doc.Groups.Add(string.Format(CultureInfo.InvariantCulture, "EB_说明页_{0:00}_{1}_{2}", pageIndex, suffix, Guid.NewGuid().ToString("N").Substring(0, 6)));
	}

	private static PageZone NewZone(int pageIndex, string name, BoundingBox bounds, string title)
	{
		return new PageZone
		{
			PageIndex = pageIndex,
			LayoutName = "EB_" + pageIndex.ToString("00", CultureInfo.InvariantCulture) + "_" + name,
			Title = title,
			Bounds = bounds
		};
	}

	private static string Trim(string value, int maximum)
	{
		if (string.IsNullOrEmpty(value) || value.Length <= maximum)
		{
			return value ?? string.Empty;
		}
		return value.Substring(0, Math.Max(1, maximum - 1)) + "…";
	}

	private static void CreateLayout(RhinoDoc doc, PageZone page, ExplodeSettings settings)
	{
        double paperUnits=RhinoMath.UnitScale(UnitSystem.Millimeters,doc.PageUnitSystem);
        if(double.IsNaN(paperUnits)||double.IsInfinity(paperUnits)||paperUnits<=0)throw new InvalidOperationException("文档纸张单位无效，请先设置纸张单位。");
		page.LayoutName = UniqueLayoutName(doc, page.LayoutName);
        RhinoPageView rhinoPageView = doc.Views.AddPageView(page.LayoutName, settings.PageWidthMillimeters*paperUnits, settings.PageHeightMillimeters*paperUnits);
		if (rhinoPageView == null) throw new InvalidOperationException("无法建立说明书Layout："+page.LayoutName);
		if (rhinoPageView != null)
		{
            Point2d corner = new Point2d(5.0*paperUnits, (settings.PageHeightMillimeters - 5.0)*paperUnits);
            Point2d corner2 = new Point2d((settings.PageWidthMillimeters - 5.0)*paperUnits, 5.0*paperUnits);
			DetailViewObject detailViewObject = rhinoPageView.AddDetailView("装配说明", corner, corner2, DefinedViewportProjection.Top);
			if (detailViewObject == null) throw new InvalidOperationException("无法建立说明书平行视口："+page.LayoutName);
			if (detailViewObject != null)
			{
				rhinoPageView.SetActiveDetail(detailViewObject.Id);
				detailViewObject.Viewport.DisplayMode = DisplayModeDescription.GetDisplayMode(DisplayModeDescription.ShadedId);
				detailViewObject.Viewport.ZoomBoundingBox(page.Bounds);
				detailViewObject.DetailGeometry.IsProjectionLocked = true;
				detailViewObject.CommitChanges();
				rhinoPageView.SetPageAsActive();
			}
		}
	}

	private static string UniqueLayoutName(RhinoDoc doc, string requested)
	{
		HashSet<string> hashSet = new HashSet<string>((doc.Views.GetPageViews() ?? new RhinoPageView[0]).Select((RhinoPageView item) => item.PageName), StringComparer.OrdinalIgnoreCase);
		if (!hashSet.Contains(requested))
		{
			return requested;
		}
		int num = 2;
		while (hashSet.Contains(requested + "_" + num))
		{
			num++;
		}
		return requested + "_" + num;
	}

	private static void AddId(ICollection<Guid> ids, Guid id)
	{
		if (id != Guid.Empty)
		{
			ids.Add(id);
		}
	}
}
