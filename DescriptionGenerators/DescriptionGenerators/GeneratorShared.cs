using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DescriptionGenerators;

/// <summary>
/// Shared building blocks for the description generators: the common syntax filter,
/// the member-name → snake_case conversion, and the IgnoreKey target bits mirrored
/// from <c>Modules.Framework.Core.IgnoreKeyTarget</c> (the analyzer cannot reference
/// that enum directly, so the values are duplicated here in one place).
/// </summary>
internal static class GeneratorShared
{
	public const int IgnoreConstructorBit = 1;  // IgnoreKeyTarget.Constructor
	public const int IgnoreEditorSchemaBit = 2; // IgnoreKeyTarget.EditorSchema
	public const int IgnoreAllBits = 3;         // IgnoreKeyTarget.All (default when no arg)

	/// <summary>Partial, non-static class declaration carrying at least one attribute.</summary>
	public static bool IsPartialNonStaticClass(SyntaxNode node, CancellationToken _)
		=> node is ClassDeclarationSyntax { AttributeLists.Count: > 0 } candidate
		   && candidate.Modifiers.Any(SyntaxKind.PartialKeyword)
		   && !candidate.Modifiers.Any(SyntaxKind.StaticKeyword);

	/// <summary>
	/// Converts a member or type name to snake_case. A leading <c>m_</c> field prefix is
	/// stripped; type names never carry that prefix, so the strip is a no-op for them.
	/// </summary>
	public static string ToSnakeCase(string name)
	{
		if (string.IsNullOrEmpty(name))
			return name;

		if (name.StartsWith("m_"))
			name = name.Substring(2);

		var builder = new StringBuilder();
		for (int i = 0; i < name.Length; i++)
		{
			char c = name[i];
			if (char.IsUpper(c))
			{
				if (i > 0)
					builder.Append('_');
				builder.Append(char.ToLowerInvariant(c));
			}
			else
			{
				builder.Append(c);
			}
		}

		return builder.ToString();
	}
}
