using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace DescriptionGenerators.Tests;

public class EditorSchemaTooltipTests
{
	private const string Source = """
		namespace Modules.Framework.Core
		{
			public sealed class DescriptionTypeAttribute : System.Attribute
			{
				public DescriptionTypeAttribute(string key = null) { }
			}
		}

		namespace Game
		{
			public sealed class Track { }

			[Modules.Framework.Core.DescriptionType("race")]
			public partial class RaceDescription
			{
				/// <summary>
				/// Сколько кругов на <see cref="Track"/>,
				/// <c>0</c> — спринт.
				/// <para>Значение &lt; 100.</para>
				/// </summary>
				public int laps;

				/// <summary>Путь к "иконке" \ спрайту.</summary>
				public string Icon { get; }

				// обычный комментарий
				public float ratio = 1f;

				/// <remarks>Без summary.</remarks>
				public bool hidden;
			}
		}
		""";

	[Theory]
	[InlineData(DocumentationMode.None)]
	[InlineData(DocumentationMode.Parse)]
	public void SummaryBecomesTooltip(DocumentationMode mode)
	{
		string schema = RunSchemaGenerator(mode);

		Assert.Contains(
			"\"laps\", global::Framework.Core.EditorFieldKind.Int, tooltip: \"Сколько кругов на Track, 0 — спринт.\\nЗначение < 100.\")",
			schema);
		Assert.Contains("tooltip: \"Путь к \\\"иконке\\\" \\\\ спрайту.\")", schema);
		Assert.DoesNotContain("tooltip", Line(schema, "\"ratio\""));
		Assert.DoesNotContain("tooltip", Line(schema, "\"hidden\""));
	}

	private static string RunSchemaGenerator(DocumentationMode mode)
	{
		var parseOptions = CSharpParseOptions.Default.WithDocumentationMode(mode);
		var compilation = CSharpCompilation.Create(
			"Test",
			new[] { CSharpSyntaxTree.ParseText(Source, parseOptions) },
			new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

		var driver = CSharpGeneratorDriver
			.Create(new[] { new DescriptionEditorSchemaGenerator().AsSourceGenerator() }, parseOptions: parseOptions)
			.RunGenerators(compilation);

		return driver.GetRunResult().GeneratedTrees.Single().ToString();
	}

	private static string Line(string text, string marker)
	{
		return text.Split('\n').Single(line => line.Contains(marker));
	}
}
