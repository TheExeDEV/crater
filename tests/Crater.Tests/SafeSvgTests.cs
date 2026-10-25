using System.Text;
using Crater.Core;

namespace Crater.Tests;

public sealed class SafeSvgTests
{
	[Theory]
	[InlineData("""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><rect width="10" height="10" fill="#123"/></svg>""", true)]
	[InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><defs><linearGradient id="g"/></defs><rect fill="url(#g)"/><use href="#g"/></svg>""", true)]
	[InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>""", false)]
	[InlineData("""<svg xmlns="http://www.w3.org/2000/svg" onload="alert(1)"/>""", false)]
	[InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><a href="javascript:alert(1)"><rect/></a></svg>""", false)]
	[InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><foreignObject><div/></foreignObject></svg>""", false)]
	[InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><image href="https://tracker.test/x.png"/></svg>""", false)]
	[InlineData("""<!DOCTYPE svg [<!ENTITY x "y">]><svg xmlns="http://www.w3.org/2000/svg"/>""", false)]
	public void ChecksIcons(string svg, bool safe)
	{
		Assert.Equal(safe, SafeSvg.IsSafe(Encoding.UTF8.GetBytes(svg)));
	}
}
