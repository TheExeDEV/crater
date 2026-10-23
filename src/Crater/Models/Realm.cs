using Crater.Util;

namespace Crater.Models;

public enum Realm
{
	Client,
	Server,
	Develop,
}

public static class Realms
{
	public static readonly Realm[] All = [Realm.Client, Realm.Server, Realm.Develop];

	public static string Key(this Realm realm) => realm switch
	{
		Realm.Client => "client",
		Realm.Server => "server",
		_ => "develop",
	};

	public static string ManifestField(this Realm realm) => "import_" + realm.Key();

	public static Realm ParseKey(string key) => key switch
	{
		"client" => Realm.Client,
		"server" => Realm.Server,
		"develop" => Realm.Develop,
		_ => throw new CraterException($"Unknown realm {Fmt.Val(key)}"),
	};
}
