using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace ExplodeBook.Core;

internal static class DrawingBuilder
{
	private const string ArrowLayerName = "ExplodeBook_箭头";

	private const string NumberLayerName = "ExplodeBook_编号";

	private const string PageLayerName = "ExplodeBook_页面";

	private const string CurrentPartLayerName = "ExplodeBook_当前步骤";

	private const string VectorLayerName = "ExplodeBook_矢量线稿";

	public static List<Guid> CreateExplodedOverview(RhinoDoc doc, AssemblyAnalysis analysis, ExplodeSettings settings)
	{
		List<Guid> list = new List<Guid>();
		int layerIndex = FindOrCreateLayer(doc, "ExplodeBook_箭头", Color.FromArgb(220, 60, 55));
		int layerIndex2 = FindOrCreateLayer(doc, "ExplodeBook_编号", Color.FromArgb(35, 35, 35));
		double num = 50.0 * settings.ModelUnitsPerMillimeter;
		Vector3d vector3d = new Vector3d(analysis.Bounds.Max.X + num - analysis.Bounds.Min.X, 0.0, 0.0);
		int groupIndex = doc.Groups.Add("EB_爆炸总览_" + Guid.NewGuid().ToString("N").Substring(0, 6));
		List<BoundingBox> list2 = new List<BoundingBox>();
		foreach (AssemblyModule module in analysis.Modules)
		{
			Vector3d vector3d2 = ModuleExplosionOffset(module, analysis, settings);
			Transform transform = Transform.Translation(vector3d + vector3d2);
			foreach (AssemblyPart part in module.Parts)
			{
				list.AddRange(AddPartCopy(doc, part, transform, groupIndex, highlightCurrent: false));
			}
			Point3d point3d = module.Center + vector3d;
			Point3d point3d2 = point3d + vector3d2;
			if (!module.IsBase)
			{
				list.AddRange(AddArrow(doc, point3d2, point3d, settings.ArrowHead, layerIndex, groupIndex));
			}
			Vector3d vector3d3 = vector3d2;
			if (!vector3d3.Unitize())
			{
				vector3d3 = Vector3d.XAxis;
			}
			Point3d point3d3 = point3d2 + vector3d3 * (module.Bounds.Diagonal.Length * 0.25 + 10.0 * settings.ModelUnitsPerMillimeter);
			string number = module.Number;
			list.AddRange(AddNumberBubble(doc, number, point3d3, settings, layerIndex2, groupIndex));
			double num2 = Math.Max(4.5, 0.65 * (double)number.Length) * settings.ModelUnitsPerMillimeter;
			list2.Add(new BoundingBox(point3d3 - new Vector3d(num2, num2, 0.0), point3d3 + new Vector3d(num2, num2, 0.0)));
		}
		Point3d point = new Point3d(analysis.Bounds.Min.X + vector3d.X, analysis.Bounds.Max.Y + 12.0 * settings.ModelUnitsPerMillimeter, analysis.Bounds.Max.Z + 2.0 * settings.ModelUnitsPerMillimeter);
		Guid guid = AddText(doc, "EXPLODED VIEW / 装配爆炸总览", point, 7.0 * settings.ModelUnitsPerMillimeter, layerIndex2, groupIndex, TextJustification.BottomLeft);
		if (guid != Guid.Empty)
		{
			list.Add(guid);
		}
		return list;
	}

    public static List<Guid> AddPartCopy(RhinoDoc doc, AssemblyPart part, Transform transform, int groupIndex, bool highlightCurrent)
    { return new List<Guid> { RenderCache.Add(doc,part,transform,groupIndex,highlightCurrent) }; }

	public static List<Guid> AddArrow(RhinoDoc doc, Point3d start, Point3d end, double headSize, int layerIndex, int groupIndex)
	{
		List<Guid> list = new List<Guid>();
		Vector3d vector3d = end - start;
		if (!vector3d.Unitize() || start.DistanceTo(end) <= headSize * 1.5)
		{
			return list;
		}
		Vector3d vector3d2 = Vector3d.ZAxis;
		if (Math.Abs(vector3d * vector3d2) > 0.95)
		{
			vector3d2 = Vector3d.YAxis;
		}
		Vector3d vector3d3 = Vector3d.CrossProduct(vector3d, vector3d2);
		if (!vector3d3.Unitize())
		{
			vector3d3 = Vector3d.XAxis;
		}
		Point3d point3d = end - vector3d * headSize;
		Point3d point3d2 = point3d + vector3d3 * headSize * 0.45;
		Point3d point3d3 = point3d - vector3d3 * headSize * 0.45;
		list.Add(AddCurve(doc, new LineCurve(start, end), layerIndex, groupIndex, "安装箭头"));
		list.Add(AddCurve(doc, new LineCurve(point3d2, end), layerIndex, groupIndex, "安装箭头头部"));
		list.Add(AddCurve(doc, new LineCurve(point3d3, end), layerIndex, groupIndex, "安装箭头头部"));
		return list.Where((Guid item) => item != Guid.Empty).ToList();
	}

	public static List<Guid> AddNumberBubble(RhinoDoc doc, string number, Point3d center, ExplodeSettings settings, int layerIndex, int groupIndex)
	{
		List<Guid> list = new List<Guid>();
		double radius = Math.Max(4.5, 0.65 * (double)(number ?? string.Empty).Length) * settings.ModelUnitsPerMillimeter;
		list.Add(AddCurve(doc, new Circle(new Plane(center, Vector3d.ZAxis), radius).ToNurbsCurve(), layerIndex, groupIndex, "零件编号框"));
		Point3d point = new Point3d(center.X, center.Y, center.Z);
		Guid guid = AddText(doc, number, point, 3.5 * settings.ModelUnitsPerMillimeter, layerIndex, groupIndex, TextJustification.MiddleCenter);
		if (guid != Guid.Empty)
		{
			list.Add(guid);
		}
		return list.Where((Guid item) => item != Guid.Empty).ToList();
	}

	public static Guid AddPageFrame(RhinoDoc doc, BoundingBox pageBounds, int groupIndex)
	{
		int layerIndex = FindOrCreateLayer(doc, "ExplodeBook_页面", Color.FromArgb(70, 70, 70));
		double z = pageBounds.Min.Z;
		Point3d[] points = new Point3d[5]
		{
			new Point3d(pageBounds.Min.X, pageBounds.Min.Y, z),
			new Point3d(pageBounds.Max.X, pageBounds.Min.Y, z),
			new Point3d(pageBounds.Max.X, pageBounds.Max.Y, z),
			new Point3d(pageBounds.Min.X, pageBounds.Max.Y, z),
			new Point3d(pageBounds.Min.X, pageBounds.Min.Y, z)
		};
		return AddCurve(doc, new PolylineCurve(points), layerIndex, groupIndex, "说明书页框");
	}

	public static Guid AddPageText(RhinoDoc doc, string text, Point3d point, double height, int groupIndex, TextJustification justification)
	{
		int layerIndex = FindOrCreateLayer(doc, "ExplodeBook_页面", Color.FromArgb(35, 35, 35));
		return AddText(doc, text, point, height, layerIndex, groupIndex, justification);
	}

	public static Vector3d ExplosionOffset(AssemblyPart part, AssemblyAnalysis analysis, ExplodeSettings settings)
	{
		if (part.PathVerified && !part.IsBase) return part.AutoDirection * Math.Max(settings.ExplodeDistance, Math.Min(part.PathTravel, part.Bounds.Diagonal.Length));
		if (part.IsBase)
		{
			return Vector3d.Zero;
		}
		if (part.HasExactExplosionOffset && part.ExactExplosionOffset.IsValid && !part.ExactExplosionOffset.IsTiny())
		{
			return part.ExactExplosionOffset;
		}
		Vector3d vector3d;
		if (part.HasCustomDirection && part.CustomDirection.IsValid && !part.CustomDirection.IsTiny())
		{
			vector3d = part.CustomDirection;
		}
		else
		{
			switch (settings.Mode)
			{
			case ExplodeMode.XAxis:
				vector3d = Vector3d.XAxis;
				break;
			case ExplodeMode.YAxis:
				vector3d = Vector3d.YAxis;
				break;
			case ExplodeMode.ZAxis:
				vector3d = Vector3d.ZAxis;
				break;
			default:
				vector3d = ((part.AutoDirection.IsValid && !part.AutoDirection.IsTiny()) ? part.AutoDirection : (part.Center - analysis.Bounds.Center));
				if (!vector3d.Unitize())
				{
					vector3d = ((part.AssemblyOrder - 2) % 4) switch
					{
						2 => -Vector3d.XAxis, 
						1 => Vector3d.YAxis, 
						0 => Vector3d.XAxis, 
						_ => -Vector3d.YAxis, 
					};
				}
				break;
			}
		}
		vector3d.Unitize();
		double num = Math.Min(part.Bounds.Diagonal.Length * 0.3, settings.ExplodeDistance * 1.5);
		double num2 = settings.ExplodeDistance * (1.0 + 0.28 * (double)Math.Max(0, part.AssemblyOrder - 2)) + num;
		return vector3d * num2;
	}

	public static Vector3d ModuleExplosionOffset(AssemblyModule module, AssemblyAnalysis analysis, ExplodeSettings settings)
	{
		if (module != null && module.PathVerified && !module.IsBase) return module.AutoDirection * Math.Max(settings.ExplodeDistance, Math.Min(module.PathTravel, module.Bounds.Diagonal.Length));
		if (module == null || module.IsBase)
		{
			return Vector3d.Zero;
		}
		Vector3d vector3d = module.AutoDirection;
		if (!vector3d.IsValid || vector3d.IsTiny())
		{
			vector3d = module.Center - analysis.Bounds.Center;
		}
		if (!vector3d.Unitize())
		{
			vector3d = ((module.AssemblyOrder - 2) % 4) switch
			{
				2 => -Vector3d.XAxis, 
				1 => Vector3d.YAxis, 
				0 => Vector3d.XAxis, 
				_ => -Vector3d.YAxis, 
			};
		}
		double num = Math.Min(module.Bounds.Diagonal.Length * 0.22, settings.ExplodeDistance * 1.5);
		return vector3d * (settings.ExplodeDistance * (1.15 + 0.38 * (double)Math.Max(0, module.AssemblyOrder - 2)) + num);
	}

	public static Vector3d ModulePartExplosionOffset(AssemblyPart part, AssemblyModule module, ExplodeSettings settings)
	{
		if (part != null && part.PathVerified && !part.IsModuleBase) return part.AutoDirection * Math.Max(settings.ExplodeDistance, Math.Min(part.PathTravel, part.Bounds.Diagonal.Length));
		if (part == null || part.IsModuleBase)
		{
			return Vector3d.Zero;
		}
		if (part.HasExactExplosionOffset && part.ExactExplosionOffset.IsValid && !part.ExactExplosionOffset.IsTiny())
		{
			return part.ExactExplosionOffset;
		}
		Vector3d vector3d = ((!part.HasCustomDirection || !part.CustomDirection.IsValid || part.CustomDirection.IsTiny()) ? ((part.AutoDirection.IsValid && !part.AutoDirection.IsTiny()) ? part.AutoDirection : (part.Center - module.Center)) : part.CustomDirection);
		if (!vector3d.Unitize())
		{
			vector3d = ((part.ModulePartOrder % 2 == 0) ? Vector3d.XAxis : Vector3d.YAxis);
		}
		double num = Math.Min(part.Bounds.Diagonal.Length * 0.24, settings.ExplodeDistance);
		return vector3d * (settings.ExplodeDistance * (0.85 + 0.24 * (double)Math.Max(0, part.ModulePartOrder - 2)) + num);
	}

	public static BoundingBox TransformedPartBounds(AssemblyPart part, Transform transform)
	{
		BoundingBox boundingBox = BoundingBox.Unset;
		Point3d[] corners = part.Bounds.GetCorners();
		for (int i = 0; i < corners.Length; i++)
		{
			Point3d point3d = corners[i];
			point3d.Transform(transform);
			boundingBox = (boundingBox.IsValid ? BoundingBox.Union(boundingBox, point3d) : new BoundingBox(point3d, point3d));
		}
		return boundingBox;
	}

	public static int FindOrCreateArrowLayer(RhinoDoc doc)
	{
		return FindOrCreateLayer(doc, "ExplodeBook_箭头", Color.FromArgb(220, 60, 55));
	}

	public static int FindOrCreateNumberLayer(RhinoDoc doc)
	{
		return FindOrCreateLayer(doc, "ExplodeBook_编号", Color.FromArgb(35, 35, 35));
	}

	public static int FindOrCreateVectorLayer(RhinoDoc doc)
	{
		return FindOrCreateLayer(doc, "ExplodeBook_矢量线稿", Color.FromArgb(35, 35, 35));
	}

	public static int FindOrCreateCurrentLayer(RhinoDoc doc)
	{
		return FindOrCreateLayer(doc, "ExplodeBook_当前步骤", Color.FromArgb(245, 178, 35));
	}

	public static Guid AddGeneratedCurve(RhinoDoc doc, Curve curve, int layerIndex, int groupIndex, string name)
	{
		return AddCurve(doc, curve, layerIndex, groupIndex, name);
	}

	public static Guid AddGeneratedText(RhinoDoc doc, string text, Point3d point, double height, int layerIndex, int groupIndex, TextJustification justification)
	{
		return AddText(doc, text, point, height, layerIndex, groupIndex, justification);
	}

	public static string PartLabel(AssemblyPart part)
	{
		return part.PartNumber + ((part.Quantity > 1) ? (" ×" + part.Quantity) : string.Empty);
	}

	public static Point3d FindLabelPoint(AssemblyPart part, Point3d explodedCenter, Vector3d offset, ExplodeSettings settings, IList<BoundingBox> usedLabelBoxes)
	{
		if (part.HasLabelOffset && part.LabelOffset.IsValid)
		{
			return explodedCenter + part.LabelOffset;
		}
		Vector3d vector3d = offset;
		if (!vector3d.Unitize())
		{
			vector3d = Vector3d.XAxis;
		}
		if (Math.Abs(vector3d * Vector3d.ZAxis) > 0.9)
		{
			vector3d = Vector3d.XAxis;
		}
		double num = part.Bounds.Diagonal.Length * 0.35 + 9.0 * settings.ModelUnitsPerMillimeter;
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
		double num2 = Math.Max(5.0, 0.65 * (double)PartLabel(part).Length) * settings.ModelUnitsPerMillimeter;
		Vector3d[] array = obj;
		foreach (Vector3d vector3d2 in array)
		{
			Vector3d vector3d3 = vector3d2;
			vector3d3.Unitize();
			Point3d point3d = explodedCenter + vector3d3 * num;
			BoundingBox box = new BoundingBox(point3d - new Vector3d(num2, num2, 0.0), point3d + new Vector3d(num2, num2, 0.0));
			if (usedLabelBoxes == null || usedLabelBoxes.All((BoundingBox existing) => !BoxesOverlap(box, existing)))
			{
				return point3d;
			}
		}
		return explodedCenter + vector3d * (num + num2 * 2.0);
	}

	private static bool BoxesOverlap(BoundingBox left, BoundingBox right)
	{
		if (left.Min.X <= right.Max.X && left.Max.X >= right.Min.X && left.Min.Y <= right.Max.Y)
		{
			return left.Max.Y >= right.Min.Y;
		}
		return false;
	}

    private static Guid AddCurve(RhinoDoc doc, Curve curve, int layerIndex, int groupIndex, string name)
    {
        ObjectAttributes attributes = GeneratedAttributes(layerIndex, groupIndex, name);
        var id=doc.Objects.AddCurve(curve, attributes);
        if(id==Guid.Empty)throw new InvalidOperationException("无法生成说明图形："+name);
        return id;
	}

	private static Guid AddText(RhinoDoc doc, string text, Point3d point, double height, int layerIndex, int groupIndex, TextJustification justification)
	{
		TextEntity text2 = new TextEntity
		{
			Plane = new Plane(point, Vector3d.ZAxis),
			PlainText = (text ?? string.Empty),
			TextHeight = height,
			Justification = justification
		};
        var id=doc.Objects.AddText(text2, GeneratedAttributes(layerIndex, groupIndex, "说明文字"));
        if(id==Guid.Empty)throw new InvalidOperationException("无法生成说明文字。");
        return id;
	}

	private static ObjectAttributes GeneratedAttributes(int layerIndex, int groupIndex, string name)
	{
		ObjectAttributes objectAttributes = new ObjectAttributes
		{
			LayerIndex = layerIndex,
			Name = name,
			ColorSource = ObjectColorSource.ColorFromLayer
		};
		if (groupIndex >= 0)
		{
			objectAttributes.AddToGroup(groupIndex);
		}
		objectAttributes.SetUserString("ExplodeBook.Generated", "1");
		return objectAttributes;
	}

	internal static int FindOrCreateLayer(RhinoDoc doc, string name, Color color)
	{
		foreach (Layer layer in doc.Layers)
		{
			if (string.Equals(layer.Name, name, StringComparison.OrdinalIgnoreCase))
			{
				return layer.Index;
			}
		}
		return doc.Layers.Add(new Layer
		{
			Name = name,
			Color = color,
			PlotColor = color
		});
	}
}
