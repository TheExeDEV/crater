namespace Crater.Cli;

public sealed class Args
{
	static HashSet<string> OptionsWithValue => CommandList.Current.OptionsWithValue;

	readonly Dictionary<string, string?> options = [];

	public string? Command { get; private set; }
	public List<string> Positional { get; } = [];
	public List<string> Passthrough { get; } = [];

	public static Args Parse(string[] argv)
	{
		Args result = new();

		for (int i = 0; i < argv.Length; i++)
		{
			string current = argv[i];

			if (current == "--")
			{
				result.Passthrough.AddRange(argv[(i + 1)..]);
				break;
			}

			if (result.Command is null && current is "-v" or "--version")
			{
				result.Command = "version";
				continue;
			}

			if (current.StartsWith('-') && current.Length > 1)
			{
				string key = current.TrimStart('-');
				string? value = null;
				int equals = key.IndexOf('=');

				if (equals >= 0)
				{
					value = key[(equals + 1)..];
					key = key[..equals];
				}
				else if (OptionsWithValue.Contains(key) && i + 1 < argv.Length)
				{
					i++;
					value = argv[i];
				}

				result.options[key] = value;
				continue;
			}

			if (result.Command is null)
			{
				result.Command = current;
			}
			else
			{
				result.Positional.Add(current);
			}
		}

		return result;
	}

	public bool Has(params string[] keys) => keys.Any(options.ContainsKey);

	public string? Value(params string[] keys)
	{
		foreach (string key in keys)
		{
			if (options.TryGetValue(key, out string? value) && value is not null)
			{
				return value;
			}
		}

		return null;
	}

	public string? First => Positional.Count > 0 ? Positional[0] : null;

	public Args Shift()
	{
		Args shifted = new() { Command = First };

		shifted.Positional.AddRange(Positional.Skip(1));
		shifted.Passthrough.AddRange(Passthrough);

		foreach ((string key, string? value) in options)
		{
			shifted.options[key] = value;
		}

		return shifted;
	}
}
