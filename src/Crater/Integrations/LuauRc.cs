using System.Text.Json;
using System.Text.Json.Nodes;
using Crater.Core;
using Crater.Models;
using Crater.Util;

namespace Crater.Integrations;

public static class LuauRc
{
	public const string FileName = ".luaurc";

	public static void Update(Project project, IReadOnlyDictionary<Realm, ResolvedGraph> graphs, Prompter prompt)
	{
		string path = Path.Combine(project.Directory, FileName);
		JsonObject root = Json.ReadObject(path);
		string before = Json.Serialize(root);

		if (root["aliases"] is not JsonObject aliases)
		{
			aliases = new JsonObject();
			root["aliases"] = aliases;
		}

		foreach ((Realm realm, string folder) in project.Config.ConfiguredFolders())
		{
			string alias = AliasFor(folder);
			string value = "./" + folder;
			bool installed = graphs.TryGetValue(realm, out ResolvedGraph? graph) && graph.All.Count > 0;

			if (installed)
			{
				aliases[alias] = value;
				continue;
			}

			bool pointsHere = aliases[alias]?.GetValueKind() == JsonValueKind.String && (string?)aliases[alias] == value;

			if (pointsHere)
			{
				aliases.Remove(alias);
			}
		}

		if (aliases.Count == 0)
		{
			root.Remove("aliases");
		}

		string after = Json.Serialize(root);
		bool nothingToWrite = after == before || (!File.Exists(path) && root.Count == 0);

		if (nothingToWrite)
		{
			return;
		}

		string action = File.Exists(path)
			? $"Update aliases in {Fmt.File(FileName)}{CommentsNote(path)}"
			: $"Create {Fmt.File(FileName)} with package folder aliases";

		if (prompt.Confirm(action))
		{
			Json.WriteText(path, after);
		}
	}

	public static string AliasFor(string folder)
	{
		return folder[(folder.LastIndexOf('/') + 1)..];
	}

	internal static string CommentsNote(string path)
	{
		if (!File.Exists(path))
		{
			return "";
		}

		string text = File.ReadAllText(path);
		bool hasComments = text.Contains("//") || text.Contains("/*");

		return hasComments ? " (comments in the file will be lost)" : "";
	}
}
