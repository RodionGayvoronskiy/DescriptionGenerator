using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Aspid.Generators.Helper;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DescriptionGenerators;

[Generator(LanguageNames.CSharp)]
public class FactoryRegistrationGenerator : IIncrementalGenerator
{
	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		// Find all AddNew<T>("path") invocations
		var addNewInvocations = context.SyntaxProvider.CreateSyntaxProvider(
			predicate: IsAddNewInvocation,
			transform: GetAddNewData
		).Where(x => x.HasValue).Select((x, _) => x!.Value);

		// Find [DescriptionType] classes in current compilation source files
		var descriptionTypesFromSource = context.SyntaxProvider.ForAttributeWithMetadataName(
			"Modules.Framework.Core.DescriptionTypeAttribute",
			predicate: (node, _) => node is ClassDeclarationSyntax,
			transform: GetDescriptionTypeData
		).Where(x => x.HasValue).Select((x, _) => x!.Value);

		// Find [DescriptionType] classes in referenced assemblies (not visible to ForAttributeWithMetadataName)
		var descriptionTypesFromReferences = context.CompilationProvider.SelectMany(
			static (compilation, ct) => GetDescriptionTypesFromReferences(compilation, ct));

		var allDescriptionTypes = descriptionTypesFromSource.Collect()
			.Combine(descriptionTypesFromReferences.Collect())
			.Select(static (pair, _) => pair.Left.AddRange(pair.Right));

		// Combine and generate
		var combined = addNewInvocations.Collect().Combine(allDescriptionTypes);

		context.RegisterSourceOutput(combined, GenerateCode);
	}

	private static bool IsAddNewInvocation(SyntaxNode node, CancellationToken ct)
	{
		// Looking for: factory.AddNew<T>("path")
		return node is InvocationExpressionSyntax
		{
			Expression: MemberAccessExpressionSyntax
			{
				Name: GenericNameSyntax { Identifier.Text: "AddNew" }
			}
		};
	}

	private static AddNewData? GetAddNewData(GeneratorSyntaxContext context, CancellationToken ct)
	{
		var invocation = (InvocationExpressionSyntax)context.Node;
		var memberAccess = (MemberAccessExpressionSyntax)invocation.Expression;
		var genericName = (GenericNameSyntax)memberAccess.Name;

		// Get the type argument T from AddNew<T>
		if (genericName.TypeArgumentList.Arguments.Count != 1)
			return null;

		var typeArg = genericName.TypeArgumentList.Arguments[0];
		var typeSymbol = context.SemanticModel.GetTypeInfo(typeArg, ct).Type as INamedTypeSymbol;
		if (typeSymbol == null)
			return null;

		// Get the path string argument (optional — paramless AddNew<T>() registers types only)
		string path;
		if (invocation.ArgumentList.Arguments.Count == 0)
		{
			path = string.Empty;
		}
		else if (invocation.ArgumentList.Arguments.Count == 1)
		{
			var pathArg = invocation.ArgumentList.Arguments[0].Expression;
			var pathValue = context.SemanticModel.GetConstantValue(pathArg, ct);
			if (!pathValue.HasValue || pathValue.Value is not string pathString)
				return null;

			path = pathString;
		}
		else
		{
			return null;
		}

		return new AddNewData(typeSymbol, path);
	}

	private static ImmutableArray<DescriptionTypeData> GetDescriptionTypesFromReferences(Compilation compilation, CancellationToken ct)
	{
		var descAttrSymbol = compilation.GetTypeByMetadataName("Modules.Framework.Core.DescriptionTypeAttribute");
		if (descAttrSymbol == null)
			return ImmutableArray<DescriptionTypeData>.Empty;

		var result = new List<DescriptionTypeData>();

		foreach (var reference in compilation.References)
		{
			ct.ThrowIfCancellationRequested();
			if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
				continue;

			WalkNamespace(assembly.GlobalNamespace, descAttrSymbol, result, ct);
		}

		return result.ToImmutableArray();
	}

	private static void WalkNamespace(INamespaceSymbol ns, INamedTypeSymbol descAttrSymbol, List<DescriptionTypeData> result, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();

		foreach (var type in ns.GetTypeMembers())
		{
			if (type.IsAbstract) continue;

			foreach (var attr in type.GetAttributes())
			{
				if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, descAttrSymbol))
					continue;

				var typePath = attr.ConstructorArguments.FirstOrDefault().Value as string ?? string.Empty;
				var isDefault = attr.ConstructorArguments.Length > 1 && attr.ConstructorArguments[1].Value is true;
				result.Add(new DescriptionTypeData(type, typePath, isDefault));
				break;
			}
		}

		foreach (var nested in ns.GetNamespaceMembers())
			WalkNamespace(nested, descAttrSymbol, result, ct);
	}

	private static DescriptionTypeData? GetDescriptionTypeData(GeneratorAttributeSyntaxContext context, CancellationToken ct)
	{
		if (context.TargetSymbol is not INamedTypeSymbol typeSymbol)
			return null;

		if (typeSymbol.IsAbstract)
			return null;

		// Get the type argument from DescriptionTypeAttribute
		var attribute = context.Attributes.FirstOrDefault();
		var typePath = attribute?.ConstructorArguments.FirstOrDefault().Value as string ?? string.Empty;
		var isDefault = attribute is not null && attribute.ConstructorArguments.Length > 1
		                                      && attribute.ConstructorArguments[1].Value is true;

		return new DescriptionTypeData(typeSymbol, typePath, isDefault);
	}

	private static void GenerateCode(
		SourceProductionContext context,
		(ImmutableArray<AddNewData> AddNewCalls, ImmutableArray<DescriptionTypeData> DescriptionTypes) data)
	{
		// Group description types by their implemented interfaces
		var typesByInterface = new Dictionary<string, List<DescriptionTypeData>>();

		foreach (var descType in data.DescriptionTypes)
		{
			foreach (var iface in descType.typeSymbol.AllInterfaces)
			{
				var ifaceName = iface.ToDisplayString();
				if (!typesByInterface.ContainsKey(ifaceName))
					typesByInterface[ifaceName] = new List<DescriptionTypeData>();
				typesByInterface[ifaceName].Add(descType);
			}
		}

		// Generate extension for each AddNew<T> call
		foreach (AddNewData addNew in data.AddNewCalls)
		{
			var interfaceName = addNew.interfaceType.ToDisplayString();
			var interfaceSimpleName = addNew.interfaceType.Name;

			if (!typesByInterface.TryGetValue(interfaceName, out var implementations))
				continue;

			// Полностью квалифицированное имя (с global::). Extension-класс кладётся в неймспейс
			// интерфейса; если он вида Modules.Framework.* — неквалифицированные "Framework.Core.*"
			// в теле резолвятся относительно (→ Modules.Framework.Core.*) и не компилируются. global:: лечит.
			var globalInterfaceName = addNew.interfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

			var code = new CodeWriter();
			
			// Generate the extension class
			code.AppendLine("// <auto-generated/>");
			code.AppendLine();
			code.AppendLine($"namespace {addNew.interfaceType.ContainingNamespace.ToDisplayString()}");
			code.BeginBlock();
			
			code.AppendLine($"public static class __{interfaceSimpleName}Extension");
			code.BeginBlock();
			
			code.AppendLine($"public static void RegisterAll(this global::Framework.Core.Factories.Registration<{globalInterfaceName}> registration)");
			code.BeginBlock();
			
			code.AppendLine("var factory = registration.factory;");
			code.AppendLine();

			// path (from AddNew<T>("path")) = JSON-section key коллекции → регистрируем её
			// DescriptionsCreator, чтобы секция читалась. Пусто для безаргументного AddNew<T>().
			if (!string.IsNullOrEmpty(addNew.path))
			{
				code.AppendLine($"factory.Add<global::Framework.Core.Collections.IDescriptionsCreator>(\"{addNew.path}\", typeof(global::Framework.Core.Collections.DescriptionsCreator<{globalInterfaceName}>));");
				code.AppendLine();
			}

			var seenKeys = new HashSet<string>();
			foreach (var impl in implementations)
			{
				var typePath = !string.IsNullOrEmpty(impl.typePath)
					? impl.typePath
					: GeneratorShared.ToSnakeCase(impl.typeSymbol.Name);

				// DESCGEN003: два [DescriptionType] претендуют на один ключ в рамках интерфейса —
				// factory.Add бросит исключение при регистрации на старте. Ловим на компиляции.
				if (!seenKeys.Add(typePath))
				{
					context.ReportDiagnostic(Diagnostic.Create(DuplicateKey,
						impl.typeSymbol.Locations.FirstOrDefault(),
						impl.typeSymbol.Name, typePath, interfaceSimpleName));
					continue;
				}

				var globalImplName = impl.typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
				code.AppendLine($"factory.Add<{globalInterfaceName}>(\"{typePath}\", typeof({globalImplName}));");

				// isDefault → дефолтная регистрация для этого интерфейса (factory.SetDefault идемпотентен).
				if (impl.isDefault)
					code.AppendLine($"factory.SetDefault<{globalInterfaceName}>(typeof({globalImplName}));");
			}

			code.EndBlock(); // RegisterAll
			code.EndBlock(); // class
			code.EndBlock(); // namespace

			context.AddSource($"__{interfaceSimpleName}Extension.g.cs", code.GetSourceText());
		}
	}

	private static readonly DiagnosticDescriptor DuplicateKey = new(
		id: "DESCGEN003",
		title: "Duplicate description key",
		messageFormat: "Description type '{0}' uses key '{1}' for interface '{2}', which is already registered by another [DescriptionType]. Keys must be unique per interface, otherwise registration throws at startup.",
		category: "DescriptionGenerators",
		defaultSeverity: DiagnosticSeverity.Error,
		isEnabledByDefault: true);

	private readonly struct AddNewData(INamedTypeSymbol interfaceType, string path)
	{
		public readonly INamedTypeSymbol interfaceType = interfaceType;
		public readonly string path = path;
	}

	private readonly struct DescriptionTypeData(INamedTypeSymbol typeSymbol, string typePath, bool isDefault)
	{
		public readonly INamedTypeSymbol typeSymbol = typeSymbol;
		public readonly string typePath = typePath;
		public readonly bool isDefault = isDefault;
	}
}
