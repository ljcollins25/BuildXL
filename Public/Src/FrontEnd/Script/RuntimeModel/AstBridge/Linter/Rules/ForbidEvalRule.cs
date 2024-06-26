// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using TypeScript.Net.Types;

namespace BuildXL.FrontEnd.Script.RuntimeModel.AstBridge.Rules
{
    /// <summary>
    /// Checks that calls to 'eval' are not allowed.
    /// </summary>
    internal sealed class ForbidEvalRule : LanguageRule
    {
        private ForbidEvalRule()
        { }

        /// <inheritdoc />
        public override RuleAnalysisScope AnalysisScope => RuleAnalysisScope.All;

        public static ForbidEvalRule CreateAndRegister(AnalysisContext context)
        {
            var result = new ForbidEvalRule();
            result.Initialize(context);
            return result;
        }

        public override void Initialize(AnalysisContext context)
        {
            context.RegisterSyntaxNodeAction(
                this,
                CheckCallToEvalIsNotAllowed,
                TypeScript.Net.Types.SyntaxKind.CallExpression);
        }

        private static void CheckCallToEvalIsNotAllowed(INode node, DiagnosticContext context)
        {
            var expression = node.Cast<ICallExpression>();
            if (expression.Expression.Kind == TypeScript.Net.Types.SyntaxKind.Identifier)
            {
                var expressionName = expression.Expression.Cast<IIdentifier>();
                if (expressionName.Text == "eval")
                {
                    context.Logger.ReportEvalIsNotAllowed(
                        context.LoggingContext,
                        expression.LocationForLogging(context.SourceFile));
                }
            }
        }
    }
}
