using System;
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

[Generator(LanguageNames.CSharp)]
public class DescriptionConstructorGenerator : IIncrementalGenerator
{
	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		var provider = context.SyntaxProvider.ForAttributeWithMetadataName(
				"Modules.Framework.Core.DescriptionTypeAttribute", SyntaxPredicate, FindDescriptionTypeAttribute)
			.Where(static foundForSourceGenerator => foundForSourceGenerator.HasValue)
			.Select(static (foundForSourceGenerator, _) => foundForSourceGenerator!.Value);

		context.RegisterSourceOutput(source: provider, action: GenerateCode);
	}

	private bool SyntaxPredicate(SyntaxNode node, CancellationToken cancellationToken)
	{
		return node is ClassDeclarationSyntax { AttributeLists.Count: > 0 } candidate
		       && candidate.Modifiers.Any(SyntaxKind.PartialKeyword)
		       && !candidate.Modifiers.Any(SyntaxKind.StaticKeyword);
	}

	private DescriptionData? FindDescriptionTypeAttribute(GeneratorAttributeSyntaxContext context,
		CancellationToken cancellationToken)
	{
		if (context.TargetSymbol is not INamedTypeSymbol symbol) return null;

		var candidate = Unsafe.As<ClassDeclarationSyntax>(context.TargetNode);

		var list = new List<KeyData>();

		foreach (var member in symbol.GetMembers())
		{
			// Обрабатываем только поля и свойства
			if (member is not (IPropertySymbol or IFieldSymbol))
				continue;

			// Пропускаем статические члены
			if (member.IsStatic)
				continue;

			// Пропускаем compiler-generated backing fields (они начинаются с '<')
			if (member.Name.StartsWith("<"))
				continue;

			// Пропускаем внутренние члены (обрабатываем только public/protected)
			if (member.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.Private
			    or Accessibility.ProtectedOrInternal))
				continue;

			// Пропускаем члены с атрибутом IgnoreKey
			if (member.TryGetAnyAttributeInSelf(out AttributeData? _, IgnoreKeyAttribute))
				continue;

			// Определяем ключ: либо из атрибута Key, либо автоматически из имени
			string? key;
			if (member.TryGetAnyAttributeInSelf(out AttributeData? keyAttribute, KeyAttribute))
			{
				// Если есть атрибут Key, берем значение из него или генерируем из имени
				key = keyAttribute?.ConstructorArguments.FirstOrDefault().Value as string;
				if (string.IsNullOrEmpty(key))
				{
					key = ToSnakeCase(member.Name);
				}
			}
			else
			{
				// Если атрибута Key нет, генерируем ключ автоматически
				key = ToSnakeCase(member.Name);
			}
			
			object? defValue = member.TryGetAnyAttributeInSelf(out AttributeData? attribute, KeyAttribute) ? attribute?.ConstructorArguments[1].Value : null;

			list.Add(new KeyData(key!, member, defValue is not null, FormatDefaultArg(member.GetSymbolType()?.ToDisplayString(), defValue)));
		}

		return new DescriptionData(string.Empty, list.ToImmutableArray(), candidate, symbol);
	}

	// Maps primitive generic collections (List/HashSet/IList/IReadOnlyList/ICollection/
	// IReadOnlyCollection/IEnumerable<T> for string/int/float/byte, and
	// IReadOnlyDictionary<string,string>) to the matching reader call. Arrays already
	// implement the read-only/list/collection/enumerable interfaces, so those are
	// assigned directly; concrete List/HashSet are wrapped.
	private static bool TryGetPrimitiveCollectionRead(INamedTypeSymbol type, string key, string memberName, out string assignment)
	{
		assignment = null;
		var origDef = type.OriginalDefinition.ToDisplayString();

		if (origDef == "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>"
		    && type.TypeArguments.Length == 2
		    && type.TypeArguments[0].ToDisplayString() == "string"
		    && type.TypeArguments[1].ToDisplayString() == "string")
		{
			assignment = $"{memberName} = reader.ReadStringsDictOrEmpty(\"{key}\");";
			return true;
		}

		if (type.TypeArguments.Length != 1) return false;

		var element = type.TypeArguments[0].ToDisplayString();
		var arrayReader = element switch
		{
			"string" => "ReadStringArrayOrEmpty",
			"int" => "ReadIntArrayOrEmpty",
			"float" => "ReadFloatArrayOrEmpty",
			"byte" => "ReadByteArrayOrEmpty",
			_ => null
		};
		if (arrayReader == null) return false;

		switch (origDef)
		{
			case "System.Collections.Generic.List<T>":
				assignment = $"{memberName} = new global::System.Collections.Generic.List<{element}>(reader.{arrayReader}(\"{key}\"));";
				return true;
			case "System.Collections.Generic.HashSet<T>":
				assignment = $"{memberName} = new global::System.Collections.Generic.HashSet<{element}>(reader.{arrayReader}(\"{key}\"));";
				return true;
			case "System.Collections.Generic.IList<T>":
			case "System.Collections.Generic.IReadOnlyList<T>":
			case "System.Collections.Generic.ICollection<T>":
			case "System.Collections.Generic.IReadOnlyCollection<T>":
			case "System.Collections.Generic.IEnumerable<T>":
				assignment = $"{memberName} = reader.{arrayReader}(\"{key}\");";
				return true;
			default:
				return false;
		}
	}

	private static string ToSnakeCase(string name)
	{
		if (string.IsNullOrEmpty(name))
			return name;

		if (name.StartsWith("m_"))
			name = name.Substring(2);

		var builder = new System.Text.StringBuilder();
		for (int i = 0; i < name.Length; i++)
		{
			var c = name[i];
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

	private struct DescriptionData(
		string type,
		ImmutableArray<KeyData> members,
		ClassDeclarationSyntax declaration,
		ISymbol symbol)
	{
		public readonly string type = type;
		public readonly ImmutableArray<KeyData> members = members;
		public readonly ClassDeclarationSyntax declaration = declaration;
		public readonly ISymbol symbol = symbol;
	}

	private struct KeyData(string key, ISymbol symbol, bool hasDefaultValue, string defaultValue)
	{
		public readonly string key = key;
		public readonly ISymbol symbol = symbol;
	
		public readonly bool hasDefaultValue = hasDefaultValue;
		public readonly string defaultValue = defaultValue;
	}
	
	private static string FormatDefaultArg(string? typeName, object? defaultValue)
	{
		if (defaultValue == null) return "default";
		if (defaultValue is bool boolValue)
			return boolValue ? "true" : "false";
		if (defaultValue is float floatValue)
			return floatValue.ToString(System.Globalization.CultureInfo.InvariantCulture) + "f";
		if (defaultValue is double doubleValue)
			return doubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
		var literal = typeName switch
		{
			_ => $"{defaultValue}"
		};
		return literal;
	}

	private void GenerateCode(SourceProductionContext context, DescriptionData data)
	{
		var declaration = data.declaration;
		var @namespace = declaration.GetNamespaceName();
		var declarationText = new DeclarationText(declaration);

		var isAbstract = declaration.Modifiers.Any(SyntaxKind.AbstractKeyword);
		var ctorModifier = isAbstract ? "protected" : "public";

		var code = new CodeWriter();
		code.AppendLine($"using Framework.Core.Factories;")
			.BeginClass(@namespace, declarationText)
			.AppendLine(
				$"{ctorModifier} {data.symbol.Name}(global::Framework.Core.IContext context, string id, global::Framework.Core.Data.IJsonDataReader reader) : base(context, id, reader)")
			.BeginBlock();

		foreach (KeyData member in data.members)
		{
			var typeName = member.symbol.GetSymbolType()?.ToDisplayString();
			switch (typeName)
			{
				case "string":
				{
					var defaultValue = member.hasDefaultValue ? $"\"{member.defaultValue}\"" : "default";
					code.AppendLine(
						$"{member.symbol.Name} = reader.ReadStringOrDefault(\"{member.key}\", defaultValue: {defaultValue});");
				}
					break;
				case "int":
					code.AppendLine($"{member.symbol.Name} = reader.ReadIntOrDefault(\"{member.key}\", defaultValue: {member.defaultValue});");
					break;
				case "bool":
					code.AppendLine($"{member.symbol.Name} = reader.ReadBoolOrDefault(\"{member.key}\", defaultValue: {member.defaultValue});");
					break;
				case "byte":
					code.AppendLine($"{member.symbol.Name} = reader.ReadByteOrDefault(\"{member.key}\", defaultValue: {member.defaultValue});");
					break;
				case "double":
					code.AppendLine($"{member.symbol.Name} = reader.ReadDoubleOrDefault(\"{member.key}\", defaultValue: {member.defaultValue});");
					break;
				case "long":
					code.AppendLine($"{member.symbol.Name} = reader.ReadLongOrDefault(\"{member.key}\", defaultValue: {member.defaultValue});");
					break;
				case "ulong":
					code.AppendLine($"{member.symbol.Name} = reader.ReadULongOrDefault(\"{member.key}\", defaultValue: {member.defaultValue});");
					break;
				case "ushort":
					code.AppendLine($"{member.symbol.Name} = reader.ReadUshortOrDefault(\"{member.key}\", defaultValue: {member.defaultValue});");
					break;
				case "uint":
					code.AppendLine($"{member.symbol.Name} = reader.ReadUIntOrDefault(\"{member.key}\", defaultValue: {member.defaultValue});");
					break;
				case "float":
					code.AppendLine($"{member.symbol.Name} = reader.ReadFloatOrDefault(\"{member.key}\", defaultValue: {member.defaultValue});");
					break;
				case "string[]":
					code.AppendLine(member.hasDefaultValue
						? $"{member.symbol.Name} = reader.ReadStringArrayOrDefault(\"{member.key}\", defaultValue: {member.defaultValue});"
						: $"{member.symbol.Name} = reader.ReadStringArrayOrEmpty(\"{member.key}\");");
					break;
				case "float[]":
					code.AppendLine(member.hasDefaultValue
						? $"{member.symbol.Name} = reader.ReadFloatArrayOrDefault(\"{member.key}\", defaultValue: {member.defaultValue});"
						: $"{member.symbol.Name} = reader.ReadFloatArrayOrEmpty(\"{member.key}\");");
					break;
				case "int[]":
					code.AppendLine(member.hasDefaultValue
						? $"{member.symbol.Name} = reader.ReadIntArrayOrDefault(\"{member.key}\", defaultValue: {member.defaultValue});"
						: $"{member.symbol.Name} = reader.ReadIntArrayOrEmpty(\"{member.key}\");");
					break;
				case "byte[]":
					code.AppendLine(member.hasDefaultValue
						? $"{member.symbol.Name} = reader.ReadByteArrayOrDefault(\"{member.key}\", defaultValue: {member.defaultValue});"
						: $"{member.symbol.Name} = reader.ReadByteArrayOrEmpty(\"{member.key}\");");
					break;
				case "Framework.Core.Maths.CBounds":
					code.AppendLine($"{member.symbol.Name} = reader.ReadBoundsOrDefault(\"{member.key}\");");
					break;
				case "Framework.Core.Maths.CFloat2[]":
					code.AppendLine($"{member.symbol.Name} = reader.ReadFloat2ArrayOrEmpty(\"{member.key}\");");
					break;
				case "Framework.Core.Maths.CFloat3[]":
					code.AppendLine($"{member.symbol.Name} = reader.ReadFloat3ArrayOrEmpty(\"{member.key}\");");
					break;
				case "Framework.Core.Maths.CFloat2":
					code.AppendLine($"{member.symbol.Name} = reader.ReadFloat2OrDefault(\"{member.key}\");");
					break;
				case "Framework.Core.Maths.CFloat3":
					code.AppendLine($"{member.symbol.Name} = reader.ReadFloat3OrDefault(\"{member.key}\");");
					break;
				case "Framework.Core.Maths.CQuaternion":
					code.AppendLine($"{member.symbol.Name} = reader.ReadQuaternionOrDefault(\"{member.key}\");");
					break;
				case "CodeStage.AntiCheat.ObscuredTypes.ObscuredString":
				{
					var defaultValue = member.hasDefaultValue ? $"\"{member.defaultValue}\"" : "default";
					code.AppendLine(
						$"{member.symbol.Name} = reader.ReadStringOrDefault(\"{member.key}\", defaultValue: {defaultValue});");
				}
					break;
				case "CodeStage.AntiCheat.ObscuredTypes.ObscuredBool":
					code.AppendLine($"{member.symbol.Name} = reader.ReadBoolOrDefault(\"{member.key}\", defaultValue: {member.defaultValue});");
					break;
				case "CodeStage.AntiCheat.ObscuredTypes.ObscuredInt":
					code.AppendLine($"{member.symbol.Name} = reader.ReadIntOrDefault(\"{member.key}\", defaultValue: {member.defaultValue});");
					break;
				case "CodeStage.AntiCheat.ObscuredTypes.ObscuredFloat":
					code.AppendLine($"{member.symbol.Name} = reader.ReadFloatOrDefault(\"{member.key}\", defaultValue: {member.defaultValue});");
					break;
				case "CodeStage.AntiCheat.ObscuredTypes.ObscuredLong":
					code.AppendLine($"{member.symbol.Name} = reader.ReadLongOrDefault(\"{member.key}\", defaultValue: {member.defaultValue});");
					break;
				case "Framework.Core.Data.IJsonDataReader":
					code.AppendLine($"{member.symbol.Name} = reader.ReadNodeOrEmpty(\"{member.key}\");");
					break;
				default:
					var memberType = member.symbol.GetSymbolType();
					if (memberType is INamedTypeSymbol { TypeKind: TypeKind.Enum })
					{
						code.AppendLine(
							$"{member.symbol.Name} = reader.ReadEnumOrDefault<global::{typeName}>(\"{member.key}\", defaultValue: (global::{typeName}){member.defaultValue});");
					}
					else if (memberType is INamedTypeSymbol { IsGenericType: true } genericMember
					         && genericMember.OriginalDefinition.ToDisplayString() ==
					         "Framework.Core.Collections.IDescriptions<T>")
					{
						var elementType = genericMember.TypeArguments[0].ToDisplayString();
						code.AppendLine(
							$"{member.symbol.Name} = context.InstantiateDescriptions<global::{elementType}>(\"{member.key}\", reader);");
					}
					else if (memberType is IArrayTypeSymbol { ElementType: INamedTypeSymbol { TypeKind: TypeKind.Interface or TypeKind.Class } arrayElement })
					{
						var elementTypeName = arrayElement.ToDisplayString();
						code.AppendLine(
							$"{member.symbol.Name} = context.InstantiateArray<global::{elementTypeName}>(\"{member.key}\", reader);");
					}
					else if (memberType is INamedTypeSymbol { IsGenericType: true } primitiveColl
							&& TryGetPrimitiveCollectionRead(primitiveColl, member.key, member.symbol.Name, out string collectionAssignment))
						{
							code.AppendLine(collectionAssignment);
						}
						else if (memberType is INamedTypeSymbol { TypeKind: TypeKind.Interface or TypeKind.Class })
					{
						code.AppendLine(
							$"{member.symbol.Name} = context.Instantiate<global::{typeName}>(\"{member.key}\", reader);");
					}

					break;
			}
		}

		code.AppendLine("OnConstructed(context, reader);")
			.EndBlock()
			.AppendLine("partial void OnConstructed(global::Framework.Core.IContext context, global::Framework.Core.Data.IJsonDataReader reader);")
			.EndClass(@namespace);

		context.AddSource(declarationText.GetFileName(@namespace, "Constructor"), code.GetSourceText());
	}

	private static readonly AttributeText KeyAttribute = new("KeyAttribute", "Modules.Framework.Core");
	private static readonly AttributeText IgnoreKeyAttribute = new("IgnoreKeyAttribute", "Modules.Framework.Core");
}