using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Aspid.Generators.Helper;
using Microsoft.CodeAnalysis;
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
	private static readonly AttributeText KeyAttribute = new("KeyAttribute", "Modules.Framework.Core");
	private static readonly AttributeText IgnoreKeyAttribute = new("IgnoreKeyAttribute", "Modules.Framework.Core");

	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		var provider1 = context.SyntaxProvider
			.ForAttributeWithMetadataName(
				"Modules.Framework.Core.DescriptionTypeAttribute",
				GeneratorShared.IsPartialNonStaticClass,
				FindData)
			.Where(static x => x.HasValue)
			.Select(static (x, _) => x!.Value);

		var provider2 = context.SyntaxProvider
			.ForAttributeWithMetadataName(
				"Modules.Framework.Core.DescriptionEditorSchemaAttribute",
				GeneratorShared.IsPartialNonStaticClass,
				FindData)
			.Where(static x => x.HasValue)
			.Select(static (x, _) => x!.Value);

		context.RegisterSourceOutput(provider1, GenerateCode);
		context.RegisterSourceOutput(provider2, GenerateCode);
	}

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
				if (member.TryGetAnyAttributeInSelf(out AttributeData? ignoreAttr, new[] { IgnoreKeyAttribute.FullName }))
				{
					int targetBits = ignoreAttr?.ConstructorArguments.FirstOrDefault().Value is int v ? v : GeneratorShared.IgnoreAllBits;
					if ((targetBits & GeneratorShared.IgnoreEditorSchemaBit) != 0)
						continue;
				}

				// [Key(flat: true)] fields use a packed [x,y,(z),...] layout the editor cannot render;
				// a Float2/3List entry would corrupt them on re-save, so hide them.
				if (member.TryGetAnyAttributeInSelf(out AttributeData? flatKey, new[] { KeyAttribute.FullName })
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
				if (member.TryGetAnyAttributeInSelf(out AttributeData? keyAttr, new[] { KeyAttribute.FullName })
				    && keyAttr?.ConstructorArguments.FirstOrDefault().Value is string explicitKey
				    && !string.IsNullOrEmpty(explicitKey))
				{
					key = explicitKey;
					hasExplicitKey = true;
				}
				else
				{
					key = GeneratorShared.ToSnakeCase(member.Name);
				}

				// Internal framework identity/discriminator keys — hidden unless a field
				// explicitly opts into the key via [Key(...)].
				if (!hasExplicitKey && key is "id" or "type" or "m_id" or "m_type") continue;

				// Derived class already declared this key — skip base version
				if (!seenKeys.Add(key)) continue;

				// [Key(defaultValue = ...)] — named-property: присутствует в NamedArguments только
				// когда задан явно, поэтому сам факт наличия и есть признак объявленного дефолта.
				bool hasDefaultValue = false;
				object? defaultValue = null;
				if (keyAttr is not null)
				{
					foreach (var named in keyAttr.NamedArguments)
					{
						if (named.Key != "defaultValue") continue;
						hasDefaultValue = true;
						defaultValue = named.Value.Value;
						break;
					}
				}

				// Хинты-поля (Sprite/Reference/Curve/…) определяет редактор рефлексией по подклассам
				// EditorFieldAttribute; схема несёт только тип-зависимый kind.
				levelFields.Add(new FieldData(key, member, hasDefaultValue, defaultValue));
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

			string? entry = BuildEntry(field.key, typeName, memberType);
			if (entry == null) continue;

			if (field.hasDefaultValue)
				entry = WithDefault(entry, FormatDefaultLiteral(typeName, memberType, field.defaultValue));

			code.AppendLine(entry);
		}

		code.EndBlock();
		code.AppendLine(";");
		code.EndClass(@namespace);

		context.AddSource(declarationText.GetFileName(@namespace, "EditorSchema"), code.GetSourceText());
	}

	// ── Entry builders ───────────────────────────────────────────────────────────

	// Дефолт несут только скаляры и enum: у остальных видов [Key(defaultValue = ...)] не бывает
	// (значение обязано быть compile-time-константой), а редактор рисует их своими контролами.
	private static readonly string[] s_defaultableKinds =
	{
		"EditorFieldKind.String)",
		"EditorFieldKind.Int)",
		"EditorFieldKind.Float)",
		"EditorFieldKind.Bool)",
		"EditorFieldKind.Enum,"
	};

	private static string WithDefault(string entry, string? literal)
	{
		if (literal == null) return entry;

		const string ctor = "new global::Framework.Core.DescriptionEditorField(";
		const string factory = "global::Framework.Core.DescriptionEditorField.WithDefault(";

		if (!entry.StartsWith(ctor, StringComparison.Ordinal)) return entry;
		if (!s_defaultableKinds.Any(kind => entry.Contains(kind))) return entry;

		string body = entry.TrimEnd();

		if (body.EndsWith(",", StringComparison.Ordinal))
			body = body.Substring(0, body.Length - 1);

		if (!body.EndsWith(")", StringComparison.Ordinal)) return entry;

		string arguments = body.Substring(ctor.Length, body.Length - ctor.Length - 1);

		return $"{factory}{arguments}, {literal}),";
	}

	private static string? FormatDefaultLiteral(string? typeName, ITypeSymbol? memberType, object? value)
	{
		if (value == null) return "null";

		if (memberType is INamedTypeSymbol { TypeKind: TypeKind.Enum })
			return $"(global::{memberType.ToDisplayString()})({Convert.ToInt64(value, CultureInfo.InvariantCulture)})";

		return value switch
		{
			bool boolValue => boolValue ? "true" : "false",
			string stringValue => $"\"{stringValue.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"",
			float floatValue => $"{floatValue.ToString(CultureInfo.InvariantCulture)}f",
			double doubleValue => $"{doubleValue.ToString(CultureInfo.InvariantCulture)}d",
			long longValue => $"{longValue.ToString(CultureInfo.InvariantCulture)}L",
			ulong ulongValue => $"{ulongValue.ToString(CultureInfo.InvariantCulture)}UL",
			uint uintValue => $"{uintValue.ToString(CultureInfo.InvariantCulture)}U",
			_ => Convert.ToString(value, CultureInfo.InvariantCulture)
		};
	}

	private static string? BuildEntry(string key, string? typeName, ITypeSymbol? memberType)
	{
		const string prefix = "new global::Framework.Core.DescriptionEditorField(";
		const string kinds = "global::Framework.Core.EditorFieldKind.";

		if (typeName == null) return null;

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
				return BuildListEntry(generic.TypeArguments[0], key);
		}

		// Array collections: T[] (e.g. ClubPostProgressionLevelDescription[])
		if (memberType is IArrayTypeSymbol { Rank: 1 } array)
			return BuildListEntry(array.ElementType, key);

		// Single nested Description (interface or class)
		if (memberType is INamedTypeSymbol { TypeKind: TypeKind.Interface or TypeKind.Class })
			return $"{prefix}\"{key}\", {kinds}Nested, typeof(global::{memberType.ToDisplayString()})),";

		return null;
	}

	/// <summary>
	/// Maps the element type of a collection (List/HashSet/IList/.../array) to its editor
	/// list-kind entry. Shared by both the generic-collection and array branches.
	/// </summary>
	private static string? BuildListEntry(ITypeSymbol elementType, string key)
	{
		const string prefix = "new global::Framework.Core.DescriptionEditorField(";
		const string kinds = "global::Framework.Core.EditorFieldKind.";

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
			default:
				return null;
		}
	}

	// ── Data types ───────────────────────────────────────────────────────────────

	private readonly struct DescriptionData(ImmutableArray<FieldData> fields, ClassDeclarationSyntax declaration)
	{
		public readonly ImmutableArray<FieldData> fields = fields;
		public readonly ClassDeclarationSyntax declaration = declaration;
	}

	private readonly struct FieldData(string key, ISymbol member, bool hasDefaultValue, object? defaultValue)
	{
		public readonly string key = key;
		public readonly ISymbol member = member;
		public readonly bool hasDefaultValue = hasDefaultValue;
		public readonly object? defaultValue = defaultValue;
	}
}