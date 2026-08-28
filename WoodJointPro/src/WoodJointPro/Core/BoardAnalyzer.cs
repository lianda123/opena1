using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace WoodJointPro.Core
{
  internal static class BoardAnalyzer
  {
    internal static bool TryAnalyze(
      RhinoObject rhinoObject,
      double tolerance,
      out BoardInfo board)
    {
      board = null;
      if (rhinoObject == null || rhinoObject.Geometry == null)
        return false;
      var brep = ToBrep(rhinoObject.Geometry);
      if (brep == null || !brep.IsValid || !brep.IsSolid)
        return false;

      var faces = new List<FaceData>();
      for (var index = 0; index < brep.Faces.Count; index++)
      {
        Plane plane;
        if (!brep.Faces[index].TryGetPlane(out plane, tolerance * 10.0))
          continue;
        var properties = AreaMassProperties.Compute(brep.Faces[index]);
        if (properties == null || properties.Area <= tolerance * tolerance)
          continue;
        faces.Add(new FaceData
        {
          Index = index,
          Plane = plane,
          Area = properties.Area,
          Centroid = properties.Centroid
        });
      }
      if (faces.Count < 2)
        return false;

      PairData best = null;
      var maximumArea = faces.Max(item => item.Area);
      var diagonal = brep.GetBoundingBox(true).Diagonal.Length;
      var minimumParallel = Math.Cos(Rhino.RhinoMath.ToRadians(2.0));
      for (var leftIndex = 0; leftIndex < faces.Count - 1; leftIndex++)
      {
        for (var rightIndex = leftIndex + 1; rightIndex < faces.Count; rightIndex++)
        {
          var left = faces[leftIndex];
          var right = faces[rightIndex];
          if (Math.Abs(Vector3d.Multiply(left.Plane.Normal, right.Plane.Normal)) < minimumParallel)
            continue;
          var distance = Math.Abs(left.Plane.DistanceTo(right.Centroid));
          if (distance <= tolerance * 2.0 || distance >= diagonal * 0.5)
            continue;
          var smaller = Math.Min(left.Area, right.Area);
          var larger = Math.Max(left.Area, right.Area);
          var balance = smaller / Math.Max(larger, tolerance * tolerance);
          if (balance < 0.25 || smaller < maximumArea * 0.08)
            continue;
          var score = smaller * (0.5 + 0.5 * balance) / Math.Max(distance, tolerance);
          if (best == null || score > best.Score)
          {
            best = new PairData
            {
              First = left,
              Second = right,
              Distance = distance,
              Score = score
            };
          }
        }
      }
      if (best == null)
        return false;

      var normal = best.First.Plane.Normal;
      if (!normal.Unitize())
        return false;
      var signedDistance = best.First.Plane.DistanceTo(best.Second.Centroid);
      var midOrigin = best.First.Plane.Origin + normal * (signedDistance * 0.5);
      var midPlane = new Plane(midOrigin, best.First.Plane.XAxis, best.First.Plane.YAxis);
      var volume = VolumeMassProperties.Compute(brep);
      board = new BoardInfo
      {
        Object = rhinoObject,
        Brep = brep,
        FirstFaceIndex = best.First.Index,
        SecondFaceIndex = best.Second.Index,
        FirstPlane = best.First.Plane,
        SecondPlane = best.Second.Plane,
        MidPlane = midPlane,
        Centroid = volume == null ? brep.GetBoundingBox(true).Center : volume.Centroid,
        Thickness = best.Distance,
        Score = best.Score,
        Bounds = brep.GetBoundingBox(true)
      };
      return true;
    }

    internal static Brep ToBrep(GeometryBase geometry)
    {
      var brep = geometry as Brep;
      if (brep != null)
        return brep.DuplicateBrep();
      var extrusion = geometry as Extrusion;
      return extrusion == null ? null : extrusion.ToBrep();
    }

    internal static bool TryGetFacePlane(Brep brep, int faceIndex, double tolerance, out Plane plane)
    {
      plane = Plane.Unset;
      return brep != null && faceIndex >= 0 && faceIndex < brep.Faces.Count &&
        brep.Faces[faceIndex].TryGetPlane(out plane, tolerance * 10.0);
    }

    private sealed class FaceData
    {
      public int Index { get; set; }
      public Plane Plane { get; set; }
      public Point3d Centroid { get; set; }
      public double Area { get; set; }
    }

    private sealed class PairData
    {
      public FaceData First { get; set; }
      public FaceData Second { get; set; }
      public double Distance { get; set; }
      public double Score { get; set; }
    }
  }
}
