using System;
using Rhino.Geometry;

namespace ProductMotionTimeline.Core
{
  internal struct QuaternionValue
  {
    public double X;
    public double Y;
    public double Z;
    public double W;

    public QuaternionValue(double x, double y, double z, double w)
    {
      X = x;
      Y = y;
      Z = z;
      W = w;
    }

    public static QuaternionValue Identity => new QuaternionValue(0.0, 0.0, 0.0, 1.0);

    public QuaternionValue Normalized()
    {
      var length = Math.Sqrt(X * X + Y * Y + Z * Z + W * W);
      if (length < 1e-12)
        return Identity;
      return new QuaternionValue(X / length, Y / length, Z / length, W / length);
    }

    public static QuaternionValue Slerp(QuaternionValue a, QuaternionValue b, double t)
    {
      a = a.Normalized();
      b = b.Normalized();
      var dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
      if (dot < 0.0)
      {
        b = new QuaternionValue(-b.X, -b.Y, -b.Z, -b.W);
        dot = -dot;
      }

      if (dot > 0.9995)
      {
        return new QuaternionValue(
          a.X + (b.X - a.X) * t,
          a.Y + (b.Y - a.Y) * t,
          a.Z + (b.Z - a.Z) * t,
          a.W + (b.W - a.W) * t).Normalized();
      }

      dot = Math.Max(-1.0, Math.Min(1.0, dot));
      var theta = Math.Acos(dot);
      var sinTheta = Math.Sin(theta);
      if (Math.Abs(sinTheta) < 1e-12)
        return a;

      var wa = Math.Sin((1.0 - t) * theta) / sinTheta;
      var wb = Math.Sin(t * theta) / sinTheta;
      return new QuaternionValue(
        a.X * wa + b.X * wb,
        a.Y * wa + b.Y * wb,
        a.Z * wa + b.Z * wb,
        a.W * wa + b.W * wb).Normalized();
    }

    public static QuaternionValue Multiply(QuaternionValue a, QuaternionValue b)
    {
      return new QuaternionValue(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z).Normalized();
    }

    public static QuaternionValue FromAxisAngle(RotationAxis axis, double degrees)
    {
      var halfAngle = degrees * Math.PI / 360.0;
      var sine = Math.Sin(halfAngle);
      var cosine = Math.Cos(halfAngle);
      switch (axis)
      {
        case RotationAxis.Y:
          return new QuaternionValue(0.0, sine, 0.0, cosine);
        case RotationAxis.Z:
          return new QuaternionValue(0.0, 0.0, sine, cosine);
        default:
          return new QuaternionValue(sine, 0.0, 0.0, cosine);
      }
    }

    public static QuaternionValue FromRotationMatrix(
      double m00, double m01, double m02,
      double m10, double m11, double m12,
      double m20, double m21, double m22)
    {
      double x;
      double y;
      double z;
      double w;
      var trace = m00 + m11 + m22;

      if (trace > 0.0)
      {
        var s = Math.Sqrt(trace + 1.0) * 2.0;
        w = 0.25 * s;
        x = (m21 - m12) / s;
        y = (m02 - m20) / s;
        z = (m10 - m01) / s;
      }
      else if (m00 > m11 && m00 > m22)
      {
        var s = Math.Sqrt(1.0 + m00 - m11 - m22) * 2.0;
        w = (m21 - m12) / s;
        x = 0.25 * s;
        y = (m01 + m10) / s;
        z = (m02 + m20) / s;
      }
      else if (m11 > m22)
      {
        var s = Math.Sqrt(1.0 + m11 - m00 - m22) * 2.0;
        w = (m02 - m20) / s;
        x = (m01 + m10) / s;
        y = 0.25 * s;
        z = (m12 + m21) / s;
      }
      else
      {
        var s = Math.Sqrt(1.0 + m22 - m00 - m11) * 2.0;
        w = (m10 - m01) / s;
        x = (m02 + m20) / s;
        y = (m12 + m21) / s;
        z = 0.25 * s;
      }

      return new QuaternionValue(x, y, z, w).Normalized();
    }
  }

  internal static class AnimationMath
  {
    public static QuaternionValue FromEulerDegrees(Vector3d eulerDegrees)
    {
      var halfX = eulerDegrees.X * Math.PI / 360.0;
      var halfY = eulerDegrees.Y * Math.PI / 360.0;
      var halfZ = eulerDegrees.Z * Math.PI / 360.0;
      var sx = Math.Sin(halfX);
      var cx = Math.Cos(halfX);
      var sy = Math.Sin(halfY);
      var cy = Math.Cos(halfY);
      var sz = Math.Sin(halfZ);
      var cz = Math.Cos(halfZ);
      return new QuaternionValue(
        sx * cy * cz - cx * sy * sz,
        cx * sy * cz + sx * cy * sz,
        cx * cy * sz - sx * sy * cz,
        cx * cy * cz + sx * sy * sz).Normalized();
    }

    public static Vector3d ToEulerDegrees(QuaternionValue rotation)
    {
      var q = rotation.Normalized();
      var roll = Math.Atan2(
        2.0 * (q.W * q.X + q.Y * q.Z),
        1.0 - 2.0 * (q.X * q.X + q.Y * q.Y));
      var sinPitch = 2.0 * (q.W * q.Y - q.Z * q.X);
      var pitch = Math.Abs(sinPitch) >= 1.0
        ? (sinPitch >= 0.0 ? Math.PI / 2.0 : -Math.PI / 2.0)
        : Math.Asin(sinPitch);
      var yaw = Math.Atan2(
        2.0 * (q.W * q.Z + q.X * q.Y),
        1.0 - 2.0 * (q.Y * q.Y + q.Z * q.Z));
      const double degrees = 180.0 / Math.PI;
      return new Vector3d(roll * degrees, pitch * degrees, yaw * degrees);
    }

    public static Transform Compose(Pose pose, RotationAxis rotationAxis)
    {
      var axisRotation = QuaternionValue.FromAxisAngle(rotationAxis, pose.AxisAngleDegrees);
      var q = QuaternionValue.Multiply(axisRotation, pose.Rotation).Normalized();
      var xx = q.X * q.X;
      var yy = q.Y * q.Y;
      var zz = q.Z * q.Z;
      var xy = q.X * q.Y;
      var xz = q.X * q.Z;
      var yz = q.Y * q.Z;
      var wx = q.W * q.X;
      var wy = q.W * q.Y;
      var wz = q.W * q.Z;

      var result = Transform.Identity;
      result.M00 = (1.0 - 2.0 * (yy + zz)) * pose.Scale.X;
      result.M01 = (2.0 * (xy - wz)) * pose.Scale.Y;
      result.M02 = (2.0 * (xz + wy)) * pose.Scale.Z;
      result.M10 = (2.0 * (xy + wz)) * pose.Scale.X;
      result.M11 = (1.0 - 2.0 * (xx + zz)) * pose.Scale.Y;
      result.M12 = (2.0 * (yz - wx)) * pose.Scale.Z;
      result.M20 = (2.0 * (xz - wy)) * pose.Scale.X;
      result.M21 = (2.0 * (yz + wx)) * pose.Scale.Y;
      result.M22 = (1.0 - 2.0 * (xx + yy)) * pose.Scale.Z;
      result.M03 = pose.Translation.X;
      result.M13 = pose.Translation.Y;
      result.M23 = pose.Translation.Z;
      return result;
    }

    public static bool TryDecompose(Transform transform, out Pose pose)
    {
      pose = Pose.Identity;

      var x = new Vector3d(transform.M00, transform.M10, transform.M20);
      var y = new Vector3d(transform.M01, transform.M11, transform.M21);
      var z = new Vector3d(transform.M02, transform.M12, transform.M22);
      var sx = x.Length;
      var sy = y.Length;
      var sz = z.Length;
      if (sx < 1e-10 || sy < 1e-10 || sz < 1e-10)
        return false;

      x /= sx;
      y /= sy;
      z /= sz;
      if (Vector3d.Multiply(Vector3d.CrossProduct(x, y), z) < 0.0)
      {
        sz = -sz;
        z = -z;
      }

      pose = new Pose
      {
        Translation = new Vector3d(transform.M03, transform.M13, transform.M23),
        Rotation = QuaternionValue.FromRotationMatrix(
          x.X, y.X, z.X,
          x.Y, y.Y, z.Y,
          x.Z, y.Z, z.Z),
        Scale = new Vector3d(sx, sy, sz)
      };
      return true;
    }

    public static bool AlmostEqual(Transform a, Transform b, double tolerance = 1e-9)
    {
      for (var row = 0; row < 4; row++)
      {
        for (var column = 0; column < 4; column++)
        {
          if (Math.Abs(a[row, column] - b[row, column]) > tolerance)
            return false;
        }
      }
      return true;
    }

    public static double SmoothStep(double t)
    {
      t = Math.Max(0.0, Math.Min(1.0, t));
      return t * t * (3.0 - 2.0 * t);
    }

    public static double ExtractAxisRotationDegrees(
      QuaternionValue rotation,
      RotationAxis axis)
    {
      var q = rotation.Normalized();
      double component;
      switch (axis)
      {
        case RotationAxis.Y:
          component = q.Y;
          break;
        case RotationAxis.Z:
          component = q.Z;
          break;
        default:
          component = q.X;
          break;
      }

      var twistLength = Math.Sqrt(q.W * q.W + component * component);
      if (twistLength < 1e-12)
        return 0.0;
      var angle = 2.0 * Math.Atan2(
        component / twistLength,
        q.W / twistLength) * 180.0 / Math.PI;
      while (angle > 180.0)
        angle -= 360.0;
      while (angle <= -180.0)
        angle += 360.0;
      return angle;
    }

    public static double MechanicalAngleDegrees(Pose pose, RotationAxis axis)
    {
      if (pose == null)
        return 0.0;
      return pose.AxisAngleDegrees + ExtractAxisRotationDegrees(pose.Rotation, axis);
    }
  }
}
