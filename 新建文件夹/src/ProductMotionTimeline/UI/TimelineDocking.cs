using System;
using Rhino;
using Rhino.UI;
using RhinoWindows.Controls;

namespace ProductMotionTimeline.UI
{
  internal static class TimelineDocking
  {
    public static bool OpenAtBottom()
    {
      try
      {
        Panels.OpenPanel(TimelinePanel.PanelId);
        var dockBarId = Panels.PanelDockBar(TimelinePanel.PanelId);
        if (dockBarId == Guid.Empty)
          return false;
        DockBar.Dock(dockBarId, DockBarDockLocation.Bottom);
        DockBar.Show(dockBarId, false);
        DockBar.RecalcRhinoLayout(true);
        return true;
      }
      catch (Exception exception)
      {
        RhinoApp.WriteLine(
          "ProductMotion：无法自动移到底部，已保留当前面板位置：{0}",
          exception.Message);
        Panels.OpenPanel(TimelinePanel.PanelId);
        return false;
      }
    }
  }
}
