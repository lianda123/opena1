using Rhino.PlugIns;
using WoodExport.Core;

namespace WoodExport
{
  public sealed class WoodExportPlugin : PlugIn
  {
    public WoodExportPlugin()
    {
      Instance = this;
    }

    public static WoodExportPlugin Instance { get; private set; }
    internal static ExportSettings CurrentSettings { get; } = new ExportSettings();
  }
}
