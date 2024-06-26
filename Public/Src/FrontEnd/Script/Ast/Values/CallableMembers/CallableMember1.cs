// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using BuildXL.FrontEnd.Script.Evaluator;
using BuildXL.Utilities.Core;

namespace BuildXL.FrontEnd.Script.Values
{
    /// <summary>
    /// Call signature for a member that takes a receiver and one argument.
    /// </summary>
    public delegate EvaluationResult CallableMemberSignature1<T>(Context context, T receiver, EvaluationResult arg, EvaluationStackFrame captures);

    /// <summary>
    /// Callable member that takes exactly one argument
    /// </summary>
    public sealed class CallableMember1<T> : CallableMember<T>
    {
        private readonly CallableMemberSignature1<T> m_function;

        /// <nodoc />
        public CallableMember1(FunctionStatistic statistic, SymbolAtom name, CallableMemberSignature1<T> function, short minArity, bool rest)
            : base(statistic, name, minArity, 1, rest)
        {
            m_function = function;
        }

        /// <inheritdoc />
        public override SyntaxKind Kind => SyntaxKind.Function1;

        /// <inheritdoc />
        public override EvaluationResult Apply(Context context, T receiver, EvaluationResult arg, EvaluationStackFrame captures)
        {
            return m_function(context, receiver, arg, captures);
        }
    }
}
