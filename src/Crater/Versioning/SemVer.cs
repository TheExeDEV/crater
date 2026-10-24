using System.Globalization;
using Crater.Util;

namespace Crater.Versioning;

public sealed class SemVer : IComparable<SemVer>, IEquatable<SemVer>
{
	public int Major { get; }
	public int Minor { get; }
	public int Patch { get; }
	public string Prerelease { get; }

	public SemVer(int major, int minor, int patch, string prerelease = "")
	{
		Major = major;
		Minor = minor;
		Patch = patch;
		Prerelease = prerelease;
	}

	public bool IsPrerelease => Prerelease.Length > 0;

	public static SemVer Parse(string text)
	{
		if (TryParse(text, out SemVer version, out _))
		{
			return version;
		}

		throw new FormatException(Explain(text));
	}

	public static string Explain(string? text)
	{
		string shown = (text ?? "").Trim();

		if (TryParse(shown, out SemVer version, out int parts) && parts < 3)
		{
			return $"version '{shown}' needs three numbers, write '{version}'";
		}

		return $"'{shown}' is not a version, write three numbers like '1.0.0'";
	}

	public static bool TryParse(string? text, out SemVer version)
	{
		return TryParse(text, out version, out int parts) && parts == 3;
	}

	public static bool TryParse(string? text, out SemVer version, out int parts)
	{
		version = null!;
		parts = 0;

		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		string body = text.Trim();

		if (body[0] is 'v' or 'V')
		{
			body = body[1..];
		}

		int buildStart = body.IndexOf('+');

		if (buildStart >= 0)
		{
			body = body[..buildStart];
		}

		string prerelease = "";
		int prereleaseStart = body.IndexOf('-');

		if (prereleaseStart >= 0)
		{
			prerelease = body[(prereleaseStart + 1)..];
			body = body[..prereleaseStart];

			if (prerelease.Length == 0 || !prerelease.Split('.').All(IsValidIdentifier))
			{
				return false;
			}
		}

		string[] items = body.Split('.');

		if (items.Length is < 1 or > 3)
		{
			return false;
		}

		if (prerelease.Length > 0 && items.Length != 3)
		{
			return false;
		}

		int[] numbers = new int[3];

		for (int i = 0; i < items.Length; i++)
		{
			if (!int.TryParse(items[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
			{
				return false;
			}
		}

		parts = items.Length;
		version = new SemVer(numbers[0], numbers[1], numbers[2], prerelease);

		return true;
	}

	public bool SameTriple(SemVer other)
	{
		return Major == other.Major && Minor == other.Minor && Patch == other.Patch;
	}

	public int CompareTo(SemVer? other)
	{
		if (other is null)
		{
			return 1;
		}

		int result = Major.CompareTo(other.Major);

		if (result == 0)
		{
			result = Minor.CompareTo(other.Minor);
		}

		if (result == 0)
		{
			result = Patch.CompareTo(other.Patch);
		}

		if (result == 0)
		{
			result = ComparePrerelease(Prerelease, other.Prerelease);
		}

		return result;
	}

	static int ComparePrerelease(string left, string right)
	{
		if (left == right)
		{
			return 0;
		}

		if (left.Length == 0)
		{
			return 1;
		}

		if (right.Length == 0)
		{
			return -1;
		}

		string[] leftParts = left.Split('.');
		string[] rightParts = right.Split('.');

		for (int i = 0; i < Math.Min(leftParts.Length, rightParts.Length); i++)
		{
			bool leftIsNumber = int.TryParse(leftParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out int leftNumber);
			bool rightIsNumber = int.TryParse(rightParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out int rightNumber);

			int result;

			if (leftIsNumber && rightIsNumber)
			{
				result = leftNumber.CompareTo(rightNumber);
			}
			else if (leftIsNumber)
			{
				result = -1;
			}
			else if (rightIsNumber)
			{
				result = 1;
			}
			else
			{
				result = string.CompareOrdinal(leftParts[i], rightParts[i]);
			}

			if (result != 0)
			{
				return result;
			}
		}

		return leftParts.Length.CompareTo(rightParts.Length);
	}

	static bool IsValidIdentifier(string identifier)
	{
		return identifier.Length > 0 && identifier.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
	}

	public bool Equals(SemVer? other) => other is not null && CompareTo(other) == 0;

	public override bool Equals(object? other) => other is SemVer version && Equals(version);

	public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);

	public static bool operator <(SemVer left, SemVer right) => left.CompareTo(right) < 0;

	public static bool operator >(SemVer left, SemVer right) => left.CompareTo(right) > 0;

	public static bool operator <=(SemVer left, SemVer right) => left.CompareTo(right) <= 0;

	public static bool operator >=(SemVer left, SemVer right) => left.CompareTo(right) >= 0;

	public SemVer Next(string part)
	{
		return part switch
		{
			"major" => new SemVer(Major + 1, 0, 0),
			"minor" => Minor < 9 ? new SemVer(Major, Minor + 1, 0) : new SemVer(Major + 1, 0, 0),
			"patch" when IsPrerelease => new SemVer(Major, Minor, Patch),
			"patch" => Patch < 9 ? new SemVer(Major, Minor, Patch + 1) : new SemVer(Major, Minor, 0).Next("minor"),
			_ => throw new CraterException($"Unknown part {Fmt.Val(part)}. Use patch, minor or major."),
		};
	}

	public override string ToString()
	{
		string core = $"{Major}.{Minor}.{Patch}";

		return IsPrerelease ? $"{core}-{Prerelease}" : core;
	}
}
