using Crater.Core;
using Crater.Models;
using Crater.Registry;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Commands;

public static class MonorepositoryPicker
{
	public sealed record Element(string Name, DependencySpec Spec);

	public sealed record Choice(DependencySpec Spec, Realm Realm);

	const string Apply = "apply";
	const string AsPackageSays = "require as package says";

	public static async Task<List<Choice>?> ExpandAsync(Project project, DependencySpec spec, Realm? flagRealm, Realm defaultRealm, bool interactive)
	{
		if (spec.Source is not null)
		{
			return null;
		}

		IRegistry? registry = RegistryFor(project, spec);
		RegistryIndex? index = registry is null ? null : await TryIndexAsync(registry, spec.FullName);
		RegistryRelease? release = index?.Versions
			.Where(item => spec.Req.Matches(item.SemVer))
			.MaxBy(item => item.SemVer) ?? index?.Latest;

		if (release is null || !release.Manifest.IsMonorepository)
		{
			return null;
		}

		List<Element> elements = Elements(release.Manifest, spec.RegistryName);

		Out.Info($"{Fmt.Pkg($"{spec.FullName}@{release.Version}")} is a monorepository of {Fmt.Plural(elements.Count, "package", "packages")}.");

		List<Element> chosen = interactive ? PickElements(elements) : elements;
		bool asPackageSays = flagRealm is null && (!interactive || AskAsPackageSays(defaultRealm, out defaultRealm));
		List<Choice> choices = [];

		foreach (Element element in chosen)
		{
			Realm realm = flagRealm ?? defaultRealm;

			if (asPackageSays)
			{
				realm = await RealmFromPackageAsync(project, element.Spec) ?? defaultRealm;
			}

			choices.Add(new Choice(element.Spec, realm));
		}

		return choices;
	}

	public static List<Element> Elements(Manifest manifest, string? registryName)
	{
		List<Element> elements = [];

		foreach ((string name, string raw) in manifest.MonorepositoryElements ?? [])
		{
			DependencySpec parsed = DependencySpec.Parse(raw, $"monorepository-elements.{name}");
			string? alias = parsed.Alias ?? (name == parsed.Name || !IsAlias(name) ? null : name);
			string? registry = parsed.RegistryName ?? registryName;

			elements.Add(new Element(name, parsed.WithNames(alias, registry)));
		}

		return elements;
	}

	static bool IsAlias(string name)
	{
		return name.Length > 0 && char.IsAsciiLetter(name[0]) && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
	}

	static List<Element> PickElements(List<Element> elements)
	{
		HashSet<int> selected = [];

		while (true)
		{
			Out.Info($"  {Fmt.Val("0".PadLeft(2))}  all of them");

			for (int i = 0; i < elements.Count; i++)
			{
				string mark = selected.Contains(i) ? "[g]x[/]" : " ";

				Out.Info($"  {Fmt.Val((i + 1).ToString().PadLeft(2))}  [{mark}] {Fmt.Pkg(elements[i].Name)}  [c]{elements[i].Spec}[/]");
			}

			string? answer = Select.Ask($"Type numbers to pick packages, then {Fmt.Cmd(Apply)}:");

			if (answer is null || answer.Equals("q", StringComparison.OrdinalIgnoreCase))
			{
				throw new CraterException("Cancelled.");
			}

			if (answer.Equals(Apply, StringComparison.OrdinalIgnoreCase))
			{
				if (selected.Count > 0)
				{
					return selected.Order().Select(i => elements[i]).ToList();
				}

				Out.Warn("Pick at least one package first.");
				continue;
			}

			foreach (string part in answer.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries))
			{
				if (!int.TryParse(part, out int number) || number < 0 || number > elements.Count)
				{
					Out.Warn($"{Fmt.Val(part)} is not one of the numbers above.");
					continue;
				}

				if (number == 0)
				{
					selected = Enumerable.Range(0, elements.Count).ToHashSet();
				}
				else if (!selected.Remove(number - 1))
				{
					selected.Add(number - 1);
				}
			}
		}
	}

	static bool AskAsPackageSays(Realm fallback, out Realm realm)
	{
		Realm[] realms = [Realm.Client, Realm.Server, Realm.Develop];
		List<Select.Item> items = realms
			.Select(item => new Select.Item(Fmt.Val(item.ManifestField()), false, null))
			.Append(new Select.Item(Fmt.Val(AsPackageSays), false, "import_to of each package"))
			.ToList();

		int? picked = Select.One("Where should they go?", items, Array.IndexOf(realms, fallback));

		if (picked is null)
		{
			throw new CraterException("Cancelled.");
		}

		realm = picked.Value < realms.Length ? realms[picked.Value] : fallback;

		return picked.Value == realms.Length;
	}

	static async Task<Realm?> RealmFromPackageAsync(Project project, DependencySpec spec)
	{
		IRegistry? registry = RegistryFor(project, spec);
		RegistryRelease? release = registry is null ? null : (await TryIndexAsync(registry, spec.FullName))?.Latest;

		return release?.Manifest.ImportTo switch
		{
			"server" => Realm.Server,
			null => null,
			_ => Realm.Client,
		};
	}

	static IRegistry? RegistryFor(Project project, DependencySpec spec)
	{
		if (spec.RegistryName is string named)
		{
			return Registries.Open(NamedRegistries.Location(named, project.Manifest), project.Directory);
		}

		return project.OpenRegistry();
	}

	static async Task<RegistryIndex?> TryIndexAsync(IRegistry registry, string name)
	{
		try
		{
			return await registry.GetIndexAsync(name);
		}
		catch (CraterException)
		{
			return null;
		}
	}
}
