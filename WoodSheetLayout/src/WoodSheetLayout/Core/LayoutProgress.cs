using System;
using System.Diagnostics;
using Rhino;
using Rhino.UI;

namespace WoodSheetLayout.Core
{
  internal sealed class LayoutProgress : IDisposable
  {
    private readonly Stopwatch _pumpClock = Stopwatch.StartNew();
    private bool _cancelled;
    private bool _shown;
    private int _lastPercent = -1;
    private int _packingSteps;
    private int _packingCompleted;

    public bool IsCancelled => _cancelled;

    public void Start()
    {
      RhinoApp.EscapeKeyPressed += OnEscapeKeyPressed;
      StatusBar.ShowProgressMeter(
        0,
        100,
        "WoodSheetLayout：正在铺平和排版，按 Esc 取消",
        true,
        true);
      _shown = true;
      ReportPercent(0);
    }

    public bool ReportAnalysis(int completed, int total)
    {
      var fraction = total <= 0 ? 1.0 : (double)completed / total;
      return ReportPercent(5 + (int)Math.Round(Math.Max(0.0, Math.Min(1.0, fraction)) * 25.0));
    }

    public void BeginPacking(int totalSteps)
    {
      _packingSteps = Math.Max(1, totalSteps);
      _packingCompleted = 0;
      ReportPercent(30);
    }

    public bool CompletePackingStep()
    {
      _packingCompleted++;
      var fraction = (double)_packingCompleted / _packingSteps;
      return ReportPercent(30 + (int)Math.Round(Math.Min(1.0, fraction) * 60.0));
    }

    public bool ReportOutput(int completed, int total)
    {
      var fraction = total <= 0 ? 1.0 : (double)completed / total;
      return ReportPercent(90 + (int)Math.Round(Math.Max(0.0, Math.Min(1.0, fraction)) * 10.0));
    }

    public bool Pulse()
    {
      if (_pumpClock.ElapsedMilliseconds < 40)
        return !_cancelled;
      _pumpClock.Restart();
      RhinoApp.Wait();
      return !_cancelled;
    }

    private bool ReportPercent(int percent)
    {
      percent = Math.Max(0, Math.Min(100, percent));
      if (_shown && percent != _lastPercent)
      {
        StatusBar.UpdateProgressMeter(percent, true);
        _lastPercent = percent;
      }
      return Pulse();
    }

    private void OnEscapeKeyPressed(object sender, EventArgs eventArgs)
    {
      _cancelled = true;
    }

    public void Dispose()
    {
      RhinoApp.EscapeKeyPressed -= OnEscapeKeyPressed;
      if (_shown)
      {
        StatusBar.HideProgressMeter();
        _shown = false;
      }
    }
  }
}
