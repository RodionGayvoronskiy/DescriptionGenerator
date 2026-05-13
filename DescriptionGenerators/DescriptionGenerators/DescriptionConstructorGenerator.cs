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

			list.Add(new KeyData(key!, member));
		}

		return new DescriptionData(string.Empty, list.ToImmutableArray(), candidate, symbol);
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

	private struct KeyData(string key, ISymbol symbol)
	{
		public readonly string key = key;
		public readonly ISymbol symbol = symbol;
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
				$"{ctorModifier} {data.symbol.Name}(Framework.Core.IContext context, string id, Framework.Core.Data.IJsonDataReader reader) : base(context, id, reader)")
			.BeginBlock();

		foreach (var member in data.members)
		{
			var typeName = member.symbol.GetSymbolType()?.ToDisplayString();
			switch (typeName)
			{
				case "string":
					code.AppendLine($"{member.symbol.Name} = reader.ReadStringOrDefault(\"{member.key}\");");
					break;
				case "int":
					code.AppendLine($"{member.symbol.Name} = reader.ReadIntOrDefault(\"{member.key}\");");
					break;
				case "bool":
					code.AppendLine($"{member.symbol.Name} = reader.ReadBoolOrDefault(\"{member.key}\");");
					break;
				case "byte":
					code.AppendLine($"{member.symbol.Name} = reader.ReadByteOrDefault(\"{member.key}\");");
					break;
				case "double":
					code.AppendLine($"{member.symbol.Name} = reader.ReadDoubleOrDefault(\"{member.key}\");");
					break;
				case "long":
					code.AppendLine($"{member.symbol.Name} = reader.ReadLongOrDefault(\"{member.key}\");");
					break;
				case "ulong":
					code.AppendLine($"{member.symbol.Name} = reader.ReadULongOrDefault(\"{member.key}\");");
					break;
				case "ushort":
					code.AppendLine($"{member.symbol.Name} = reader.ReadUshortOrDefault(\"{member.key}\");");
					break;
				case "uint":
					code.AppendLine($"{member.symbol.Name} = reader.ReadUIntOrDefault(\"{member.key}\");");
					break;
				case "float":
					code.AppendLine($"{member.symbol.Name} = reader.ReadFloatOrDefault(\"{member.key}\");");
					break;
				case "string[]":
					code.AppendLine($"{member.symbol.Name} = reader.ReadStringArrayOrEmpty(\"{member.key}\");");
					break;
				case "float[]":
					code.AppendLine($"{member.symbol.Name} = reader.ReadFloatArrayOrEmpty(\"{member.key}\");");
					break;
				case "CodeStage.AntiCheat.ObscuredTypes.ObscuredString":
					code.AppendLine($"{member.symbol.Name} = reader.ReadStringOrDefault(\"{member.key}\");");
					break;
				case "CodeStage.AntiCheat.ObscuredTypes.ObscuredBool":
					code.AppendLine($"{member.symbol.Name} = reader.ReadBoolOrDefault(\"{member.key}\");");
					break;
				case "CodeStage.AntiCheat.ObscuredTypes.ObscuredInt":
					code.AppendLine($"{member.symbol.Name} = reader.ReadIntOrDefault(\"{member.key}\");");
					break;
				case "CodeStage.AntiCheat.ObscuredTypes.ObscuredFloat":
					code.AppendLine($"{member.symbol.Name} = reader.ReadFloatOrDefault(\"{member.key}\");");
					break;
				case "CodeStage.AntiCheat.ObscuredTypes.ObscuredLong":
					code.AppendLine($"{member.symbol.Name} = reader.ReadLongOrDefault(\"{member.key}\");");
					break;
				case "Framework.Core.Data.IJsonDataReader":
					code.AppendLine($"{member.symbol.Name} = reader.ReadNodeOrEmpty(\"{member.key}\");");
					break;
				default:
					var memberType = member.symbol.GetSymbolType();
					if (memberType is INamedTypeSymbol { TypeKind: TypeKind.Enum })
					{
						code.AppendLine(
							$"{member.symbol.Name} = reader.ReadEnumOrDefault<{typeName}>(\"{member.key}\");");
					}
					else if (memberType is INamedTypeSymbol { IsGenericType: true } genericMember
					         && genericMember.OriginalDefinition.ToDisplayString() ==
					         "Framework.Core.Collections.IDescriptions<T>")
					{
						var elementType = genericMember.TypeArguments[0].ToDisplayString();
						code.AppendLine(
							$"{member.symbol.Name} = context.InstantiateDescriptions<global::{elementType}>(\"{member.key}\", reader);");
					}
					else if (memberType is INamedTypeSymbol { TypeKind: TypeKind.Interface or TypeKind.Class })
					{
						code.AppendLine(
							$"{member.symbol.Name} = context.Instantiate<{typeName}>(\"{member.key}\", reader);");
					}

					break;
			}
		}

		code.AppendLine("OnConstructed(context, reader);")
			.EndBlock()
			.AppendLine("partial void OnConstructed(Framework.Core.IContext context, Framework.Core.Data.IJsonDataReader reader);")
			.EndClass(@namespace);

		context.AddSource(declarationText.GetFileName(@namespace, "Constructor"), code.GetSourceText());
	}

	private static readonly AttributeText KeyAttribute = new("KeyAttribute", "Modules.Framework.Core");
	private static readonly AttributeText IgnoreKeyAttribute = new("IgnoreKeyAttribute", "Modules.Framework.Core");
}