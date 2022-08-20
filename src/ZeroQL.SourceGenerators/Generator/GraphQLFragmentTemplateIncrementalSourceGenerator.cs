using System;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ZeroQL.SourceGenerators.Resolver;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace ZeroQL.SourceGenerators.Generator;

[Generator]
public class GraphQLFragmentTemplateIncrementalSourceGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var fragments = context.SyntaxProvider.CreateSyntaxProvider(
            FindFragmentThatRequiresSourceGeneration,
            (c, _) =>
            {
                var attribute = (AttributeSyntax)c.Node;
                var method = (MethodDeclarationSyntax)attribute.Parent!.Parent!;

                return (Method: method, c.SemanticModel);
            });

        context.RegisterImplementationSourceOutput(fragments, AddMetadataForFragment);
    }

    private void AddMetadataForFragment(
        SourceProductionContext context,
        (MethodDeclarationSyntax Method, SemanticModel SemanticModel) input)
    {
        var (methodDeclaration, semanticModel) = input;
        if (methodDeclaration.Parent is not ClassDeclarationSyntax classDeclaration)
        {
            return;
        }

        var namespaceDeclaration = classDeclaration
            .AncestorsAndSelf()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .FirstOrDefault();

        var compilationUnit = classDeclaration
            .AncestorsAndSelf()
            .OfType<CompilationUnitSyntax>()
            .First();

        var (graphQLTemplate, error) = GraphQLQueryResolver.ResolveFragmentTemplate(semanticModel, methodDeclaration, context.CancellationToken).Unwrap();
        if (error)
        {
            if (error is ErrorWithData<DiagnosticDescriptor> diagnosticError)
            {
                context.ReportDiagnostic(Diagnostic.Create(diagnosticError.Data, methodDeclaration.GetLocation()));
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Descriptors.FailedToConvert, methodDeclaration.GetLocation()));
            return;
        }

        var usings = compilationUnit.Usings
            .Concat(namespaceDeclaration?.Usings ?? Enumerable.Empty<UsingDirectiveSyntax>())
            .ToArray();

        var literal = LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(graphQLTemplate));
        var newMethod = MethodDeclaration(methodDeclaration.ReturnType, methodDeclaration.Identifier)
            .WithParameterList(methodDeclaration.ParameterList)
            .AddAttributeLists(AttributeList()
                .AddAttributes(Attribute(ParseName(SourceGeneratorInfo.GraphQLQueryTemplateAttribute))
                    .WithArgumentList(ParseAttributeArgumentList($"({literal})"))))
            .WithModifiers(methodDeclaration.Modifiers)
            .WithBody(null)
            .WithSemicolonToken(ParseToken(";"));

        var newClassDeclaration = classDeclaration
            .WithMembers(List<MemberDeclarationSyntax>()
                .Add(newMethod));

        MemberDeclarationSyntax members = namespaceDeclaration is null
            ? newClassDeclaration
            : NamespaceDeclaration(namespaceDeclaration.Name)
                .WithMembers(List<MemberDeclarationSyntax>()
                    .Add(newClassDeclaration));

        var syntaxTree = CompilationUnit()
            .WithUsings(List(usings))
            .WithMembers(List<MemberDeclarationSyntax>()
                .Add(members));

        var source = syntaxTree
            .NormalizeWhitespace()
            .ToString();

        var uniqueId = Guid.NewGuid().ToString("N");
        context.AddSource($"{classDeclaration.Identifier.Text}.{uniqueId}.g.cs", source);
    }

    private bool FindFragmentThatRequiresSourceGeneration(SyntaxNode node, CancellationToken cancellationToken)
    {
        if (node is not AttributeSyntax attribute)
        {
            return false;
        }

        if (attribute.Name is not IdentifierNameSyntax name)
        {
            return false;
        }

        if (attribute.Parent is not AttributeListSyntax attributeList)
        {
            return false;
        }

        if (attributeList.Parent is not MethodDeclarationSyntax method)
        {
            return false;
        }

        if (method.Modifiers.All(m => !m.IsKind(SyntaxKind.PartialKeyword)))
        {
            return false;
        }

        return name.Identifier.Text.EndsWith(SourceGeneratorInfo.GraphQLFragmentAttributeTypeName);
    }
}