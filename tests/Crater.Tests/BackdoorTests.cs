using System.IO.Compression;
using System.Text;
using Crater.Registry;

namespace Crater.Tests;

public sealed class BackdoorTests
{
	static List<Finding> Scan(string source)
	{
		using MemoryStream stream = new();

		using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
		{
			foreach ((string path, string text) in new[] { ("crater.json", "{\"name\":\"t/evil\",\"version\":\"1.0.0\"}"), ("init.luau", source) })
			{
				using Stream entry = zip.CreateEntry(path).Open();

				entry.Write(Encoding.UTF8.GetBytes(text));
			}
		}

		return PackageScanner.ScanArchive(stream.ToArray(), "t/evil@1.0.0");
	}

	[Theory]
	[InlineData("require(4566874012)")]
	[InlineData("require ( 4566874012 )")]
	[InlineData("require(0x1104E9D3C)")]
	[InlineData("require(tonumber(\"4566874012\"))")]
	[InlineData("local id = 4566874012\nrequire(id)")]
	[InlineData("local id = 0x1104E9D3C;\nlocal m = require(id)")]
	[InlineData("loadstring(source)()")]
	[InlineData("local run = _G[\"load\" .. \"string\"]")]
	[InlineData("local run = getfenv()['load'..'string']")]
	[InlineData("local r = rawget(getfenv(), \"require\")")]
	[InlineData("game:GetService(\"InsertService\"):LoadAsset(123)")]
	public void FindsBackdoors(string source)
	{
		Assert.Contains(Scan(source), finding => finding.Severity == Severity.Danger);
	}

	[Theory]
	[InlineData("local s = string.char(108, 111, 97, 100, 115, 116, 114, 105, 110, 103)")]
	[InlineData("local s = \"\\108\\111\\97\\100\\115\\116\\114\\105\\110\\103\\40\\41\"")]
	[InlineData("local env = getfenv(2)")]
	public void FindsObfuscation(string source)
	{
		Assert.Contains(Scan(source), finding => finding.Severity is Severity.Warning or Severity.Danger);
	}

	[Theory]
	[InlineData("local Module = require(script.Parent.Module)")]
	[InlineData("local Packages = game:GetService(\"ReplicatedStorage\").Packages\nlocal Promise = require(Packages.Promise)")]
	[InlineData("-- require(4566874012) was the old way\nreturn {}")]
	[InlineData("local maxPlayers = 100000\nprint(maxPlayers)")]
	[InlineData("local text = \"require is a function\"")]
	[InlineData("local id = 4566874012\nlocal Module = require(script.Module)")]
	public void LeavesNormalCodeAlone(string source)
	{
		Assert.DoesNotContain(Scan(source), finding => finding.Severity is Severity.Danger or Severity.Blocked);
	}
}
