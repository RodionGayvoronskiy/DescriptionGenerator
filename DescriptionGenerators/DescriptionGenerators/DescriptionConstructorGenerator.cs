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
				"Modules.Framework.Core.DescriptionTypeAttribute", GeneratorShared.IsPartialNonStaticClass, FindDescriptionTypeAttribute)
			.Where(static foundForSourceGenerator => foundForSourceGenerator.HasValue)
			.Select(static (foundForSourceGenerator, _) => foundForSourceGenerator!.Value);

		context.RegisterSourceOutput(source: provider, action: GenerateCode);
	}

	private static DescriptionData? FindDescriptionTypeAttribute(GeneratorAttributeSyntaxContext context,
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

			// Пропускаем члены с атрибутом IgnoreKey, если установлен бит Constructor
			if (member.TryGetAnyAttributeInSelf(out AttributeData? ignoreAttr, new[] { IgnoreKeyAttribute.FullName }))
			{
				int targetBits = ignoreAttr?.ConstructorArguments.FirstOrDefault().Value is int v ? v : GeneratorShared.IgnoreAllBits;
				if ((targetBits & GeneratorShared.IgnoreConstructorBit) != 0)
					continue;
			}

			// Определяем ключ: либо из атрибута Key (значение из него или авто из имени), либо авто из имени.
			bool hasKey = member.TryGetAnyAttributeInSelf(out AttributeData? keyAttribute, new[] { KeyAttribute.FullName });
			string? key = hasKey ? keyAttribute?.ConstructorArguments.FirstOrDefault().Value as string : null;
			if (string.IsNullOrEmpty(key))
				key = GeneratorShared.ToSnakeCase(member.Name);

			// defaultValue — это named-property, а не ctor-параметр: в NamedArguments он присутствует
			// ТОЛЬКО когда задан явно. Поэтому сам факт наличия = opt-in на OrDefault-чтение (в т.ч. при значении null).
			bool hasDefaultValue = false;
			object? defValue = null;
			if (keyAttribute is not null)
			{
				foreach (var named in keyAttribute.NamedArguments)
				{
					if (named.Key != "defaultValue") continue;
					hasDefaultValue = true;
					defValue = named.Value.Value;
					break;
				}
			}

			// Key from [JsonItem] - the data source of truth for serialization (used for DESCGEN001).
			string? jsonKey = member.TryGetAnyAttributeInSelf(out AttributeData? jsonAttribute, new[] { JsonItemAttribute.FullName })
				? jsonAttribute?.ConstructorArguments.FirstOrDefault().Value as string
				: null;

			// [Key(flat: true)] opts a CFloat2/3 collection into the flat [x,y,(z),...] reader.
			// flat — ctor-параметр №1 (сразу после key).
			bool isFlat = keyAttribute is { ConstructorArguments.Length: >= 2 } && keyAttribute.ConstructorArguments[1].Value is true;

			list.Add(new KeyData(key!, member, hasDefaultValue, FormatDefaultArg(member.GetSymbolType()?.ToDisplayString(), defValue), jsonKey, isFlat, HasInitializer(member)));
		}

		return new DescriptionData(list.ToImmutableArray(), candidate, symbol);
	}

	// Maps generic collections to the matching reader call:
	//  - element string/int/float/byte → Read<E>ArrayOrEmpty
	//  - element CFloat2/CFloat3       → ReadFloat2/Float3ArrayOrEmpty
	//  - element interface/class       → context.InstantiateArray<E>
	// Collection kinds: concrete List/HashSet are wrapped; IList/IReadOnlyList/ICollection/
	// IReadOnlyCollection/IEnumerable get the array assigned directly (arrays implement them);
	// ISet/IReadOnlySet are wrapped in HashSet. Plus IReadOnlyDictionary<string,string>.
	private static bool TryGetCollectionRead(INamedTypeSymbol type, string key, string memberName, bool isFlat, out string assignment)
	{
		assignment = null;

		// Only BCL collections are consumed here. A System.Collections.Generic type is NEVER a
		// factory description, so even when it can't be read (e.g. List<some-struct> runtime cache,
		// Dictionary<string,int>, Queue<T>) we still return true to keep it OUT of the single-
		// Instantiate fallback — otherwise the ctor would emit context.Instantiate<List<…>> and
		// throw KeyNotFound at runtime. Real nested-description interfaces/classes are left to the
		// fallback by returning false here.
		if (type.OriginalDefinition.ContainingNamespace?.ToDisplayString() != "System.Collections.Generic")
			return false;

		var origDef = type.OriginalDefinition.ToDisplayString();

		if (origDef == "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>"
		    && type.TypeArguments.Length == 2
		    && type.TypeArguments[0].ToDisplayString() == "string"
		    && type.TypeArguments[1].ToDisplayString() == "string")
		{
			assignment = $"{memberName} = reader.ReadStringsDictOrEmpty(\"{key}\");";
			return true;
		}

		bool isConcreteList = origDef == "System.Collections.Generic.List<T>";
		bool isConcreteSet = origDef == "System.Collections.Generic.HashSet<T>";
		bool isListInterface = origDef is
			"System.Collections.Generic.IList<T>" or
			"System.Collections.Generic.IReadOnlyList<T>" or
			"System.Collections.Generic.ICollection<T>" or
			"System.Collections.Generic.IReadOnlyCollection<T>" or
			"System.Collections.Generic.IEnumerable<T>";
		bool isSetInterface = origDef is
			"System.Collections.Generic.ISet<T>" or
			"System.Collections.Generic.IReadOnlySet<T>";

		// Other BCL collection (Dictionary<string,non-string>, Queue, Stack, multi-arg, …) —
		// consume and skip (no read emitted).
		if (type.TypeArguments.Length != 1 || (!isConcreteList && !isConcreteSet && !isListInterface && !isSetInterface))
			return true;

		var element = type.TypeArguments[0];
		var elementName = element.ToDisplayString();

		// Source expression yielding an E[] for the element type.
		string source;
		switch (elementName)
		{
			case "string": source = $"reader.ReadStringArrayOrEmpty(\"{key}\")"; break;
			case "int": source = $"reader.ReadIntArrayOrEmpty(\"{key}\")"; break;
			case "float": source = $"reader.ReadFloatArrayOrEmpty(\"{key}\")"; break;
			case "byte": source = $"reader.ReadByteArrayOrEmpty(\"{key}\")"; break;
			case "Framework.Core.Maths.CFloat2": source = $"reader.{(isFlat ? "ReadFlatFloat2ArrayOrEmpty" : "ReadFloat2ArrayOrEmpty")}(\"{key}\")"; break;
			case "Framework.Core.Maths.CFloat3": source = $"reader.{(isFlat ? "ReadFlatFloat3ArrayOrEmpty" : "ReadFloat3ArrayOrEmpty")}(\"{key}\")"; break;
			default:
				// Element is a non-readable value type (e.g. a sample struct) — consume & skip.
				if (element is not INamedTypeSymbol { TypeKind: TypeKind.Interface or TypeKind.Class, SpecialType: SpecialType.None })
					return true;
				source = $"context.InstantiateArray<global::{elementName}>(\"{key}\", reader)";
				break;
		}

		var elementRef = elementName is "string" or "int" or "float" or "byte" ? elementName : $"global::{elementName}";

		if (isConcreteList)
			assignment = $"{memberName} = new global::System.Collections.Generic.List<{elementRef}>({source});";
		else if (isConcreteSet || isSetInterface)
			assignment = $"{memberName} = new global::System.Collections.Generic.HashSet<{elementRef}>({source});";
		else
			assignment = $"{memberName} = {source};";

		return true;
	}

	private struct DescriptionData(
		ImmutableArray<KeyData> members,
		ClassDeclarationSyntax declaration,
		ISymbol symbol)
	{
		public readonly ImmutableArray<KeyData> members = members;
		public readonly ClassDeclarationSyntax declaration = declaration;
		public readonly ISymbol symbol = symbol;
	}

	private struct KeyData(string key, ISymbol symbol, bool hasDefaultValue, string defaultValue, string? jsonKey, bool isFlat, bool hasInitializer)
	{
		public readonly string key = key;
		public readonly ISymbol symbol = symbol;

		public readonly bool hasDefaultValue = hasDefaultValue;
		public readonly string defaultValue = defaultValue;
		public readonly string? jsonKey = jsonKey;
		public readonly bool isFlat = isFlat;

		/// <summary>Член объявлен с инициализатором — его значение и есть дефолт.</summary>
		public readonly bool hasInitializer = hasInitializer;
	}

	// Инициализатор поля выполняется до тела конструктора, поэтому его значение можно
	// сохранить, читая узел только когда ключ реально есть в данных.
	private static bool HasInitializer(ISymbol member)
	{
		foreach (var reference in member.DeclaringSyntaxReferences)
		{
			switch (reference.GetSyntax())
			{
				case VariableDeclaratorSyntax { Initializer: not null }:
				case PropertyDeclarationSyntax { Initializer: not null }:
					return true;
			}
		}

		return false;
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

	private static void GenerateCode(SourceProductionContext context, DescriptionData data)
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
			var memberType = member.symbol.GetSymbolType();
			var typeName = memberType?.ToDisplayString();

			// DESCGEN001: ключ из [JsonItem] (источник истины данных) расходится с ключом,
			// по которому генератор читает узел. Иначе член молча останется пустым.
			if (member.jsonKey is { Length: > 0 } jsonKey && jsonKey != member.key)
			{
				context.ReportDiagnostic(Diagnostic.Create(JsonItemKeyMismatch,
					member.symbol.Locations.FirstOrDefault(), member.symbol.Name, jsonKey, member.key));
			}

			// DESCGEN002: член не получил ни одного чтения и останется null/default.
			var emitted = true;

			// Член с инициализатором читается условно: нет ключа — остаётся объявленное значение.
			// Так дефолт работает и для массивов, коллекций и вложенных объектов, которым
			// compile-time-константу в атрибуте задать нельзя.
			if (member.hasInitializer)
			{
				code.AppendLine($"if (reader.Contains(\"{member.key}\"))");
				code.BeginBlock();
			}

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
					code.AppendLine($"{member.symbol.Name} = reader.{(member.isFlat ? "ReadFlatFloat2ArrayOrEmpty" : "ReadFloat2ArrayOrEmpty")}(\"{member.key}\");");
					break;
				case "Framework.Core.Maths.CFloat3[]":
					code.AppendLine($"{member.symbol.Name} = reader.{(member.isFlat ? "ReadFlatFloat3ArrayOrEmpty" : "ReadFloat3ArrayOrEmpty")}(\"{member.key}\");");
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
				case "CodeStage.AntiCheat.ObscuredTypes.ObscuredDouble":
					code.AppendLine($"{member.symbol.Name} = reader.ReadDoubleOrDefault(\"{member.key}\", defaultValue: {member.defaultValue});");
					break;
				case "CodeStage.AntiCheat.ObscuredTypes.ObscuredLong":
					code.AppendLine($"{member.symbol.Name} = reader.ReadLongOrDefault(\"{member.key}\", defaultValue: {member.defaultValue});");
					break;
				case "Framework.Core.Data.IJsonDataReader":
					code.AppendLine($"{member.symbol.Name} = reader.ReadNodeOrEmpty(\"{member.key}\");");
					break;
				default:
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
					else if (memberType is INamedTypeSymbol { IsGenericType: true } collectionType
							&& TryGetCollectionRead(collectionType, member.key, member.symbol.Name, member.isFlat, out string collectionAssignment))
						{
							if (!string.IsNullOrEmpty(collectionAssignment))
								code.AppendLine(collectionAssignment);
							else
								emitted = false;
						}
						else if (memberType is INamedTypeSymbol { TypeKind: TypeKind.Interface or TypeKind.Class })
					{
						// [Key(defaultValue = ...)] задан (пусть даже null) → поле опционально: отсутствие ключа → null.
						// Не задан → фабричный дефолт (isDefault-инстанс), как для обязательных вложенных полей.
						string instantiate = member.hasDefaultValue ? "InstantiateOrDefault" : "Instantiate";
						code.AppendLine(
							$"{member.symbol.Name} = context.{instantiate}<global::{typeName}>(\"{member.key}\", reader);");
					}
					else
					{
						emitted = false;
					}

					break;
			}

			if (member.hasInitializer)
			{
				code.EndBlock();
			}

			if (!emitted)
			{
				context.ReportDiagnostic(Diagnostic.Create(MemberNotRead,
					member.symbol.Locations.FirstOrDefault(), member.symbol.Name, typeName));
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
	private static readonly AttributeText JsonItemAttribute = new("JsonItemAttribute", "Framework.Core.Serializers");

	private static readonly DiagnosticDescriptor JsonItemKeyMismatch = new(
		id: "DESCGEN001",
		title: "JsonItem key differs from generated read key",
		messageFormat: "Member '{0}' has [JsonItem(\"{1}\")] but the generated constructor reads key '{2}'. Add [Key(\"{1}\")] so it reads the data key, otherwise the member is silently left empty.",
		category: "DescriptionGenerators",
		defaultSeverity: DiagnosticSeverity.Warning,
		isEnabledByDefault: true);

	private static readonly DiagnosticDescriptor MemberNotRead = new(
		id: "DESCGEN002",
		title: "Description member is not read",
		messageFormat: "Member '{0}' of type '{1}' is not read by the generated constructor and stays null/default. Mark it [IgnoreKey] if intentional, or give it a readable type.",
		category: "DescriptionGenerators",
		defaultSeverity: DiagnosticSeverity.Warning,
		isEnabledByDefault: true);
}