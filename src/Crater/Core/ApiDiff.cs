namespace Crater.Core;

public sealed record ApiChange(string Text, bool Breaking);

public static class ApiDiff
{
	public static List<ApiChange> Compare(PublicApi before, PublicApi after)
	{
		List<ApiChange> changes = [];

		foreach ((string key, ApiMember old) in before.Members.OrderBy(pair => pair.Key, StringComparer.Ordinal))
		{
			if (!after.Members.TryGetValue(key, out ApiMember? current))
			{
				changes.Add(new ApiChange($"removed {Describe(old)}", true));
				continue;
			}

			if (old.Kind != current.Kind)
			{
				changes.Add(new ApiChange($"{Describe(old)} is now {Kind(current.Kind)}", true));
				continue;
			}

			if (old.Kind == ApiKind.Type && old.RequiredTypeParameters != current.RequiredTypeParameters)
			{
				changes.Add(new ApiChange($"{Describe(old)} now needs {current.RequiredTypeParameters} type parameters instead of {old.RequiredTypeParameters}", true));
				continue;
			}

			if (old.Kind == ApiKind.Function)
			{
				changes.AddRange(CompareParameters(old, current));
			}
		}

		foreach ((string key, ApiMember added) in after.Members.OrderBy(pair => pair.Key, StringComparer.Ordinal))
		{
			if (!before.Members.ContainsKey(key))
			{
				changes.Add(new ApiChange($"added {Describe(added)}", false));
			}
		}

		return changes;
	}

	static IEnumerable<ApiChange> CompareParameters(ApiMember old, ApiMember current)
	{
		bool oldVariadic = old.Parameters.Any(parameter => parameter.Name == "...");

		for (int i = 0; i < current.Parameters.Count; i++)
		{
			ApiParameter parameter = current.Parameters[i];

			if (!parameter.Required)
			{
				continue;
			}

			if (i >= old.Parameters.Count)
			{
				if (!oldVariadic)
				{
					yield return new ApiChange($"{Describe(old)} has a new required parameter {parameter.Name}", true);
				}

				continue;
			}

			ApiParameter before = old.Parameters[i];

			if (!before.Required && before.Name != "...")
			{
				yield return new ApiChange($"parameter {parameter.Name} of {Describe(old)} is now required", true);
			}
		}
	}

	static string Describe(ApiMember member) => member.Kind switch
	{
		ApiKind.Function => member.Key.EndsWith("()", StringComparison.Ordinal) ? member.Key : member.Key + "()",
		ApiKind.Module => $"module {member.Key}",
		_ => member.Key,
	};

	static string Kind(ApiKind kind) => kind switch
	{
		ApiKind.Function => "a function",
		ApiKind.Module => "a module",
		ApiKind.Type => "a type",
		_ => "a value, not a function",
	};
}
