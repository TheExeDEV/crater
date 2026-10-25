using System.Text;
using System.Text.RegularExpressions;

namespace Crater.Core;

public static partial class SafeSvg
{
	static readonly string[] ForbiddenParts = ["<script", "<foreignobject", "<!entity", "<!doctype", "javascript:", "vbscript:", "data:text", "<iframe", "<object", "<embed", "@import"];

	public static bool IsSafe(byte[] data)
	{
		string text = Encoding.UTF8.GetString(data).ToLowerInvariant();

		return !ForbiddenParts.Any(text.Contains)
			&& !EventAttribute().IsMatch(text)
			&& !ExternalReference().IsMatch(text);
	}

	[GeneratedRegex(@"[\s""'/]on[a-z]+\s*=")]
	private static partial Regex EventAttribute();

	[GeneratedRegex(@"(?:xlink:)?href\s*=(?![\s""']*(?:#|data:image/))|url\((?![\s""']*(?:#|data:image/))")]
	private static partial Regex ExternalReference();
}
