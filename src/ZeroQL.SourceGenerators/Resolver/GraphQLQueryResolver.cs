using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ZeroQL.SourceGenerators.Resolver;

public static class GraphQLQueryResolver
{
    public const string Cancelled = nameof(Cancelled);
    public static readonly Error CancelledError = new(Cancelled);

    public static Result<GraphQLQueryResolverResult> Resolve(SemanticModel semanticModel,
        ExpressionSyntax query,
        CancellationToken cancellationToken)
    {
        if (query is not LambdaExpressionSyntax lambda)
        {
            return Failed(query);
        }

        var inputs = GetQueryInputs(lambda);
        var variables = GetVariablesFromLambda(semanticModel, lambda, cancellationToken);
        var predefinedVariables = inputs.VariablesName is null
            ? new Dictionary<string, GraphQLQueryVariable>()
            : variables.ToDictionary(o => $"{inputs.VariablesName}.{o.Name}");

        var context = new GraphQLResolveContext(
            inputs.QueryName,
            lambda,
            predefinedVariables,
            new Dictionary<string, GraphQLQueryVariable>(),
            semanticModel,
            cancellationToken);

        var (body, error) = ResolveQuery(context, lambda.Body).Unwrap();
        if (error)
        {
            return error;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return CancelledError;
        }

        var stringBuilder = new StringBuilder();
        if (context.Variables.Any())
        {
            var variablesBody = context.Variables
                .Select(o => $"{o.GraphQLValue}: {o.GraphQLType}")
                .Join()
                .Wrap("(", ") ");

            stringBuilder.Append(variablesBody);
        }

        stringBuilder.Append("{ ");
        stringBuilder.Append(body);
        stringBuilder.Append("}");

        return new GraphQLQueryResolverResult
        {
            Query = stringBuilder.ToString(),
            Variables = context.Variables.ToDictionary(o => o.Name)
        };
    }

    public static Result<GraphQLQueryResolverResult> Resolve(SemanticModel semanticModel,
        MethodDeclarationSyntax method,
        CancellationToken cancellationToken)
    {
        var inputs = GetQueryInputs(method);
        var variables = GetVariablesFromRequestMethod(semanticModel, method, cancellationToken);
        var availableVariables = variables
            .ToDictionary(o => o.Name);

        var context = new GraphQLResolveContext(
            inputs.QueryName,
            method,
            availableVariables,
            new Dictionary<string, GraphQLQueryVariable>(),
            semanticModel,
            cancellationToken);

        var (body, error) = ResolveQuery(context, method).Unwrap();
        if (error)
        {
            return error;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return CancelledError;
        }

        var stringBuilder = new StringBuilder();
        if (variables.Any())
        {
            var variablesBody = variables
                .Select(o => $"{o.GraphQLValue}: {o.GraphQLType}")
                .Join()
                .Wrap("(", ") ");

            stringBuilder.Append(variablesBody);
        }

        stringBuilder.Append("{ ");
        stringBuilder.Append(body);
        stringBuilder.Append("}");

        return new GraphQLQueryResolverResult
        {
            Query = stringBuilder.ToString(),
            Variables = availableVariables
        };
    }

    public static Result<string> ResolveFragmentTemplate(
        SemanticModel semanticModel,
        MethodDeclarationSyntax methodDeclaration,
        CancellationToken cancellationToken)
    {
        var parameters = methodDeclaration.ParameterList.Parameters;
        var queryName = parameters.First().Identifier.Text;
        var variables = parameters.Skip(1)
            .Select(o => GraphQLQueryVariable.Variable(
                o.Identifier.ValueText,
                semanticModel.GetTypeInfo(o.Type!).Type!))
            .ToArray();

        var availableVariables = variables.Empty()
            ? new Dictionary<string, GraphQLQueryVariable>()
            : variables.ToDictionary(o => o.Name);

        var context = new GraphQLResolveContext(
            queryName,
            methodDeclaration,
            availableVariables,
            new Dictionary<string, GraphQLQueryVariable>(),
            semanticModel,
            cancellationToken);

        var (body, error) = HandleMethod(context, methodDeclaration, methodDeclaration).Unwrap();
        if (error)
        {
            return error;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return string.Empty;
        }

        return body;
    }

    private static (string? VariablesName, string QueryName) GetQueryInputs(CSharpSyntaxNode lambda)
    {
        if (lambda is SimpleLambdaExpressionSyntax simpleLambda)
        {
            return (null, simpleLambda.Parameter.Identifier.ValueText);
        }

        if (lambda is ParenthesizedLambdaExpressionSyntax parenthesizedLambda)
        {
            var parameters = parenthesizedLambda.ParameterList.Parameters;
            return (parameters.First().Identifier.ValueText, parameters.Last().Identifier.ValueText);
        }

        if (lambda is MethodDeclarationSyntax methodDeclaration)
        {
            var parameters = methodDeclaration.ParameterList.Parameters;
            return (null, parameters.First().Identifier.ValueText);
        }

        return default;
    }

    private static GraphQLQueryVariable[] GetVariablesFromLambda(SemanticModel semanticModel,
        LambdaExpressionSyntax lambda,
        CancellationToken cancellationToken)
    {
        var symbol = semanticModel.GetSymbolInfo(lambda, cancellationToken);
        if (symbol.Symbol is not IMethodSymbol method)
        {
            Failed(lambda);
            return Array.Empty<GraphQLQueryVariable>();
        }

        if (method.Parameters.Length == 1)
        {
            return Array.Empty<GraphQLQueryVariable>();
        }

        var type = method.Parameters.First().Type;

        return type.GetMembers()
            .OfType<IPropertySymbol>()
            .Select(o => GraphQLQueryVariable.Variable(o.Name, o.Type, verifyNullability: true))
            .ToArray();
    }

    private static GraphQLQueryVariable[] GetVariablesFromRequestMethod(SemanticModel semanticModel,
        MethodDeclarationSyntax methodDeclaration,
        CancellationToken cancellationToken)
    {
        var method = semanticModel.GetDeclaredSymbol(methodDeclaration, cancellationToken);
        if (method!.ContainingSymbol is not INamedTypeSymbol containingType)
        {
            return Array.Empty<GraphQLQueryVariable>();
        }

        return containingType.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(o => !o.IsImplicitlyDeclared)
            .Select(o => GraphQLQueryVariable.Variable(o.Name, o.Type))
            .ToArray();
    }

    private static Result<string> ResolveQuery(GraphQLResolveContext context, CSharpSyntaxNode node)
    {
        if (context.CancellationToken.IsCancellationRequested)
        {
            return new Error(Cancelled);
        }

        switch (node)
        {
            case InvocationExpressionSyntax invocation:
            {
                return HandleInvocation(context, invocation);
            }
            case MemberAccessExpressionSyntax member:
            {
                return HandleMemberAccess(context, member);
            }
            case IdentifierNameSyntax identifierNameSyntax:
            {
                return HanleIndentifier(context, node, identifierNameSyntax);
            }
            case SimpleLambdaExpressionSyntax simpleLambda:
            {
                return HandlerSimpleLambda(context, simpleLambda);
            }
            case ArgumentSyntax argument when context.Parent is BaseObjectCreationExpressionSyntax:
            {
                return HandleArgumentAsObjectCreation(context, argument);
            }
            case ArgumentSyntax argument:
            {
                return HandleArgumentAsVariable(context, argument);
            }
            case AnonymousObjectCreationExpressionSyntax anonymous:
            {
                return HandleAnonymousObjectCreation(context, anonymous);
            }
            case AnonymousObjectMemberDeclaratorSyntax anonymousMember:
            {
                var query = ResolveQuery(context.WithParent(anonymousMember), anonymousMember.Expression);
                return query;
            }
            case ObjectCreationExpressionSyntax objectCreation:
            {
                return HandleObjectCreation(context, objectCreation);
            }
            case ImplicitObjectCreationExpressionSyntax implicitObjectCreation:
            {
                return HandleObjectCreation(context, implicitObjectCreation);
            }
            case MethodDeclarationSyntax methodDeclaration:
            {
                return HandleMethod(context, methodDeclaration, methodDeclaration);
            }
            case PostfixUnaryExpressionSyntax postfixUnary:
            {
                return ResolveQuery(context.WithParent(postfixUnary), postfixUnary.Operand);
            }
            case BlockSyntax blockSyntax:
            {
                if (blockSyntax.Statements.First() is ReturnStatementSyntax { Expression: { } } returnStatement)
                {
                    return ResolveQuery(context.WithParent(returnStatement), returnStatement.Expression);
                }

                return Failed(blockSyntax.Statements.First());
            }
            case ArrowExpressionClauseSyntax arrowExpression:
            {
                return ResolveQuery(context.WithParent(arrowExpression), arrowExpression.Expression);
            }
            case CastExpressionSyntax cast:
            {
                return HandleEnumCast(context, cast);
            }
        }

        return Failed(node);
    }

    private static Result<string> HandleObjectCreation(GraphQLResolveContext context,
        BaseObjectCreationExpressionSyntax objectCreation)
    {
        var arguments = objectCreation.ArgumentList?.Arguments.ToArray();
        var initializers = objectCreation.Initializer?.Expressions
            .OfType<AssignmentExpressionSyntax>()
            .Select(o => o.Right)
            .ToArray();

        if (arguments is null && initializers is null)
        {
            return string.Empty;
        }

        var expressions = new List<CSharpSyntaxNode>();
        if (arguments is not null)
        {
            expressions.AddRange(arguments);
        }

        if (initializers is not null)
        {
            expressions.AddRange(initializers);
        }

        var results = expressions
            .Select(o => ResolveQuery(context.WithParent(objectCreation), o))
            .ToArray();

        if (results.Any(o => o.Error))
        {
            return results.FirstOrDefault(o => o.Error)?.Error!;
        }

        return results.Select(o => o.Value!).Join(" ");
    }

    private static Result<string> HandleArgumentAsObjectCreation(GraphQLResolveContext context, ArgumentSyntax argument)
    {
        return ResolveQuery(context.WithParent(argument), argument.Expression);
    }

    private static Result<string> HandleAnonymousObjectCreation(GraphQLResolveContext context,
        AnonymousObjectCreationExpressionSyntax anonymous)
    {
        var initializers = new List<string>(anonymous.Initializers.Count);
        foreach (var initializer in anonymous.Initializers)
        {
            var (result, error) = ResolveQuery(context, initializer).Unwrap();
            if (error)
            {
                return error;
            }

            initializers.Add(result);
        }

        return initializers.Join(" ");
    }

    private static Result<string> HandleArgumentAsVariable(GraphQLResolveContext context, ArgumentSyntax argument)
    {
        if (argument.Expression is LiteralExpressionSyntax literal)
        {
            return literal.ToString();
        }

        var value = argument.Expression.ToString();
        if (context.PredefinedVariables.TryGetValue(value, out var variable))
        {
            return variable.GraphQLValue;
        }

        var possibleMethodSymbol =
            context.SemanticModel.GetSymbolInfo(argument.Parent!.Parent!, context.CancellationToken);
        if (possibleMethodSymbol.Symbol is not IMethodSymbol methodSymbol)
        {
            return Failed(argument);
        }

        var parameter = GetParameterSymbol(methodSymbol, argument);
        if (parameter is null)
        {
            return Failed(argument);
        }

        switch (argument.Expression)
        {
            case MemberAccessExpressionSyntax memberAccess:
            {
                var symbol = context.SemanticModel.GetSymbolInfo(memberAccess.Name, context.CancellationToken);
                var namedType = symbol.GetTypeSymbol();
                if (namedType is null)
                {
                    return Failed(memberAccess);
                }

                if (namedType.EnumUnderlyingType != null && symbol.Symbol is IFieldSymbol fieldSymbol)
                {
                    var fieldSelector = fieldSymbol
                        .GetAttributes()
                        .FirstOrDefault(o =>
                            SymbolEqualityComparer.Default.Equals(o.AttributeClass, context.GraphQLNameAttribute));

                    var fieldName = memberAccess.Name.Identifier.Text.ToUpperCase();
                    if (fieldSelector is null)
                    {
                        return fieldName;
                    }

                    var name = fieldSelector.ConstructorArguments.FirstOrDefault();
                    if (name.Value is null)
                    {
                        return fieldName;
                    }

                    return name.Value.ToString();
                }

                break;
            }
            case IdentifierNameSyntax identifierName:
            {
                var symbol = context.SemanticModel.GetSymbolInfo(identifierName, context.CancellationToken);
                switch (symbol.Symbol)
                {
                    case IPropertySymbol:
                    case IFieldSymbol:
                    {
                        return Failed(identifierName, Descriptors.GraphQLVariableShouldBeLocal);
                    }

                    case ILocalSymbol localSymbol:
                    {
                        var graphQLType = parameter.ToGraphQLType();
                        var graphQLVariable =
                            GraphQLQueryVariable.Variable(localSymbol.Name, localSymbol.Type, graphQLType);
                        context.AddVariable(graphQLVariable);

                        return graphQLVariable.GraphQLValue;
                    }

                    case IParameterSymbol parameterSymbol:
                    {
                        var graphQLType = parameter.ToGraphQLType();
                        var graphQLVariable =
                            GraphQLQueryVariable.Variable(parameterSymbol.Name, parameterSymbol.Type, graphQLType);
                        context.AddVariable(graphQLVariable);

                        return graphQLVariable.GraphQLValue;
                    }

                    default:
                        return Failed(argument, Descriptors.GraphQLVariableExpected);
                }
            }
            case SimpleLambdaExpressionSyntax:
                return string.Empty;
        }

        return Failed(argument, Descriptors.GraphQLVariableExpected);
    }

    private static Result<string> HandlerSimpleLambda(GraphQLResolveContext context,
        SimpleLambdaExpressionSyntax simpleLambda)
    {
        if (QueryAnalyzerHelper.IsOpenLambda(simpleLambda))
        {
            return Failed(simpleLambda, Descriptors.OpenLambdaIsNotAllowed);
        }

        var parameter = simpleLambda.Parameter.Identifier.ValueText;
        var childContext = context with { QueryVariableName = parameter };

        return ResolveQuery(childContext.WithParent(simpleLambda), simpleLambda.Body);
    }

    private static IParameterSymbol? GetParameterSymbol(IMethodSymbol methodSymbol, ArgumentSyntax argumentSyntax)
    {
        var parameter =
            methodSymbol.Parameters.FirstOrDefault(o => o.Name == argumentSyntax.NameColon?.Name.Identifier.Text);
        if (parameter is not null)
        {
            return parameter;
        }

        if (argumentSyntax.Parent is not ArgumentListSyntax argumentList)
        {
            return null;
        }

        var argumentIndex = argumentList.Arguments.IndexOf(argumentSyntax);
        if (argumentIndex < 0 || argumentIndex >= methodSymbol.Parameters.Length)
        {
            return null;
        }

        return methodSymbol.Parameters[argumentIndex];
    }

    private static Result<string> HanleIndentifier(
        GraphQLResolveContext context,
        CSharpSyntaxNode node,
        IdentifierNameSyntax identifierNameSyntax)
    {
        if (identifierNameSyntax.Identifier.ValueText == context.QueryVariableName)
        {
            return string.Empty;
        }

        var possibleSymbol = context.SemanticModel.GetSymbolInfo(node);
        if (possibleSymbol.Symbol is IMethodSymbol method)
        {
            var syntaxReference = method.DeclaringSyntaxReferences.FirstOrDefault();
            if (syntaxReference is null)
            {
                return Failed(node);
            }

            var possibleMethodDeclaration = syntaxReference.GetSyntax(context.CancellationToken);
            if (possibleMethodDeclaration is LocalFunctionStatementSyntax
                {
                    ParameterList.Parameters.Count: 1
                } localFunction)
            {
                var identifierValueText = localFunction.ParameterList.Parameters.First().Identifier.ValueText;
                var newContext = context with
                {
                    QueryVariableName = identifierValueText,
                    Parent = localFunction
                };

                if (localFunction.ExpressionBody is not null)
                {
                    return ResolveQuery(newContext, localFunction.ExpressionBody);
                }

                if (localFunction.Body is not null)
                {
                    return ResolveQuery(newContext, localFunction.Body);
                }
            }

            if (possibleMethodDeclaration is MethodDeclarationSyntax
                {
                    ParameterList.Parameters.Count: 1
                } methodDeclaration)
            {
                var identifierValueText = methodDeclaration.ParameterList.Parameters.First().Identifier.ValueText;
                var newContext = context with
                {
                    QueryVariableName = identifierValueText,
                    Parent = methodDeclaration
                };

                if (methodDeclaration.ExpressionBody is not null)
                {
                    return ResolveQuery(newContext, methodDeclaration.ExpressionBody);
                }

                if (methodDeclaration.Body is not null)
                {
                    return ResolveQuery(newContext, methodDeclaration.Body);
                }
            }
        }

        return Failed(node);
    }

    private static Result<string> HandleMemberAccess(GraphQLResolveContext context,
        MemberAccessExpressionSyntax member)
    {
        if (member.Expression is MemberAccessExpressionSyntax left)
        {
            return ResolveQuery(context.WithParent(member), left);
        }

        if (member.Expression is IdentifierNameSyntax identifier &&
            identifier.Identifier.ValueText == context.QueryVariableName)
        {
            return ExtractSelectorName(context, member.Name);
        }

        if (member.Expression is PostfixUnaryExpressionSyntax { Operand: IdentifierNameSyntax postfixIdentifier } &&
            postfixIdentifier.Identifier.ValueText == context.QueryVariableName)
        {
            return ExtractSelectorName(context, member.Name);
        }

        return Failed(member.Expression);
    }

    private static Result<string> ExtractSelectorName(GraphQLResolveContext context, CSharpSyntaxNode name)
    {
        var nameSymbol = context.SemanticModel.GetSymbolInfo(name);
        if (nameSymbol.Symbol is null)
        {
            return Failed(name, Descriptors.OnlyFieldSelectorsAndFragmentsAreAllowed);
        }

        var attribute = nameSymbol.Symbol
            .GetAttributes()
            .FirstOrDefault(o =>
                SymbolEqualityComparer.Default.Equals(o.AttributeClass, context.GraphQLNameAttribute));

        if (attribute is null)
        {
            return Failed(name);
        }

        return attribute.ConstructorArguments.First().Value!.ToString();
    }

    private static Result<string> HandleInvocation(GraphQLResolveContext context,
        InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax
            {
                Expression: IdentifierNameSyntax identifierName
            } &&
            identifierName.Identifier.ValueText != context.QueryVariableName)
        {
            return Failed(identifierName, Descriptors.DontUseOutScopeValues);
        }

        var symbol = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken);
        if (symbol.Symbol is not IMethodSymbol method)
        {
            return Failed(invocation);
        }

        if (context.CancellationToken.IsCancellationRequested)
        {
            return new Error(Cancelled);
        }

        var attributes = method
            .GetAttributes();

        var hasSyntax = attributes
            .Any(o => SymbolEqualityComparer.Default.Equals(o.AttributeClass, context.SyntaxAttribute));
        if (hasSyntax)
        {
            return HandleSyntaxNode(context, invocation, method);
        }

        var hasFragmentTemplate = attributes
            .Any(o => SymbolEqualityComparer.Default.Equals(o.AttributeClass, context.TemplateAttribute));
        if (hasFragmentTemplate)
        {
            return HandleFragmentWithoutSyntaxTree(context, invocation, method);
        }

        var hasFragment = attributes
            .Any(o => SymbolEqualityComparer.Default.Equals(o.AttributeClass, context.FragmentAttribute));
        if (hasFragment)
        {
            return HandleFragment(context, invocation, method);
        }

        var hasFieldSelector = attributes
            .Any(o => SymbolEqualityComparer.Default.Equals(o.AttributeClass, context.GraphQLNameAttribute));
        if (hasFieldSelector)
        {
            return HandleFieldSelector(context, invocation, method);
        }

        return Failed(invocation, Descriptors.OnlyFieldSelectorsAndFragmentsAreAllowed);
    }

    private static Result<string> HandleSyntaxNode(
        GraphQLResolveContext context,
        InvocationExpressionSyntax invocation,
        IMethodSymbol method)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax
            {
                Expression: InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax { Name: { Identifier: { Text: "On" } } }
                }
            })
        {
            return HandleOn(context, invocation, method);
        }

        return Failed(invocation);
    }

    private static Result<string> HandleOn(
        GraphQLResolveContext context,
        InvocationExpressionSyntax invocation,
        IMethodSymbol method)
    {
        var (selector, error) =
            ResolveQuery(context.WithParent(invocation), invocation.ArgumentList.Arguments[0].Expression)
                .Unwrap();
        if (error)
        {
            return error;
        }

        return $"... on {method.TypeArguments[0].Name} {{ {selector} }}";
    }

    private static Result<string> HandleFragment(
        GraphQLResolveContext context,
        InvocationExpressionSyntax invocation,
        IMethodSymbol method)
    {
        if (method.DeclaringSyntaxReferences.IsEmpty)
        {
            return HandleFragmentWithoutSyntaxTree(context, invocation, method);
        }

        var fragment = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        if (fragment is not MethodDeclarationSyntax methodDeclaration)
        {
            return Failed(invocation);
        }

        var currentCompilation = context.SemanticModel.Compilation;
        var compilation = GetCompilation(fragment.SyntaxTree, currentCompilation);
        if (compilation.Error || currentCompilation != compilation.Value)
        {
            return Failed(invocation, Descriptors.FragmentsWithoutSyntaxTree);
        }


        var parameters = methodDeclaration.ParameterList.Parameters;
        var name = parameters.FirstOrDefault()?.Identifier.Text;
        if (name is null)
        {
            return Failed(invocation);
        }

        var variablesToMap = parameters
            .Skip(1)
            .Select(o => o.Identifier.Text)
            .ToArray();

        var inputVariables = invocation.ArgumentList.Arguments;

        var variables = inputVariables
            .Select((o, i) => (Key: variablesToMap[i], Value: HandleArgumentAsVariable(context, o)))
            .ToArray();

        for (int i = 0; i < variables.Length; i++)
        {
            var variable = variables[i];
            if (variable.Value.Error)
            {
                return Failed(invocation.ArgumentList.Arguments[i]);
            }
        }

        if (context.CancellationToken.IsCancellationRequested)
        {
            return new Error(Cancelled);
        }

        var newSemanticModel = compilation.Value.GetSemanticModel(fragment.SyntaxTree);
        context = context.WithVariableName(name) with
        {
            SemanticModel = newSemanticModel,
            PredefinedVariables = variables.ToDictionary(o => o.Key, o => GraphQLQueryVariable.Constant(o.Value.Value!))
        };

        return HandleMethod(context, invocation, methodDeclaration);
    }

    private static Result<string> HandleMethod(
        GraphQLResolveContext context,
        CSharpSyntaxNode node,
        MethodDeclarationSyntax methodDeclaration)
    {
        if (methodDeclaration.Body is not null)
        {
            return ResolveQuery(context.WithParent(methodDeclaration), methodDeclaration.Body);
        }

        if (context.CancellationToken.IsCancellationRequested)
        {
            return new Error(Cancelled);
        }

        if (methodDeclaration.ExpressionBody is not null)
        {
            return ResolveQuery(context.WithParent(methodDeclaration), methodDeclaration.ExpressionBody);
        }

        return Failed(node);
    }

    private static Result<string> HandleFragmentWithoutSyntaxTree(GraphQLResolveContext context,
        InvocationExpressionSyntax invocation,
        IMethodSymbol method)
    {
        var graphQLQueryTemplate = method
            .GetAttributes()
            .FirstOrDefault(o => SymbolEqualityComparer.Default.Equals(o.AttributeClass, context.TemplateAttribute))
            ?
            .ConstructorArguments
            .FirstOrDefault()
            .Value?
            .ToString();

        if (graphQLQueryTemplate is null)
        {
            return Failed(invocation);
        }

        var parameters = method.Parameters;
        var variablesToMap = parameters
            .Select(o => o.Name)
            .ToArray();

        var inputVariables = invocation.ArgumentList.Arguments;
        var fragmentVariablesToInputVariables = inputVariables
            .Select((o, i) => (Key: variablesToMap[i], Value: HandleArgumentAsVariable(context, o)))
            .ToArray();

        for (int i = 0; i < fragmentVariablesToInputVariables.Length; i++)
        {
            var variable = fragmentVariablesToInputVariables[i];
            if (variable.Value.Error)
            {
                return Failed(invocation.ArgumentList.Arguments[i]);
            }
        }

        if (context.CancellationToken.IsCancellationRequested)
        {
            return new Error(Cancelled);
        }

        var finalGraphQLQuery = fragmentVariablesToInputVariables
            .Aggregate(
                graphQLQueryTemplate,
                (current, variable) => current.Replace($"{{{{{variable.Key}}}}}", variable.Value.Value));

        return finalGraphQLQuery;
    }

    private static Result<string> HandleFieldSelector(
        GraphQLResolveContext context,
        InvocationExpressionSyntax invocation,
        IMethodSymbol method)
    {
        var ignoreLastParameter = method.Parameters.Last().Type.Name.StartsWith("Func");
        var parametersToIgnore = ignoreLastParameter ? 1 : 0;
        var argumentNames = method.Parameters
            .Take(method.Parameters.Length - parametersToIgnore)
            .Select(o => o.Name)
            .ToArray();

        var (selectorName, error) = ExtractSelectorName(context, invocation.Expression).Unwrap();
        if (error)
        {
            return error;
        }

        var stringBuilder = new StringBuilder();
        stringBuilder.Append(selectorName);
        if (argumentNames.Any())
        {
            var graphQLArguments = invocation.ArgumentList.Arguments
                .Take(argumentNames.Length)
                .Select(o =>
                {
                    var argumentName = o.NameColon?.Name.Identifier.Text;
                    var argumentValue = ResolveQuery(context.WithParent(o), o);
                    return (Name: argumentName, Value: argumentValue);
                })
                .ToArray();

            if (graphQLArguments.Any(o => o.Value.Error))
            {
                var firstError = graphQLArguments.First(o => o.Value.Error);
                return firstError.Value;
            }

            var formattedArguments = graphQLArguments
                .Where(o => !string.IsNullOrEmpty(o.Value.Value))
                .Select((o, i) => $"{o.Name ?? argumentNames[i]}: {o.Value.Value}")
                .ToArray();

            var formattedArgumentsString = formattedArguments.Any()
                ? formattedArguments
                    .Join()
                    .Wrap("(", ")")
                : string.Empty;

            stringBuilder.Append(formattedArgumentsString);
        }

        if (ignoreLastParameter)
        {
            var body = ResolveQuery(context.WithParent(invocation),
                invocation.ArgumentList.Arguments.Last().Expression);
            if (body.Error)
            {
                return body;
            }

            if (method.Parameters.Last().Type is not INamedTypeSymbol namedTypeSymbol)
            {
                return Failed(invocation);
            }

            var isUnionType = IsUnionType(context, namedTypeSymbol);

            var typeName = isUnionType ? " __typename" : string.Empty;
            stringBuilder.Append($" {{ {body.Value}{typeName} }} ");
        }

        return stringBuilder.ToString();
    }

    private static Result<string> HandleEnumCast(GraphQLResolveContext context, CastExpressionSyntax cast)
    {
        var castedTo = context.SemanticModel.GetTypeInfo(cast.Type);
        if (castedTo.Type is null)
        {
            return Failed(cast);
        }

        var type = castedTo.Type;
        if (type.TypeKind != TypeKind.Enum)
        {
            return Failed(cast);
        }
                
        return ResolveQuery(context.WithParent(cast), cast.Expression);
    }

    private static bool IsUnionType(GraphQLResolveContext context, INamedTypeSymbol namedTypeSymbol)
    {
        var selectorType = namedTypeSymbol.TypeArguments.FirstOrDefault();
        var isUnionType = selectorType?.AllInterfaces
            .Any(o => SymbolEqualityComparer.Default.Equals(o, context.UnionType));

        return isUnionType ?? false;
    }

    private static Result<Compilation> GetCompilation(SyntaxTree syntaxTree, Compilation compilation)
    {
        if (!compilation.ContainsSyntaxTree(syntaxTree))
        {
            var newCompilation = compilation.References
                .OfType<CompilationReference>()
                .FirstOrDefault(o => o.Compilation.ContainsSyntaxTree(syntaxTree))
                ?.Compilation;

            if (newCompilation is null)
            {
                return new Error("Failed to find compilation");
            }

            return newCompilation;
        }

        return compilation;
    }

    private static Error Failed(CSharpSyntaxNode node, DiagnosticDescriptor? descriptor = null)
    {
        var diagnostic = Diagnostic.Create(
            descriptor ?? Descriptors.FailedToConvertPartOfTheQuery,
            node.GetLocation(),
            node.ToString());

        return new ErrorWithData<Diagnostic>("Failed", diagnostic);
    }
}