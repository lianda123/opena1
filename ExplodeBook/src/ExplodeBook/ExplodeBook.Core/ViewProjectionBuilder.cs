using System.Globalization;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

namespace ExplodeBook.Core;

internal static class ViewProjectionBuilder
{
	private static readonly Vector3d ReferenceDirection = new Vector3d(-0.458773, 0.766576, -0.449319);

	private static readonly Vector3d ReferenceUp = new Vector3d(-0.230739, 0.385548, 0.893371);

	public static void UseReferenceView(ExplodeSettings settings)
	{
		settings.ViewDirection = ReferenceDirection;
		settings.ViewUp = ReferenceUp;
		settings.ParallelViewLocked = true;
	}

	public static bool CaptureActiveParallelView(RhinoDoc doc, ExplodeSettings settings)
	{
		if (doc == null || doc.Views.ActiveView == null || settings == null)
		{
			return false;
		}
		RhinoViewport activeViewport = doc.Views.ActiveView.ActiveViewport;
		if (activeViewport == null)
		{
			return false;
		}
		activeViewport.ChangeToParallelProjection(symmetricFrustum: true);
		Vector3d direction = activeViewport.CameraDirection;
		Vector3d up = activeViewport.CameraUp;
		if (!TryNormalizeFrame(ref direction, ref up))
		{
			return false;
		}
		settings.ViewDirection = direction;
		settings.ViewUp = up;
		settings.ParallelViewLocked = true;
		doc.Views.ActiveView.Redraw();
		return true;
	}

	public static Transform Orientation(ExplodeSettings settings)
	{
		Vector3d direction = settings?.ViewDirection ?? ReferenceDirection;
		Vector3d up = settings?.ViewUp ?? ReferenceUp;
		if (!TryNormalizeFrame(ref direction, ref up))
		{
			direction = ReferenceDirection;
			up = ReferenceUp;
			TryNormalizeFrame(ref direction, ref up);
		}
		Vector3d a = Vector3d.CrossProduct(direction, up);
		a.Unitize();
		Vector3d vector3d = Vector3d.CrossProduct(a, direction);
		vector3d.Unitize();
		Vector3d vector3d2 = -direction;
		vector3d2.Unitize();
		Transform identity = Transform.Identity;
		identity.M00 = a.X;
		identity.M01 = a.Y;
		identity.M02 = a.Z;
		identity.M10 = vector3d.X;
		identity.M11 = vector3d.Y;
		identity.M12 = vector3d.Z;
		identity.M20 = vector3d2.X;
		identity.M21 = vector3d2.Y;
		identity.M22 = vector3d2.Z;
		return identity;
	}

	public static string Describe(ExplodeSettings settings)
	{
		if (settings == null)
		{
			return "参考轴测平行视角";
		}
		return string.Format(CultureInfo.InvariantCulture, "平行锁定 D({0:0.###},{1:0.###},{2:0.###})", settings.ViewDirection.X, settings.ViewDirection.Y, settings.ViewDirection.Z);
	}

	private static bool TryNormalizeFrame(ref Vector3d direction, ref Vector3d up)
	{
		if (!direction.IsValid || !up.IsValid || direction.IsTiny() || up.IsTiny())
		{
			return false;
		}
		if (!direction.Unitize() || !up.Unitize())
		{
			return false;
		}
		Vector3d a = Vector3d.CrossProduct(direction, up);
		if (!a.Unitize())
		{
			return false;
		}
		up = Vector3d.CrossProduct(a, direction);
		return up.Unitize();
	}
}
