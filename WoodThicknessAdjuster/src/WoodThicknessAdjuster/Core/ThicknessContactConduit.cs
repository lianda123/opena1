using System;
using System.Drawing;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

namespace WoodThicknessAdjuster.Core
{
  internal sealed class ThicknessContactConduit : DisplayConduit, IDisposable
  {
    private Point3d _boardPoint = Point3d.Unset;
    private Point3d _targetPoint = Point3d.Unset;
    private Vector3d _boardNormal = Vector3d.Unset;
    private Vector3d _targetNormal = Vector3d.Unset;
    private double _markerLength = 1.0;
    private string _verificationText;
    private bool _verificationPassed;

    internal void ShowBoard(
      Point3d boardPoint,
      Vector3d boardNormal,
      double markerLength)
    {
      _boardPoint = boardPoint;
      _boardNormal = boardNormal;
      _targetPoint = Point3d.Unset;
      _targetNormal = Vector3d.Unset;
      _verificationText = null;
      _markerLength = Math.Max(markerLength, 1e-6);
      _verificationText = null;
      Enabled = true;
      Redraw();
    }

    internal void ShowVerification(bool passed, string text)
    {
      _verificationPassed = passed;
      _verificationText = text;
      Redraw();
    }

    internal void ShowContact(
      Point3d boardPoint,
      Vector3d boardNormal,
      Point3d targetPoint,
      Vector3d targetNormal,
      double markerLength)
    {
      _boardPoint = boardPoint;
      _boardNormal = boardNormal;
      _targetPoint = targetPoint;
      _targetNormal = targetNormal;
      _markerLength = Math.Max(markerLength, 1e-6);
      Enabled = true;
      Redraw();
    }

    internal void Clear()
    {
      Enabled = false;
      _boardPoint = Point3d.Unset;
      _targetPoint = Point3d.Unset;
      _verificationText = null;
      Redraw();
    }

    protected override void DrawForeground(DrawEventArgs e)
    {
      if (!_boardPoint.IsValid)
        return;
      var boardColor = Color.FromArgb(70, 210, 120);
      var targetColor = Color.FromArgb(55, 200, 255);
      var boardNormal = Unitized(_boardNormal, Vector3d.ZAxis);
      e.Display.DrawPoint(
        _boardPoint,
        PointStyle.RoundSimple,
        8,
        boardColor);
      e.Display.DrawLine(
        new Line(_boardPoint, _boardPoint + boardNormal * _markerLength),
        boardColor,
        3);
      e.Display.DrawDot(
        _boardPoint + boardNormal * (_markerLength * 1.15),
        "调整板贴合面",
        boardColor,
        Color.Black);

      if (!_targetPoint.IsValid)
        return;
      var targetNormal = Unitized(_targetNormal, boardNormal);
      e.Display.DrawPoint(
        _targetPoint,
        PointStyle.RoundSimple,
        8,
        targetColor);
      e.Display.DrawLine(
        new Line(_targetPoint, _targetPoint + targetNormal * _markerLength),
        targetColor,
        3);
      e.Display.DrawLine(
        new Line(_boardPoint, _targetPoint),
        Color.FromArgb(255, 180, 55),
        2);
      e.Display.DrawDot(
        _targetPoint + targetNormal * (_markerLength * 1.15),
        "目标贴合面",
        targetColor,
        Color.Black);
      if (!string.IsNullOrWhiteSpace(_verificationText))
      {
        var statusColor = _verificationPassed
          ? Color.FromArgb(70, 210, 120)
          : Color.FromArgb(235, 80, 75);
        e.Display.DrawDot(
          (_boardPoint + _targetPoint) * 0.5,
          _verificationText,
          statusColor,
          Color.Black);
      }
    }

    public void Dispose()
    {
      Clear();
    }

    private static Vector3d Unitized(Vector3d vector, Vector3d fallback)
    {
      var result = vector;
      if (!result.IsValid || !result.Unitize())
      {
        result = fallback;
        result.Unitize();
      }
      return result;
    }

    private static void Redraw()
    {
      var doc = RhinoDoc.ActiveDoc;
      if (doc != null)
        doc.Views.Redraw();
    }
  }
}
