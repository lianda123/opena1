using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace ExplodeBook.Core;

internal static class VectorDrawingBuilder
{
	public static List<Guid> AddProjection(RhinoDoc doc, IEnumerable<ProjectedPart> projectedParts, Transform pageTransform, int layerIndex, int groupIndex, string name)
	{
		List<Guid> list = new List<Guid>();
		List<ProjectedPart> list2 = ((projectedParts == null) ? new List<ProjectedPart>() : projectedParts.Where((ProjectedPart item) => item != null && item.Part != null).ToList());
		if (list2.Count == 0)
		{
			return list;
		}
		try
		{
			HiddenLineDrawingParameters hiddenLineDrawingParameters = new HiddenLineDrawingParameters
			{
				AbsoluteTolerance = Math.Max(doc.ModelAbsoluteTolerance, 0.001),
				IncludeHiddenCurves = false,
				IncludeTangentEdges = true,
				IncludeTangentSeams = false
			};
			hiddenLineDrawingParameters.SetViewport(CreateTopViewport());
			foreach (ProjectedPart item in list2)
			{
				foreach (RhinoObject @object in item.Part.Objects)
				{
					if (@object != null && @object.Geometry != null)
					{
						hiddenLineDrawingParameters.AddGeometry(@object.Geometry, item.Transform, @object.Id);
					}
				}
			}
			using HiddenLineDrawing hiddenLineDrawing = HiddenLineDrawing.Compute(hiddenLineDrawingParameters, multipleThreads: true);
			if (hiddenLineDrawing == null)
			{
				return AddFallbackProjection(doc, list2, pageTransform, layerIndex, groupIndex, name);
			}
			foreach (HiddenLineDrawingSegment segment in hiddenLineDrawing.Segments)
			{
				if (segment == null || segment.SegmentVisibility != HiddenLineDrawingSegment.Visibility.Visible || segment.CurveGeometry == null)
				{
					continue;
				}
				Curve curve = segment.CurveGeometry.DuplicateCurve();
				if (curve != null)
				{
					curve.Transform(Transform.PlanarProjection(Plane.WorldXY));
					curve.Transform(pageTransform);
					Guid guid = DrawingBuilder.AddGeneratedCurve(doc, curve, layerIndex, groupIndex, name);
					if (guid != Guid.Empty)
					{
						list.Add(guid);
					}
				}
			}
			return list;
		}
		catch (Exception ex)
		{
			RhinoApp.WriteLine("ExplodeBook：矢量隐藏线计算失败，已改用边线投影：" + ex.Message);
			return AddFallbackProjection(doc, list2, pageTransform, layerIndex, groupIndex, name);
		}
	}

	private static ViewportInfo CreateTopViewport()
	{
		ViewportInfo viewportInfo = new ViewportInfo();
		viewportInfo.SetCameraLocation(new Point3d(0.0, 0.0, 100000.0));
		viewportInfo.SetCameraDirection(-Vector3d.ZAxis);
		viewportInfo.SetCameraUp(Vector3d.YAxis);
		viewportInfo.ChangeToParallelProjection(symmetricFrustum: true);
		return viewportInfo;
	}

	private static List<Guid> AddFallbackProjection(RhinoDoc doc, IEnumerable<ProjectedPart> projectedParts, Transform pageTransform, int layerIndex, int groupIndex, string name)
	{
		List<Guid> list = new List<Guid>();
		foreach (ProjectedPart projectedPart in projectedParts)
		{
			foreach (RhinoObject @object in projectedPart.Part.Objects)
			{
				foreach (Curve item in ExtractEdges(@object.Geometry))
				{
					Curve curve = item.DuplicateCurve();
					if (curve != null)
					{
						curve.Transform(projectedPart.Transform);
						curve.Transform(Transform.PlanarProjection(Plane.WorldXY));
						curve.Transform(pageTransform);
						Guid guid = DrawingBuilder.AddGeneratedCurve(doc, curve, layerIndex, groupIndex, name + "_边线");
						if (guid != Guid.Empty)
						{
							list.Add(guid);
						}
					}
				}
			}
		}
		return list;
	}

	private static IEnumerable<Curve> ExtractEdges(GeometryBase geometry)
	{
		if (geometry is Curve curve)
		{
			yield return curve;
		}
		else if (geometry is Brep brep)
		{
			Curve[] array = brep.DuplicateEdgeCurves();
			for (int i = 0; i < array.Length; i++)
			{
				yield return array[i];
			}
		}
		else if (geometry is Extrusion extrusion)
		{
			Brep brep2 = extrusion.ToBrep();
			if (brep2 != null)
			{
				Curve[] array = brep2.DuplicateEdgeCurves();
				for (int i = 0; i < array.Length; i++)
				{
					yield return array[i];
				}
			}
		}
		else if (geometry is Mesh mesh)
		{
			Polyline[] array2 = mesh.GetNakedEdges() ?? new Polyline[0];
			foreach (Polyline points in array2)
			{
				yield return new PolylineCurve(points);
			}
		}
	}
}
