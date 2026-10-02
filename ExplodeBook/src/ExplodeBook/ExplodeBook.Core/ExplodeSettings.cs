using Rhino.Geometry;

namespace ExplodeBook.Core;

internal sealed class ExplodeSettings
{
	public ExplodeMode Mode { get; set; }

	public ManualPageKind PageKind { get; set; }

	public bool Landscape { get; set; } = true;

	public double CustomPageWidthMillimeters { get; set; } = 297.0;

	public double CustomPageHeightMillimeters { get; set; } = 210.0;

	public double ExplodeDistanceMillimeters { get; set; } = 25.0;

	public double ArrowHeadMillimeters { get; set; } = 4.0;

	public double PageGapMillimeters { get; set; } = 25.0;

	public int MaximumStepPages { get; set; } = 2000;
    public double MeshToleranceMillimeters { get; set; } = 0.02;
    public int AnalysisTimeoutSeconds { get; set; } = 120;


	public int PdfDpi { get; set; } = 300;

	public bool AutoUpdate { get; set; } = true;

	public bool CreateVectorLinework { get; set; } = false;

	public bool IncludeCover { get; set; } = true;

	public bool IncludePartsList { get; set; } = true;

	public bool IncludeCompletionPage { get; set; } = true;

	public bool IncludeDetailInset { get; set; } = true;

	public bool IncludeModuleOverview { get; set; } = true;

	public bool IncludeModuleAssemblyPages { get; set; } = true;

	public bool IncludeModulePartPages { get; set; } = true;

	public bool ParallelViewLocked { get; set; } = true;

	public Vector3d ViewDirection { get; set; } = new Vector3d(-0.458773, 0.766576, -0.449319);

	public Vector3d ViewUp { get; set; } = new Vector3d(-0.230739, 0.385548, 0.893371);

	public string ProductTitle { get; set; } = "木质拼装产品";

	public double ModelUnitsPerMillimeter { get; set; } = 1.0;

	public double ExplodeDistance => ExplodeDistanceMillimeters * ModelUnitsPerMillimeter;

	public double ArrowHead => ArrowHeadMillimeters * ModelUnitsPerMillimeter;

	public double PageGap => PageGapMillimeters * ModelUnitsPerMillimeter;

	public double PageWidthMillimeters
	{
		get
		{
			if (PageKind == ManualPageKind.Custom)
			{
				return CustomPageWidthMillimeters;
			}
			double result = ((PageKind == ManualPageKind.A3) ? 420.0 : 297.0);
			double result2 = ((PageKind == ManualPageKind.A3) ? 297.0 : 210.0);
			if (!Landscape)
			{
				return result2;
			}
			return result;
		}
	}

	public double PageHeightMillimeters
	{
		get
		{
			if (PageKind == ManualPageKind.Custom)
			{
				return CustomPageHeightMillimeters;
			}
			double result = ((PageKind == ManualPageKind.A3) ? 420.0 : 297.0);
			double result2 = ((PageKind == ManualPageKind.A3) ? 297.0 : 210.0);
			if (!Landscape)
			{
				return result;
			}
			return result2;
		}
	}

	public double PageWidth => PageWidthMillimeters * ModelUnitsPerMillimeter;

	public double PageHeight => PageHeightMillimeters * ModelUnitsPerMillimeter;
}
