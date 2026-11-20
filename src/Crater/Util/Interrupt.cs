namespace Crater.Util;

public static class Interrupt
{
	static readonly List<Action> actions = [];
	static readonly object gate = new();

	public static IDisposable OnCancel(Action action)
	{
		lock (gate)
		{
			actions.Add(action);
		}

		return new Registration(action);
	}

	public static void RunAll()
	{
		List<Action> pending;

		lock (gate)
		{
			pending = [.. actions];
			actions.Clear();
		}

		pending.Reverse();

		foreach (Action action in pending)
		{
			try
			{
				action();
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
			}
		}
	}

	public static IDisposable DeleteOnCancel(string directory)
	{
		return OnCancel(() =>
		{
			if (Directory.Exists(directory))
			{
				Directory.Delete(directory, recursive: true);
			}
		});
	}

	sealed class Registration(Action action) : IDisposable
	{
		public void Dispose()
		{
			lock (gate)
			{
				actions.Remove(action);
			}
		}
	}
}
