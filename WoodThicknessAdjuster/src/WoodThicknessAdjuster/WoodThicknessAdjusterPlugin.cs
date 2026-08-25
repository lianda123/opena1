using Rhino.PlugIns;

namespace WoodThicknessAdjuster
{
  public sealed class WoodThicknessAdjusterPlugin : PlugIn
  {
    public WoodThicknessAdjusterPlugin()
    {
      Instance = this;
    }

    public static WoodThicknessAdjusterPlugin Instance { get; private set; }
  }
}
