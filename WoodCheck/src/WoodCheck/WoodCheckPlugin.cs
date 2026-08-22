using Rhino.PlugIns;
using WoodCheck.Core;

namespace WoodCheck
{
  public sealed class WoodCheckPlugin : PlugIn
  {
    public WoodCheckPlugin()
    {
      Instance = this;
    }

    public static WoodCheckPlugin Instance { get; private set; }

    internal static CheckSettings CurrentSettings { get; } = new CheckSettings();
  }
}
