using ExplodeBook.Core;
using Rhino.PlugIns;

namespace ExplodeBook;

public sealed class ExplodeBookPlugin : PlugIn
{
	public static ExplodeBookPlugin Instance { get; private set; }

	internal static ExplodeSettings CurrentSettings { get; } = new ExplodeSettings();

	public ExplodeBookPlugin()
	{
		Instance = this;
	}

	protected override LoadReturnCode OnLoad(ref string errorMessage)
	{
		AutoUpdateController.Subscribe();
		return LoadReturnCode.Success;
	}

	protected override void OnShutdown()
	{
		AutoUpdateController.Unsubscribe();
		base.OnShutdown();
	}
}
