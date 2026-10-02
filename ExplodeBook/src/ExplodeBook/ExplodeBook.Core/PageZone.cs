using Rhino.Geometry;

namespace ExplodeBook.Core;

internal sealed class PageZone
{
	public int PageIndex { get; set; }

	public string LayoutName { get; set; }

	public string Title { get; set; }

	public BoundingBox Bounds { get; set; }
}
