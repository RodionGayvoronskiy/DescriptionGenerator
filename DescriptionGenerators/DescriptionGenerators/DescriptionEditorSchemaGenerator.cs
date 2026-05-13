using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Aspid.Generators.Helper;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DescriptionGenerators;

/// <summary>
/// Generates a static <c>EditorFields</c> property on every class marked with
/// <c>[DescriptionType]</c>. The property describes how the description's fields
/// map to JSON keys and which editor control should be used to edit each field.
/// </summary>
[Generator(LanguageNames.CSharp)]
public class DescriptionEditorSchemaGenerator : IIncrementalGenerator
{
	private static readonly AttributeText EditorFieldAttribute = new("EditorFieldAttribute", "Framework.Core");
	private static readonly AttributeText KeyAttribute = new("KeyAttribute", "Modules.Framework.Core");
	private static readonly AttributeText IgnoreKeyAttribute = new("IgnoreKeyAttribute", "Modules.Framework.Core");

	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		var provider1 = context.SyntaxProvider
			.ForAttributeWithMetadataName(
				"Modules.Framework.Core.DescriptionTypeAttribute",
				SyntaxPredicate,
				FindData)
			.Where(x => x.HasValue)
			.Select((x, _) => x!.Value);

		var provider2 = context.SyntaxProvider
			.ForAttributeWithMetadataName(
				"Modules.Framework.Core.DescriptionEditorSchemaAttribute",
				SyntaxPredicate,
				FindData)
			.Where(x => x.HasValue)
			.Select((x, _) => x!.Value);

		context.RegisterSourceOutput(provider1, GenerateCode);
		context.RegisterSourceOutput(provider2, GenerateCode);
	}

	// ── Syntax filter ────────────────────────────────────────────────────────────

	private static bool SyntaxPredicate(SyntaxNode node, CancellationToken _)
		=> node is ClassDeclarationSyntax { AttributeLists.Count: > 0 } c
		   && c.Modifiers.Any(SyntaxKind.PartialKeyword)
		   && !c.Modifiers.Any(SyntaxKind.StaticKeyword);

	// ── Data extraction ──────────────────────────────────────────────────────────

	private static DescriptionData? FindData(GeneratorAttributeSyntaxContext context, CancellationToken _)
	{
		if (context.TargetSymbol is not INamedTypeSymbol symbol) return null;

		var declaration = Unsafe.As<ClassDeclarationSyntax>(context.TargetNode);

		// Walk from derived → base so that overrides in derived take priority.
		// Each level's fields are inserted at the front, giving final order: base fields first.
		var fields = new List<FieldData>();
		var seenKeys = new HashSet<string>();

		var current = symbol;
		while (current != null && current.SpecialType == SpecialType.None)
		{
			var levelFields = new List<FieldData>();

			foreach (var member in current.GetMembers())
			{
				if (member is not (IPropertySymbol or IFieldSymbol)) continue;
				if (member.IsStatic) continue;
				if (member.Name.StartsWith("<")) continue;

				if (member.DeclaredAccessibility is not (
				    Accessibility.Public or
				    Accessibility.Private or
				    Accessibility.Protected or
				    Accessibility.ProtectedOrInternal))
					continue;

				if (member.TryGetAnyAttributeInSelf(out AttributeData? _, IgnoreKeyAttribute)) continue;

				// ── JSON key ──
				string key;
				if (member.TryGetAnyAttributeInSelf(out AttributeData? keyAttr, KeyAttribute))
					key = keyAttr?.ConstructorArguments.FirstOrDefault().Value as string ?? ToSnakeCase(member.Name);
				else
					key = ToSnakeCase(member.Name);

				// Internal framework keys — never shown in the editor
				if (key is "id" or "type" or "m_id" or "m_type") continue;

				// Derived class already declared this key — skip base version
				if (!seenKeys.Add(key)) continue;

				// ── [EditorField] hint ──
				var editorHint = EditorFieldHint.Default;
				if (member.TryGetAnyAttributeInSelf(out AttributeData? editorAttr, EditorFieldAttribute))
				{
					int raw = editorAttr?.ConstructorArguments.FirstOrDefault().Value is int v ? v : 0;
					editorHint = (EditorFieldHint)raw;
				}

				levelFields.Add(new FieldData(key, member, editorHint));
			}

			// Insert this level's fields before any fields already collected from more-derived types
			fields.InsertRange(0, levelFields);
			current = current.BaseType;
		}

		return new DescriptionData(fields.ToImmutableArray(), declaration);
	}

	// ── Code generation ──────────────────────────────────────────────────────────

	private static void GenerateCode(SourceProductionContext context, DescriptionData data)
	{
		var @namespace = data.declaration.GetNamespaceName();
		var declarationText = new DeclarationText(data.declaration);

		var code = new CodeWriter();
		code.BeginClass(@namespace, declarationText);
		code.AppendLine(
			"public static readonly global::System.Collections.Generic.IReadOnlyList<global::Framework.Core.DescriptionEditorField> EditorFields");
		code.AppendLine("    = new global::Framework.Core.DescriptionEditorField[]");
		code.BeginBlock();

		foreach (var field in data.fields)
		{
			var memberType = field.member.GetSymbolType();
			var typeName = memberType?.ToDisplayString();

			string? entry = BuildEntry(field.key, typeName, memberType, field.editorHint);
			if (entry != null)
				code.AppendLine(entry);
		}

		code.EndBlock();
		code.AppendLine(";");
		code.EndClass(@namespace);

		context.AddSource(declarationText.GetFileName(@namespace, "EditorSchema"), code.GetSourceText());
	}

	// ── Entry builders ───────────────────────────────────────────────────────────

	private static string? BuildEntry(string key, string? typeName, ITypeSymbol? memberType, EditorFieldHint hint)
	{
		const string prefix = "new global::Framework.Core.DescriptionEditorField(";
		const string kinds = "global::Framework.Core.EditorFieldKind.";

		if (typeName == null) return null;

		// Hint overrides for string
		if (typeName == "string" && hint == EditorFieldHint.Sprite)
			return $"{prefix}\"{key}\", {kinds}Sprite),";
		if (typeName == "string" && hint == EditorFieldHint.Texture)
			return $"{prefix}\"{key}\", {kinds}Texture),";

		switch (typeName)
		{
			case "string":
			case "CodeStage.AntiCheat.ObscuredTypes.ObscuredString":
				return $"{prefix}\"{key}\", {kinds}String),";

			case "int":
			case "byte":
			case "ushort":
			case "uint":
			case "long":
			case "ulong":
			case "short":
				return $"{prefix}\"{key}\", {kinds}Int),";

			case "float":
			case "double":
				return $"{prefix}\"{key}\", {kinds}Float),";

			case "bool":
			case "CodeStage.AntiCheat.ObscuredTypes.ObscuredBool":
				return $"{prefix}\"{key}\", {kinds}Bool),";

			case "string[]":
				return $"{prefix}\"{key}\", {kinds}StringList),";

			case "Framework.Core.Data.IJsonDataReader":
				return $"{prefix}\"{key}\", {kinds}Node),";
		}

		if (memberType == null) return null;

		// Enum
		if (memberType is INamedTypeSymbol { TypeKind: TypeKind.Enum })
			return $"{prefix}\"{key}\", {kinds}Enum, typeof(global::{memberType.ToDisplayString()})),";

		// Generic collections: List<T>, IList<T>, IReadOnlyList<T>, ICollection<T>
		if (memberType is INamedTypeSymbol { IsGenericType: true } generic)
		{
			var origDef = generic.OriginalDefinition.ToDisplayString();
			if (origDef == "Framework.Core.Collections.IDescriptions<T>")
			{
				var elementType = generic.TypeArguments[0];
				if (elementType is INamedTypeSymbol { TypeKind: TypeKind.Interface or TypeKind.Class })
					return $"{prefix}\"{key}\", {kinds}NestedMap, typeof(global::{elementType.ToDisplayString()})),";
				return null;
			}

			bool isList = origDef is
				"System.Collections.Generic.List<T>" or
				"System.Collections.Generic.IList<T>" or
				"System.Collections.Generic.IReadOnlyList<T>" or
				"System.Collections.Generic.ICollection<T>";

			if (isList)
			{
				var elementType = generic.TypeArguments[0];
				if (elementType is INamedTypeSymbol { TypeKind: TypeKind.Interface or TypeKind.Class })
					return $"{prefix}\"{key}\", {kinds}NestedList, typeof(global::{elementType.ToDisplayString()})),";

				// Primitive list — not yet supported, skip
				return null;
			}
		}

		// Single nested Description (interface or class)
		if (memberType is INamedTypeSymbol { TypeKind: TypeKind.Interface or TypeKind.Class })
			return $"{prefix}\"{key}\", {kinds}Nested, typeof(global::{memberType.ToDisplayString()})),";

		return null;
	}

	// ── Helpers ──────────────────────────────────────────────────────────────────

	private static string ToSnakeCase(string name)
	{
		if (string.IsNullOrEmpty(name)) return name;
		if (name.StartsWith("m_"))
			name = name.Substring(2);
		var sb = new System.Text.StringBuilder();
		for (int i = 0; i < name.Length; i++)
		{
			char c = name[i];
			if (char.IsUpper(c))
			{
				if (i > 0) sb.Append('_');
				sb.Append(char.ToLowerInvariant(c));
			}
			else sb.Append(c);
		}

		return sb.ToString();
	}

	// ── Data types ───────────────────────────────────────────────────────────────

	private enum EditorFieldHint
	{
		Default = 0,
		Sprite = 1,
		Texture = 2
	}

	private readonly struct DescriptionData(ImmutableArray<FieldData> fields, ClassDeclarationSyntax declaration)
	{
		public readonly ImmutableArray<FieldData> fields = fields;
		public readonly ClassDeclarationSyntax declaration = declaration;
	}

	private readonly struct FieldData(string key, ISymbol member, EditorFieldHint editorHint)
	{
		public readonly string key = key;
		public readonly ISymbol member = member;
		public readonly EditorFieldHint editorHint = editorHint;
	}
}