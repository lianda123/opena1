using Rhino.PlugIns;

namespace WoodSheetLayout
{
  public sealed class WoodSheetLayoutPlugin : PlugIn
  {
    public WoodSheetLayoutPlugin()
    {
      Instance = this;
    }

    public static WoodSheetLayoutPlugin Instance { get; private set; }
  }
}
