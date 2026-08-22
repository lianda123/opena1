using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino;
using Rhino.Geometry;

namespace MechanismMaker.Core
{
  internal static class GeometryFactory
  {
    private static readonly Color GearColor = Color.FromArgb(205, 132, 42);
    private static readonly Color LinkColor = Color.FromArgb(61, 139, 168);
    private static readonly Color CamColor = Color.FromArgb(155, 92, 176);
    private static readonly Color RatchetColor = Color.FromArgb(199, 83, 71);
    private static readonly Color GenevaColor = Color.FromArgb(67, 151, 103);

    public static GeneratedPart CreateGear(
      double module,
      int teeth,
      double pressureAngleDegrees,
      double backlash,
      double boreDiameter)
    {
      var pitchRadius = module * teeth * 0.5;
      var pressureAngle = RhinoMath.ToRadians(pressureAngleDegrees);
      var baseRadius = pitchRadius * Math.Cos(pressureAngle);
      var outerRadius = pitchRadius + module;
      var rootRadius = Math.Max(module * 0.35, pitchRadius - 1.25 * module);
      var halfToothAngle = Math.PI / (2.0 * teeth) - backlash / (2.0 * pitchRadius);
      if (halfToothAngle <= RhinoMath.ToRadians(0.1))
        throw new ArgumentException("侧隙过大，齿厚已经小于零。", nameof(backlash));

      var pitchInvolute = InvoluteAngle(baseRadius, pitchRadius);
      var startRadius = Math.Max(rootRadius, baseRadius);
      var startInvolute = InvoluteAngle(baseRadius, startRadius);
      var startHalfAngle = halfToothAngle + pitchInvolute - startInvolute;
      var points = new List<Point3d>();
      const int flankSteps = 7;
      const int tipSteps = 3;
      const int rootSteps = 3;

      for (var tooth = 0; tooth < teeth; tooth++)
      {
        var centerAngle = tooth * 2.0 * Math.PI / teeth;
        AddPolar(points, rootRadius, centerAngle - startHalfAngle);
        if (startRadius > rootRadius + 1e-9)
          AddPolar(points, startRadius, centerAngle - startHalfAngle);

        for (var step = 1; step <= flankSteps; step++)
        {
          var radius = startRadius + (outerRadius - startRadius) * step / flankSteps;
          var angle = halfToothAngle + pitchInvolute - InvoluteAngle(baseRadius, radius);
          AddPolar(points, radius, centerAngle - angle);
        }

        var outerHalfAngle = halfToothAngle + pitchInvolute - InvoluteAngle(baseRadius, outerRadius);
        for (var step = 1; step <= tipSteps; step++)
        {
          var angle = centerAngle - outerHalfAngle + 2.0 * outerHalfAngle * step / tipSteps;
          AddPolar(points, outerRadius, angle);
        }

        for (var step = flankSteps - 1; step >= 0; step--)
        {
          var radius = startRadius + (outerRadius - startRadius) * step / flankSteps;
          var angle = halfToothAngle + pitchInvolute - InvoluteAngle(baseRadius, radius);
          AddPolar(points, radius, centerAngle + angle);
        }
        if (startRadius > rootRadius + 1e-9)
          AddPolar(points, rootRadius, centerAngle + startHalfAngle);

        var currentRootAngle = centerAngle + startHalfAngle;
        var nextRootAngle = (tooth + 1) * 2.0 * Math.PI / teeth - startHalfAngle;
        for (var step = 1; step <= rootSteps; step++)
        {
          var rootAngle = currentRootAngle + (nextRootAngle - currentRootAngle) * step / rootSteps;
          AddPolar(points, rootRadius, rootAngle);
        }
      }

      var part = new GeneratedPart("渐开线齿轮_Z" + teeth, "Gear", GearColor);
      part.Curves.Add(CreateClosedPolyline(points));
      part.Curves.Add(CreateCircle(Point3d.Origin, boreDiameter * 0.5));
      part.Metadata["MM.Teeth"] = teeth.ToString();
      part.Metadata["MM.Module"] = module.ToString("0.###");
      part.Metadata["MM.PressureAngle"] = pressureAngleDegrees.ToString("0.###");
      part.Metadata["MM.PitchDiameter"] = (pitchRadius * 2.0).ToString("0.###");
      part.Metadata["MM.Bore"] = boreDiameter.ToString("0.###");
      return part;
    }

    public static GeneratedPart CreateRack(
      double module,
      int teeth,
      double pressureAngleDegrees,
      double backlash,
      double bodyHeight)
    {
      var pitch = Math.PI * module;
      var addendum = module;
      var dedendum = 1.25 * module;
      var pressureAngle = RhinoMath.ToRadians(pressureAngleDegrees);
      var halfAtPitch = Math.Max(module * 0.10, pitch * 0.25 - backlash * 0.5);
      var tipHalf = Math.Max(module * 0.08, halfAtPitch - addendum * Math.Tan(pressureAngle));
      var rootHalf = Math.Min(pitch * 0.48, halfAtPitch + dedendum * Math.Tan(pressureAngle));
      var length = teeth * pitch;
      var rootY = -dedendum;
      var bottomY = rootY - bodyHeight;
      var points = new List<Point3d>
      {
        new Point3d(0.0, bottomY, 0.0),
        new Point3d(0.0, rootY, 0.0)
      };

      for (var tooth = 0; tooth < teeth; tooth++)
      {
        var center = (tooth + 0.5) * pitch;
        points.Add(new Point3d(Math.Max(tooth * pitch, center - rootHalf), rootY, 0.0));
        points.Add(new Point3d(center - tipHalf, addendum, 0.0));
        points.Add(new Point3d(center + tipHalf, addendum, 0.0));
        points.Add(new Point3d(Math.Min((tooth + 1) * pitch, center + rootHalf), rootY, 0.0));
      }

      points.Add(new Point3d(length, rootY, 0.0));
      points.Add(new Point3d(length, bottomY, 0.0));
      var part = new GeneratedPart("渐开线齿条_Z" + teeth, "Rack", GearColor);
      part.Curves.Add(CreateClosedPolyline(points));
      part.Metadata["MM.Teeth"] = teeth.ToString();
      part.Metadata["MM.Module"] = module.ToString("0.###");
      part.Metadata["MM.Pitch"] = pitch.ToString("0.###");
      part.Metadata["MM.PressureAngle"] = pressureAngleDegrees.ToString("0.###");
      return part;
    }

    public static GeneratedPart CreateCam(
      CamKind kind,
      double baseRadius,
      double lift,
      double boreDiameter)
    {
      var part = new GeneratedPart("凸轮_" + kind, "Cam", CamColor);
      if (kind == CamKind.Eccentric)
      {
        var eccentricity = lift * 0.5;
        var discRadius = baseRadius + eccentricity;
        part.Curves.Add(CreateCircle(new Point3d(eccentricity, 0.0, 0.0), discRadius));
      }
      else
      {
        const int samples = 240;
        var points = new List<Point3d>();
        for (var index = 0; index < samples; index++)
        {
          var phase = index / (double)samples;
          var angle = phase * 2.0 * Math.PI;
          double rise;
          if (kind == CamKind.Snail)
          {
            rise = phase;
          }
          else if (kind == CamKind.Pear)
          {
            rise = PearRise(phase);
          }
          else
          {
            rise = 0.5 - 0.5 * Math.Cos(angle);
          }
          AddPolar(points, baseRadius + lift * rise, angle);
        }
        part.Curves.Add(CreateClosedPolyline(points));
      }

      part.Curves.Add(CreateCircle(Point3d.Origin, boreDiameter * 0.5));
      part.Metadata["MM.CamKind"] = kind.ToString();
      part.Metadata["MM.BaseRadius"] = baseRadius.ToString("0.###");
      part.Metadata["MM.Lift"] = lift.ToString("0.###");
      part.Metadata["MM.Bore"] = boreDiameter.ToString("0.###");
      return part;
    }

    public static GeneratedPart CreateCrank(
      double throwDistance,
      double armWidth,
      double shaftHole,
      double pinHole)
    {
      var start = Point3d.Origin;
      var end = new Point3d(throwDistance, 0.0, 0.0);
      var part = new GeneratedPart("曲柄_R" + throwDistance.ToString("0.##"), "Crank", LinkColor);
      part.Curves.Add(CreateCapsule(start, end, armWidth * 0.5));
      part.Curves.Add(CreateCircle(start, shaftHole * 0.5));
      part.Curves.Add(CreateCircle(end, pinHole * 0.5));
      part.Metadata["MM.Throw"] = throwDistance.ToString("0.###");
      part.Metadata["MM.ShaftHole"] = shaftHole.ToString("0.###");
      part.Metadata["MM.PinHole"] = pinHole.ToString("0.###");
      return part;
    }

    public static MechanismAssembly CreateFourBar(
      double groundLength,
      double inputLength,
      double couplerLength,
      double rockerLength,
      double inputAngleDegrees,
      double linkWidth,
      double fixedHole,
      double rotatingHole)
    {
      var assembly = new MechanismAssembly("FourBar");
      var pointA = Point3d.Origin;
      var pointD = new Point3d(groundLength, 0.0, 0.0);
      var angle = RhinoMath.ToRadians(inputAngleDegrees);
      var pointB = new Point3d(inputLength * Math.Cos(angle), inputLength * Math.Sin(angle), 0.0);
      Point3d pointC;
      if (!TryCircleIntersection(pointB, couplerLength, pointD, rockerLength, out pointC))
        throw new ArgumentException("四连杆在当前长度与角度下无法闭合。请修改杆长或输入角度。");

      assembly.Parts.Add(CreateLink("四连杆_机架", "FourBarGround", pointA, pointD, linkWidth, fixedHole, fixedHole, LinkColor));
      assembly.Parts.Add(CreateLink("四连杆_主动曲柄", "FourBarInput", pointA, pointB, linkWidth, rotatingHole, rotatingHole, Color.FromArgb(218, 139, 58)));
      assembly.Parts.Add(CreateLink("四连杆_连杆", "FourBarCoupler", pointB, pointC, linkWidth, rotatingHole, rotatingHole, Color.FromArgb(86, 154, 109)));
      assembly.Parts.Add(CreateLink("四连杆_摇杆", "FourBarRocker", pointC, pointD, linkWidth, rotatingHole, rotatingHole, Color.FromArgb(162, 102, 180)));
      foreach (var part in assembly.Parts)
      {
        part.Metadata["MM.GroundLength"] = groundLength.ToString("0.###");
        part.Metadata["MM.InputLength"] = inputLength.ToString("0.###");
        part.Metadata["MM.CouplerLength"] = couplerLength.ToString("0.###");
        part.Metadata["MM.RockerLength"] = rockerLength.ToString("0.###");
        part.Metadata["MM.InputAngle"] = inputAngleDegrees.ToString("0.###");
      }
      return assembly;
    }

    public static MechanismAssembly CreateRatchet(
      int teeth,
      double rootRadius,
      double toothHeight,
      double boreDiameter,
      double pawlWidth,
      double pawlHole)
    {
      var assembly = new MechanismAssembly("Ratchet");
      var outerRadius = rootRadius + toothHeight;
      var points = new List<Point3d>();
      var pitchAngle = 2.0 * Math.PI / teeth;
      for (var tooth = 0; tooth < teeth; tooth++)
      {
        var start = tooth * pitchAngle;
        AddPolar(points, rootRadius, start);
        AddPolar(points, outerRadius, start + pitchAngle * 0.16);
        AddPolar(points, outerRadius, start + pitchAngle * 0.64);
        AddPolar(points, rootRadius, start + pitchAngle * 0.98);
      }

      var wheel = new GeneratedPart("棘轮_Z" + teeth, "RatchetWheel", RatchetColor);
      wheel.Curves.Add(CreateClosedPolyline(points));
      wheel.Curves.Add(CreateCircle(Point3d.Origin, boreDiameter * 0.5));
      wheel.Metadata["MM.Teeth"] = teeth.ToString();
      wheel.Metadata["MM.RootRadius"] = rootRadius.ToString("0.###");
      assembly.Parts.Add(wheel);

      var pivot = new Point3d(outerRadius + toothHeight * 4.0, 0.0, 0.0);
      var tip = new Point3d(outerRadius - toothHeight * 0.15, 0.0, 0.0);
      var pawl = new GeneratedPart("棘爪", "RatchetPawl", Color.FromArgb(226, 151, 55));
      var pawlPoints = new List<Point3d>
      {
        tip,
        new Point3d(pivot.X - pawlWidth * 0.55, pawlWidth * 0.5, 0.0),
        new Point3d(pivot.X + pawlWidth * 0.55, pawlWidth * 0.5, 0.0),
        new Point3d(pivot.X + pawlWidth * 0.55, -pawlWidth * 0.5, 0.0),
        new Point3d(pivot.X - pawlWidth * 0.55, -pawlWidth * 0.5, 0.0)
      };
      pawl.Curves.Add(CreateClosedPolyline(pawlPoints));
      pawl.Curves.Add(CreateCircle(pivot, pawlHole * 0.5));
      assembly.Parts.Add(pawl);
      return assembly;
    }

    public static MechanismAssembly CreateGeneva(
      int slots,
      double centerDistance,
      double slotWidth,
      double rotatingHole,
      double fixedPinHole,
      double tolerance)
    {
      var assembly = new MechanismAssembly("Geneva");
      var halfIndexAngle = Math.PI / slots;
      var drivePinRadius = centerDistance * Math.Sin(halfIndexAngle);
      var drivenRadius = centerDistance * Math.Cos(halfIndexAngle);
      var slotRootRadius = Math.Max(rotatingHole, drivenRadius - drivePinRadius * 1.35);
      Curve drivenBoundary = CreateCircle(Point3d.Origin, drivenRadius);
      var slotCurves = new List<Curve>();

      for (var slot = 0; slot < slots; slot++)
      {
        var angle = slot * 2.0 * Math.PI / slots;
        var direction = new Vector3d(Math.Cos(angle), Math.Sin(angle), 0.0);
        var normal = new Vector3d(-direction.Y, direction.X, 0.0);
        var inner = Point3d.Origin + direction * slotRootRadius;
        var outer = Point3d.Origin + direction * (drivenRadius + slotWidth * 2.0);
        var half = slotWidth * 0.5;
        var slotCurve = CreateClosedPolyline(new List<Point3d>
        {
          inner - normal * half,
          outer - normal * half,
          outer + normal * half,
          inner + normal * half
        });
        slotCurves.Add(slotCurve);

        var difference = Curve.CreateBooleanDifference(drivenBoundary, slotCurve, tolerance);
        if (difference != null && difference.Length > 0)
        {
          var next = difference.OrderByDescending(item => item.GetLength()).FirstOrDefault();
          if (next != null)
            drivenBoundary = next;
        }
      }

      var driven = new GeneratedPart("日内瓦_从动槽轮_" + slots, "GenevaWheel", GenevaColor);
      driven.Curves.Add(drivenBoundary);
      driven.Curves.Add(CreateCircle(Point3d.Origin, rotatingHole * 0.5));
      driven.Metadata["MM.Slots"] = slots.ToString();
      driven.Metadata["MM.CenterDistance"] = centerDistance.ToString("0.###");
      driven.Metadata["MM.IndexDegrees"] = (360.0 / slots).ToString("0.###");
      assembly.Parts.Add(driven);

      var driveCenter = new Point3d(centerDistance, 0.0, 0.0);
      var driveDiscRadius = Math.Max(drivePinRadius * 0.68, slotWidth * 2.0);
      var pinCenter = driveCenter - new Vector3d(drivePinRadius, 0.0, 0.0);
      var driver = new GeneratedPart("日内瓦_主动轮", "GenevaDriver", Color.FromArgb(219, 147, 55));
      driver.Curves.Add(CreateCircle(driveCenter, driveDiscRadius));
      driver.Curves.Add(CreateCircle(driveCenter, rotatingHole * 0.5));
      driver.Curves.Add(CreateCircle(pinCenter, fixedPinHole * 0.5));
      driver.Metadata["MM.DrivePinRadius"] = drivePinRadius.ToString("0.###");
      driver.Metadata["MM.CenterDistance"] = centerDistance.ToString("0.###");
      assembly.Parts.Add(driver);
      return assembly;
    }

    public static Curve CreateCapsule(Point3d start, Point3d end, double radius)
    {
      var direction = end - start;
      if (!direction.Unitize())
        return CreateCircle(start, radius);
      var angle = Math.Atan2(direction.Y, direction.X);
      var points = new List<Point3d>();
      const int halfSamples = 14;

      for (var index = 0; index <= halfSamples; index++)
      {
        var current = angle + Math.PI * 0.5 - Math.PI * index / halfSamples;
        points.Add(new Point3d(end.X + radius * Math.Cos(current), end.Y + radius * Math.Sin(current), 0.0));
      }
      for (var index = 0; index <= halfSamples; index++)
      {
        var current = angle - Math.PI * 0.5 - Math.PI * index / halfSamples;
        points.Add(new Point3d(start.X + radius * Math.Cos(current), start.Y + radius * Math.Sin(current), 0.0));
      }
      return CreateClosedPolyline(points);
    }

    private static GeneratedPart CreateLink(
      string name,
      string type,
      Point3d start,
      Point3d end,
      double width,
      double startHole,
      double endHole,
      Color color)
    {
      var part = new GeneratedPart(name, type, color);
      part.Curves.Add(CreateCapsule(start, end, width * 0.5));
      part.Curves.Add(CreateCircle(start, startHole * 0.5));
      part.Curves.Add(CreateCircle(end, endHole * 0.5));
      part.Metadata["MM.LinkLength"] = start.DistanceTo(end).ToString("0.###");
      return part;
    }

    private static bool TryCircleIntersection(
      Point3d firstCenter,
      double firstRadius,
      Point3d secondCenter,
      double secondRadius,
      out Point3d upperPoint)
    {
      upperPoint = Point3d.Unset;
      var delta = secondCenter - firstCenter;
      var distance = delta.Length;
      if (distance <= 1e-9 || distance > firstRadius + secondRadius ||
          distance < Math.Abs(firstRadius - secondRadius))
        return false;

      var along = (firstRadius * firstRadius - secondRadius * secondRadius + distance * distance) /
                  (2.0 * distance);
      var heightSquared = firstRadius * firstRadius - along * along;
      if (heightSquared < -1e-9)
        return false;
      var height = Math.Sqrt(Math.Max(0.0, heightSquared));
      delta.Unitize();
      var basePoint = firstCenter + delta * along;
      var normal = new Vector3d(-delta.Y, delta.X, 0.0);
      var candidateA = basePoint + normal * height;
      var candidateB = basePoint - normal * height;
      upperPoint = candidateA.Y >= candidateB.Y ? candidateA : candidateB;
      return true;
    }

    private static double InvoluteAngle(double baseRadius, double radius)
    {
      if (radius <= baseRadius)
        return 0.0;
      var parameter = Math.Sqrt(radius * radius / (baseRadius * baseRadius) - 1.0);
      return parameter - Math.Atan(parameter);
    }

    private static double PearRise(double phase)
    {
      if (phase < 0.15)
        return 0.0;
      if (phase < 0.45)
        return SmoothStep((phase - 0.15) / 0.30);
      if (phase < 0.60)
        return 1.0;
      return 1.0 - SmoothStep((phase - 0.60) / 0.40);
    }

    private static double SmoothStep(double value)
    {
      var t = Math.Max(0.0, Math.Min(1.0, value));
      return t * t * (3.0 - 2.0 * t);
    }

    private static void AddPolar(ICollection<Point3d> points, double radius, double angle)
    {
      points.Add(new Point3d(radius * Math.Cos(angle), radius * Math.Sin(angle), 0.0));
    }

    private static Curve CreateCircle(Point3d center, double radius)
    {
      return new Circle(new Plane(center, Vector3d.ZAxis), radius).ToNurbsCurve();
    }

    private static Curve CreateClosedPolyline(IList<Point3d> source)
    {
      var points = source.ToList();
      if (points.Count < 2)
        throw new ArgumentException("闭合轮廓至少需要两个点。", nameof(source));
      if (points[0].DistanceTo(points[points.Count - 1]) > 1e-9)
        points.Add(points[0]);
      return new PolylineCurve(points);
    }
  }
}
