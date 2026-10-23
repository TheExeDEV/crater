namespace Crater.Versioning;

public enum ReqKind
{
	Latest,
	Exact,
	AtLeast,
	Caret,
	Tilde,
}

public sealed class VersionReq
{
	public static readonly VersionReq Latest = new(ReqKind.Latest, null, "", null, null);

	public ReqKind Kind { get; }
	public SemVer? Base { get; }

	readonly string baseText;
	readonly SemVer? lower;
	readonly SemVer? upper;

	VersionReq(ReqKind kind, SemVer? baseVersion, string baseText, SemVer? lower, SemVer? upper)
	{
		Kind = kind;
		Base = baseVersion;

		this.baseText = baseText;
		this.lower = lower;
		this.upper = upper;
	}

	public static VersionReq Parse(char? operatorSymbol, string versionText)
	{
		string version = versionText.Trim();

		if (operatorSymbol is null or '^' && version.Length == 0)
		{
			return Latest;
		}

		if (operatorSymbol is null)
		{
			throw new FormatException($"put a sign before the version: '^{version}' for compatible updates or '={version}' for exactly this one");
		}

		if (version.Length == 0)
		{
			throw new FormatException($"a version is required after '{operatorSymbol}'");
		}

		if (!SemVer.TryParse(version, out SemVer parsed, out int parts))
		{
			throw new FormatException(SemVer.Explain(version));
		}

		switch (operatorSymbol)
		{
			case '=':
				if (parts != 3)
				{
					throw new FormatException($"'=' needs three numbers, write '={parsed}', or '^{version}' for any {version}.x");
				}

				return new VersionReq(ReqKind.Exact, parsed, version, parsed, null);

			case '+':
				return new VersionReq(ReqKind.AtLeast, parsed, version, parsed, null);

			case '^':
				return new VersionReq(ReqKind.Caret, parsed, version, parsed, CaretUpperBound(parsed, parts));

			case '~':
				return new VersionReq(ReqKind.Tilde, parsed, version, parsed, TildeUpperBound(parsed, parts));

			default:
				throw new FormatException($"unknown operator '{operatorSymbol}'");
		}
	}

	public static VersionReq SameKind(VersionReq template, SemVer version)
	{
		char symbol = template.Kind switch
		{
			ReqKind.Exact => '=',
			ReqKind.AtLeast => '+',
			ReqKind.Tilde => '~',
			_ => '^',
		};

		return Parse(symbol, version.ToString());
	}

	static SemVer CaretUpperBound(SemVer version, int parts)
	{
		if (parts == 1 || version.Major > 0)
		{
			return new SemVer(version.Major + 1, 0, 0);
		}

		if (parts == 2 || version.Minor > 0)
		{
			return new SemVer(0, version.Minor + 1, 0);
		}

		return new SemVer(0, 0, version.Patch + 1);
	}

	static SemVer TildeUpperBound(SemVer version, int parts)
	{
		if (parts == 1)
		{
			return new SemVer(version.Major + 1, 0, 0);
		}

		return new SemVer(version.Major, version.Minor + 1, 0);
	}

	public bool Matches(SemVer version)
	{
		if (Kind == ReqKind.Exact)
		{
			return version.Equals(Base);
		}

		bool allowsPrerelease = Base is { IsPrerelease: true } && Base.SameTriple(version);

		if (version.IsPrerelease && !allowsPrerelease)
		{
			return false;
		}

		if (lower is not null && version < lower)
		{
			return false;
		}

		if (upper is not null && version >= upper)
		{
			return false;
		}

		return true;
	}

	public override string ToString() => Kind switch
	{
		ReqKind.Latest => "^",
		ReqKind.Exact => "=" + baseText,
		ReqKind.AtLeast => "+" + baseText,
		ReqKind.Caret => "^" + baseText,
		ReqKind.Tilde => "~" + baseText,
		_ => "?",
	};

	public string Describe() => Kind switch
	{
		ReqKind.Latest => "latest",
		ReqKind.Exact => $"={Base}",
		ReqKind.AtLeast => $">={Base}",
		_ => $">={lower} <{upper}",
	};
}
