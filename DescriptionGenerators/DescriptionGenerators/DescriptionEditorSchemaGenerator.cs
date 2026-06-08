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

	// IgnoreKeyTarget enum bits (mirrors Modules.Framework.Core.IgnoreKeyTarget)
	private const int IgnoreEditorSchemaBit = 2; // IgnoreKeyTarget.EditorSchema
	private const int IgnoreAllBits = 3;         // IgnoreKeyTarget.All (default when no arg)

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

				// Пропускаем члены с атрибутом IgnoreKey, если установлен бит EditorSchema
				if (member.TryGetAnyAttributeInSelf(out AttributeData? ignoreAttr, IgnoreKeyAttribute))
				{
					int targetBits = ignoreAttr?.ConstructorArguments.FirstOrDefault().Value is int v ? v : IgnoreAllBits;
					if ((targetBits & IgnoreEditorSchemaBit) != 0)
						continue;
				}

				// [Key(flat: true)] fields use a packed [x,y,(z),...] layout the editor cannot render;
				// a Float2/3List entry would corrupt them on re-save, so hide them.
				if (member.TryGetAnyAttributeInSelf(out AttributeData? flatKey, KeyAttribute)
				    && flatKey is { ConstructorArguments.Length: >= 3 } && flatKey.ConstructorArguments[2].Value is true)
					continue;

				// ── JSON key ──
				// An explicit [Key("...")] is an intentional opt-in: External* descriptions
				// (ExternalReward/Price/Amount/Requirement) reuse the reserved "id" key to
				// reference another entity, and those fields must stay editable. Keys derived
				// implicitly from the member name are the base Description identity/discriminator
				// fields and are filtered out below.
				bool hasExplicitKey = false;
				string key;
				if (member.TryGetAnyAttributeInSelf(out AttributeData? keyAttr, KeyAttribute)
				    && keyAttr?.ConstructorArguments.FirstOrDefault().Value is string explicitKey
				    && !string.IsNullOrEmpty(explicitKey))
				{
					key = explicitKey;
					hasExplicitKey = true;
				}
				else
				{
					key = ToSnakeCase(member.Name);
				}

				// Internal framework identity/discriminator keys — hidden unless a field
				// explicitly opts into the key via [Key(...)].
				if (!hasExplicitKey && key is "id" or "type" or "m_id" or "m_type") continue;

				// Derived class already declared this key — skip base version
				if (!seenKeys.Add(key)) continue;

				// ── [EditorField] hint ──
				var editorHint = EditorFieldHint.Default;
				string editorPath = "";
				if (member.TryGetAnyAttributeInSelf(out AttributeData? editorAttr, EditorFieldAttribute))
				{
					int raw = editorAttr?.ConstructorArguments.FirstOrDefault().Value is int v ? v : 0;
					editorHint = (EditorFieldHint)raw;
					editorPath = editorAttr?.ConstructorArguments.ElementAtOrDefault(1).Value as string ?? "";
				}

				levelFields.Add(new FieldData(key, member, editorHint, editorPath));
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

			string? entry = BuildEntry(field.key, typeName, memberType, field.editorHint, field.editorPath);
			if (entry != null)
				code.AppendLine(entry);
		}

		code.EndBlock();
		code.AppendLine(";");
		code.EndClass(@namespace);

		context.AddSource(declarationText.GetFileName(@namespace, "EditorSchema"), code.GetSourceText());
	}

	// ── Entry builders ───────────────────────────────────────────────────────────

	private static string? BuildEntry(string key, string? typeName, ITypeSymbol? memberType, EditorFieldHint hint, string path = "")
	{
		const string prefix = "new global::Framework.Core.DescriptionEditorField(";
		const string kinds = "global::Framework.Core.EditorFieldKind.";

		if (typeName == null) return null;

		// Hint overrides for string
		if (typeName == "string" && hint == EditorFieldHint.Sprite)
		{
			return !string.IsNullOrEmpty(path)
				? $"{prefix}\"{key}\", {kinds}Sprite, \"{path}\"),"
				: $"{prefix}\"{key}\", {kinds}Sprite),";
		}
		if (typeName == "string" && hint == EditorFieldHint.Texture)
		{
			return !string.IsNullOrEmpty(path)
				? $"{prefix}\"{key}\", {kinds}Texture, \"{path}\"),"
				: $"{prefix}\"{key}\", {kinds}Texture),";
		}

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
			case "CodeStage.AntiCheat.ObscuredTypes.ObscuredInt":
			case "CodeStage.AntiCheat.ObscuredTypes.ObscuredLong":
				return $"{prefix}\"{key}\", {kinds}Int),";

			case "float":
			case "double":
			case "CodeStage.AntiCheat.ObscuredTypes.ObscuredFloat":
			case "CodeStage.AntiCheat.ObscuredTypes.ObscuredDouble":
				return $"{prefix}\"{key}\", {kinds}Float),";

			case "bool":
			case "CodeStage.AntiCheat.ObscuredTypes.ObscuredBool":
				return $"{prefix}\"{key}\", {kinds}Bool),";

			case "string[]":
				return $"{prefix}\"{key}\", {kinds}StringList),";

			case "int[]":
				return $"{prefix}\"{key}\", {kinds}IntList),";

			case "float[]":
				return $"{prefix}\"{key}\", {kinds}FloatList),";

			case "Framework.Core.Data.IJsonDataReader":
				return $"{prefix}\"{key}\", {kinds}Node),";

			case "Framework.Core.Maths.CBounds":
				return $"{prefix}\"{key}\", {kinds}Bounds),";

			case "Framework.Core.Maths.CFloat2":
				return $"{prefix}\"{key}\", {kinds}Float2),";

			case "Framework.Core.Maths.CFloat3":
				return $"{prefix}\"{key}\", {kinds}Float3),";

			case "Framework.Core.Maths.CFloat2[]":
				return $"{prefix}\"{key}\", {kinds}Float2List),";

			case "Framework.Core.Maths.CFloat3[]":
				return $"{prefix}\"{key}\", {kinds}Float3List),";

			case "Framework.Core.Maths.CQuaternion":
				return $"{prefix}\"{key}\", {kinds}Quaternion),";
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
				if (elementType is INamedTypeSymbol { TypeKind: TypeKind.Interface or TypeKind.Class, SpecialType: SpecialType.None })
					return $"{prefix}\"{key}\", {kinds}NestedMap, typeof(global::{elementType.ToDisplayString()})),";
				return null;
			}

			// String → string map (reader: ReadStringsDict)
			if (origDef == "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>"
			    && generic.TypeArguments.Length == 2
			    && generic.TypeArguments[0].SpecialType == SpecialType.System_String
			    && generic.TypeArguments[1].SpecialType == SpecialType.System_String)
			{
				return $"{prefix}\"{key}\", {kinds}StringMap),";
			}

			bool isList = origDef is
				"System.Collections.Generic.List<T>" or
				"System.Collections.Generic.HashSet<T>" or
				"System.Collections.Generic.IList<T>" or
				"System.Collections.Generic.IReadOnlyList<T>" or
				"System.Collections.Generic.ICollection<T>" or
				"System.Collections.Generic.IReadOnlyCollection<T>" or
				"System.Collections.Generic.IEnumerable<T>" or
				"System.Collections.Generic.ISet<T>" or
				"System.Collections.Generic.IReadOnlySet<T>";

			if (isList)
			{
				var elementType = generic.TypeArguments[0];

				// String collection → editable string list
				if (elementType.SpecialType == SpecialType.System_String)
					return $"{prefix}\"{key}\", {kinds}StringList),";

				if (elementType is INamedTypeSymbol { TypeKind: TypeKind.Interface or TypeKind.Class, SpecialType: SpecialType.None })
					return $"{prefix}\"{key}\", {kinds}NestedList, typeof(global::{elementType.ToDisplayString()})),";

				string elementTypeName = elementType.ToDisplayString();
				switch (elementTypeName)
				{
					case "string":
						return $"{prefix}\"{key}\", {kinds}StringList),";
					case "int":
					case "byte":
					case "ushort":
					case "uint":
					case "long":
					case "ulong":
					case "short":
						return $"{prefix}\"{key}\", {kinds}IntList),";
					case "float":
					case "double":
						return $"{prefix}\"{key}\", {kinds}FloatList),";
					case "Framework.Core.Maths.CFloat2":
						return $"{prefix}\"{key}\", {kinds}Float2List),";
					case "Framework.Core.Maths.CFloat3":
						return $"{prefix}\"{key}\", {kinds}Float3List),";
				}

				return null;
			}
		}

		// Array collections: T[] (e.g. ClubPostProgressionLevelDescription[])
		if (memberType is IArrayTypeSymbol { Rank: 1 } array)
		{
			var elementType = array.ElementType;

			if (elementType.SpecialType == SpecialType.System_String)
				return $"{prefix}\"{key}\", {kinds}StringList),";

			if (elementType is INamedTypeSymbol { TypeKind: TypeKind.Interface or TypeKind.Class, SpecialType: SpecialType.None })
				return $"{prefix}\"{key}\", {kinds}NestedList, typeof(global::{elementType.ToDisplayString()})),";

			switch (elementType.ToDisplayString())
			{
				case "int":
				case "byte":
				case "ushort":
				case "uint":
				case "long":
				case "ulong":
				case "short":
					return $"{prefix}\"{key}\", {kinds}IntList),";
				case "float":
				case "double":
					return $"{prefix}\"{key}\", {kinds}FloatList),";
				case "Framework.Core.Maths.CFloat2":
					return $"{prefix}\"{key}\", {kinds}Float2List),";
				case "Framework.Core.Maths.CFloat3":
					return $"{prefix}\"{key}\", {kinds}Float3List),";
			}

			return null;
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

	private readonly struct FieldData(string key, ISymbol member, EditorFieldHint editorHint, string editorPath = "")
	{
		public readonly string key = key;
		public readonly ISymbol member = member;
		public readonly EditorFieldHint editorHint = editorHint;
		public readonly string editorPath = editorPath;
	}
}