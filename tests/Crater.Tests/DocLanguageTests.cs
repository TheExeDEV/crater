using Crater.Core;

namespace Crater.Tests;

public sealed class DocLanguageTests
{
	const string English = """
		# Signal

		A fast signal library for Roblox. Connect a function, fire the signal and every listener runs in order.
		Made by José and released under the MIT license, see https://github.com/someone/signal for the source.

		```lua
		local signal = Signal.new() -- 中文注释 está aquí und für
		```
		""";

	const string Russian = """
		# Сигнал

		Быстрая библиотека сигналов для Roblox. Подключите функцию, вызовите сигнал, и все слушатели сработают по порядку.
		Пакет распространяется под лицензией MIT, исходники лежат на GitHub.
		""";

	const string Spanish = """
		# Señal

		Una biblioteca de señales para Roblox. Conecta una función, dispara la señal y todos los oyentes se ejecutan en orden.
		El paquete es rápido y también puede usar tipos. Para instalar el paquete usa el comando de crater, pero lee la guía del proyecto.
		""";

	const string SpanishWithoutAccents = """
		Una biblioteca para Roblox que puede usar los tipos del paquete. Para instalar el paquete usa el comando, pero lee la guia
		del proyecto. Los ejemplos de uso estan en la carpeta, como el resto de las paginas por ahora. Esta guia es para los usuarios del paquete.
		""";

	const string Chinese = """
		# 信号

		这是一个用于 Roblox 的快速信号库。连接一个函数，触发信号，所有监听器都会按顺序运行。
		""";

	[Fact]
	public void EnglishAndRussianPass()
	{
		Assert.Null(DocLanguage.Problem(English));
		Assert.Null(DocLanguage.Problem(Russian));
		Assert.Null(DocLanguage.Problem(English + "\n" + Russian));
	}

	[Fact]
	public void OtherLanguagesAreFound()
	{
		Assert.NotNull(DocLanguage.Problem(Spanish));
		Assert.NotNull(DocLanguage.Problem(SpanishWithoutAccents));
		Assert.NotNull(DocLanguage.Problem(Chinese));
	}

	[Theory]
	[InlineData("README.md", true)]
	[InlineData("docs/guide.markdown", true)]
	[InlineData("CHANGELOG", true)]
	[InlineData("changelog.txt", true)]
	[InlineData("LICENSE.md", false)]
	[InlineData("src/init.luau", false)]
	[InlineData("notes.txt", false)]
	public void PicksTheDocuments(string path, bool expected)
	{
		Assert.Equal(expected, DocLanguage.IsDocument(path));
	}

	[Fact]
	public void PackRejectsDocsInOtherLanguages()
	{
		string directory = Path.Combine(Path.GetTempPath(), $"crater-language-{Guid.NewGuid():N}");

		Directory.CreateDirectory(directory);

		try
		{
			File.WriteAllText(Path.Combine(directory, "README.md"), English);
			File.WriteAllText(Path.Combine(directory, "CHANGELOG.md"), Chinese);

			CraterException exception = Assert.Throws<CraterException>(() => DocLanguage.Require(directory, ["README.md", "CHANGELOG.md", "src/init.luau"]));

			Assert.Contains("CHANGELOG.md", exception.Message);
			Assert.DoesNotContain("README.md", exception.Message);
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}
}
