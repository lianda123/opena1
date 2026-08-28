using System;
using Rhino;
using Rhino.UI;

namespace ProductMotionTimeline.UI
{
  internal static class TimelineDocking
  {
    public static void Initialize(ProductMotionPlugin plugIn)
    {
      Panels.RegisterPanel(plugIn, typeof(TimelinePanel), "产品动态时间轴", null);
    }

    public static bool Open()
    {
      try
      {
        Panels.OpenPanel(TimelinePanel.PanelId);
        return true;
      }
      catch (Exception exception)
      {
        RhinoApp.WriteLine("ProductMotion：无法打开时间轴侧边面板：{0}", exception.Message);
        return false;
      }
    }
  }
}
