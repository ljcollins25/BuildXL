// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using BuildXL.FrontEnd.Script.Evaluator;
using BuildXL.FrontEnd.Script.Expressions;
using BuildXL.FrontEnd.Script.Values;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Configuration;
using static BuildXL.Utilities.Core.FormattableStringEx;
using LineInfo = TypeScript.Net.Utilities.LineInfo;

namespace BuildXL.FrontEnd.Script.Statements
{
    /// <summary>
    /// For-statement.
    /// </summary>
    public class ForStatement : Statement
    {
        /// <summary>
        /// Initializer.
        /// </summary>
        /// <remarks>
        /// An initializer can either be an assignment statement or a variable statement (with initializer).
        /// </remarks>
        [AllowNull]
        public Statement Initializer { get; }

        /// <nodoc />
        [AllowNull]
        public Expression Condition { get; }

        /// <nodoc />
        [AllowNull]
        public AssignmentOrIncrementDecrementExpression Incrementor { get; }

        /// <nodoc />
        public Statement Body { get; }

        /// <nodoc />
        public ForStatement(
            [AllowNull]Statement initializer,
            [AllowNull]Expression condition,
            [AllowNull]AssignmentOrIncrementDecrementExpression incrementor,
            Statement body,
            LineInfo location)
            : base(location)
        {
            Contract.Requires(body != null);

            Initializer = initializer;
            Condition = condition;
            Incrementor = incrementor;
            Body = body;
        }

        /// <nodoc />
        public ForStatement(DeserializationContext context, LineInfo location)
            : base(location)
        {
            Initializer = Read<Statement>(context);
            Condition = ReadExpression(context);
            Incrementor = Read<AssignmentOrIncrementDecrementExpression>(context);
            Body = Read<Statement>(context);
        }

        /// <inheritdoc />
        protected override void DoSerialize(BuildXLWriter writer)
        {
            Serialize(Initializer, writer);
            Serialize(Condition, writer);
            Serialize(Incrementor, writer);
            Body.Serialize(writer);
        }

        /// <inheritdoc />
        public override void Accept(Visitor visitor)
        {
            visitor.Visit(this);
        }

        /// <inheritdoc />
        public override SyntaxKind Kind => SyntaxKind.ForStatement;

        /// <inheritdoc />
        public override string ToDebugString()
        {
            return I($"for ({Initializer?.ToDebugString()}; {Condition?.ToDebugString()}; {Incrementor.ToDebugString()}){Body.ToDebugString()}");
        }

        /// <inheritdoc />
        protected override EvaluationResult DoEval(Context context, ModuleLiteral env, EvaluationStackFrame frame)
        {
            // Initializer is optional.
            EvaluationResult? initValue = Initializer?.Eval(context, env, frame);

            if (initValue?.IsErrorValue == true)
            {
                return EvaluationResult.Error;
            }

            // Condition in for loops is optional. If the condition is missing, it means that it is true.
            EvaluationResult condValue = Condition?.Eval(context, env, frame) ?? EvaluationResult.Create(true);

            if (condValue.IsErrorValue)
            {
                return EvaluationResult.Error;
            }

            // Only unit test can specify MaxloopIterations to override this setting.
            var maxLoopIterations = context.FrontEndHost.FrontEndConfiguration.MaxLoopIterations();

            // Technically, the following implementation is not correct, because it assumes that the body is not nullable
            // and that condition expression should be evaluated once.
            // This is definitely not the case for TypeScript.
            int iterations = 0;
            while (Expression.IsTruthy(condValue))
            {
                if (++iterations > maxLoopIterations)
                {
                    context.Errors.ReportForLoopOverflow(env, Location, maxLoopIterations);
                    return EvaluationResult.Error;
                }

                var b = Body.Eval(context, env, frame);

                // If the body returns `Continue`, then do nothing! `Continue` only affects evaluation within the body, not the loop.
                if (b.Value == BreakValue.Instance)
                {
                    break;
                }

                if (b.IsErrorValue || frame.ReturnStatementWasEvaluated)
                {
                    // Got the error, or 'return' expression was reached.
                    return b;
                }

                var incValue = Incrementor?.Eval(context, env, frame);

                if (incValue?.IsErrorValue == true)
                {
                    return EvaluationResult.Error;
                }

                condValue = Condition?.Eval(context, env, frame) ?? EvaluationResult.Create(true);

                if (condValue.IsErrorValue)
                {
                    return condValue;
                }
            }

            return EvaluationResult.Undefined;
        }
    }
}
