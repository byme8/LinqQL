using Microsoft.CodeAnalysis;

namespace ZeroQL.SourceGenerators
{
    public class Descriptors
    {
        public static DiagnosticDescriptor FailedToConvert = new DiagnosticDescriptor(
            nameof(FailedToConvert),
            "Failed to convert to graphql query",
            "Failed to convert to graphql query: {0}",
            "ZeroQL",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);
        
        public static DiagnosticDescriptor DontUserOutScopeValues = new DiagnosticDescriptor(
            nameof(DontUserOutScopeValues),
            "Don't use out scope values",
            "Variable {0} is out of scope and can't be used in the child query",
            "ZeroQL",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);
        
        public static DiagnosticDescriptor OpenLambdaIsNotAllowed = new DiagnosticDescriptor(
            nameof(OpenLambdaIsNotAllowed),
            "Open lambda are not allowed",
            "Open lambda like 'o => o' are not allowed. Use a lambda like 'o => new { o.Id }'.",
            "ZeroQL",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);
        
        public static DiagnosticDescriptor OnlyStaticLambda = new DiagnosticDescriptor(
            nameof(OnlyStaticLambda),
            "Only static lambda are allowed",
            "Only static lambda are allowed",
            "ZeroQL",
            DiagnosticSeverity.Error,
            description: "Only static lambda are allowed to avoid variables from different scopes.",
            isEnabledByDefault: true);
    }
}