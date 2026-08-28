using System;
using Rhino;
using Rhino.UI;
using RhinoWindows.Controls;

#if NETFRAMEWORK
using Eto.Drawing;
using Eto.Forms;
using Rhino.PlugIns;
#endif

namespace ProductMotionTimeline.UI
{
  internal static class TimelineDocking
  {
#if NETFRAMEWORK
    private static Rhino7TimelineDockBar _rhino7DockBar;
    private static bool _rhino7InitialBottomApplied;
#else
    private static bool _rhino8InitialBottomApplied;
#endif

    public static void Initialize(ProductMotionPlugin plugIn)
    {
#if NETFRAMEWORK
      if (_rhino7DockBar != null)
        return;

      var options = new DockBarCreateOptions
      {
        DockLocation = DockBarDockLocation.Bottom,
        Visible = false,
        DockStyle = DockBarDockStyle.Any,
        FloatPoint = new System.Drawing.Point(100, 100)
      };
      _rhino7DockBar = new Rhino7TimelineDockBar(plugIn);
      _rhino7DockBar.Create(options);
#else
      Panels.RegisterPanel(plugIn, typeof(TimelinePanel), "产品动态时间轴", null);
#endif
    }

    public static bool Open()
    {
      try
      {
#if NETFRAMEWORK
        if (_rhino7DockBar == null && ProductMotionPlugin.Instance != null)
          Initialize(ProductMotionPlugin.Instance);
        var shown = DockBar.Show(Rhino7TimelineDockBar.BarId, false);
        if (!_rhino7InitialBottomApplied)
        {
          _rhino7InitialBottomApplied = true;
          DockBar.Dock(Rhino7TimelineDockBar.BarId, DockBarDockLocation.Bottom);
          DockBar.RecalcRhinoLayout(true);
        }
        return shown;
#else
        Panels.OpenPanel(TimelinePanel.PanelId);
        if (!_rhino8InitialBottomApplied)
        {
          _rhino8InitialBottomApplied = true;
          DockRegisteredPanelBottom();
        }
        return true;
#endif
      }
      catch (Exception exception)
      {
        RhinoApp.WriteLine("ProductMotion：无法打开时间轴：{0}", exception.Message);
        return false;
      }
    }

    public static bool ResetToBottom()
    {
      try
      {
#if NETFRAMEWORK
        if (_rhino7DockBar == null && ProductMotionPlugin.Instance != null)
          Initialize(ProductMotionPlugin.Instance);
        DockBar.Show(Rhino7TimelineDockBar.BarId, false);
        DockBar.Dock(Rhino7TimelineDockBar.BarId, DockBarDockLocation.Bottom);
#else
        Panels.OpenPanel(TimelinePanel.PanelId);
        DockRegisteredPanelBottom();
        _rhino8InitialBottomApplied = true;
#endif
        DockBar.RecalcRhinoLayout(true);
        RhinoApp.WriteLine("ProductMotion：时间轴已恢复到 Rhino 窗口底部。");
        return true;
      }
      catch (Exception exception)
      {
        RhinoApp.WriteLine("ProductMotion：恢复底部布局失败：{0}", exception.Message);
        return false;
      }
    }

#if !NETFRAMEWORK
    private static void DockRegisteredPanelBottom()
    {
      var dockBarId = Panels.PanelDockBar(TimelinePanel.PanelId);
      if (dockBarId != Guid.Empty)
        DockBar.Dock(dockBarId, DockBarDockLocation.Bottom);
    }
#endif
  }

#if NETFRAMEWORK
  internal sealed class Rhino7TimelineDockBar : DockBar
  {
    public static readonly Guid BarId = new Guid("36B3B1C0-2AE6-4B5A-989A-A78671C41427");
    private readonly Form _hostWindow;

    public Rhino7TimelineDockBar(PlugIn plugIn)
      : base(plugIn, BarId, "产品动态时间轴")
    {
      _hostWindow = new Form
      {
        Title = "产品动态时间轴",
        ClientSize = new Size(1200, 340),
        Content = new TimelinePanel()
      };
      SetContentControl(RhinoWindows.Forms.WindowsInterop.ObjectAsIWin32Window(_hostWindow));
    }
  }
#endif
}
