using Crater.Commands;
using Crater.Models;

namespace Crater.Tests;

public sealed class MonorepositoryTests
{
	static List<MonorepositoryPicker.Element> Elements()
	{
		Manifest kit = new()
		{
			MonorepositoryElements = new() { ["Signal"] = "studio/signal^1", ["promise"] = "studio/promise^2", ["Net"] = "wally#studio/net^1" },
		};

		return MonorepositoryPicker.Elements(kit, "team");
	}

	static List<string> Pick(params string?[] answers)
	{
		Queue<string?> queue = new(answers);

		return MonorepositoryPicker.PickElements(Elements(), _ => queue.Dequeue()).Select(element => element.Spec.ToString()).ToList();
	}

	[Fact]
	public void ElementsKeepTheirAliasAndRegistry()
	{
		Assert.Equal(["Signal:team#studio/signal^1", "team#studio/promise^2", "Net:wally#studio/net^1"], Elements().Select(element => element.Spec.ToString()));
	}

	[Fact]
	public void NumbersPickAndApplyConfirms()
	{
		Assert.Equal(["team#studio/promise^2"], Pick("2", "apply"));
		Assert.Equal(["Signal:team#studio/signal^1", "Net:wally#studio/net^1"], Pick("1, 3", "APPLY"));
		Assert.Equal(3, Pick("0", "apply").Count);
		Assert.Equal(["Net:wally#studio/net^1"], Pick("0", "1 2", "apply"));
	}

	[Fact]
	public void ApplyNeedsAPickAndBadInputIsIgnored()
	{
		Assert.Equal(["Signal:team#studio/signal^1"], Pick("apply", "9", "abc", "1", "apply"));
		Assert.Throws<CraterException>(() => Pick("1", null));
		Assert.Throws<CraterException>(() => Pick("q"));
	}
}
