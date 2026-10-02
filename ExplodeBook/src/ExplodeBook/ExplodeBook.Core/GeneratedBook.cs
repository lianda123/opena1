using System;
using System.Collections.Generic;

namespace ExplodeBook.Core;

internal sealed class GeneratedBook
{
	public List<Guid> GeneratedObjectIds { get; } = new List<Guid>();

	public List<string> LayoutNames { get; } = new List<string>();

	public int PartCount { get; set; }

	public int StepCount { get; set; }

	public bool IsLinked { get; set; }
}
