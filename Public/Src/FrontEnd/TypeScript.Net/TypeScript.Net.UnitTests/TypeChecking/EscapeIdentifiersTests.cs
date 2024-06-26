// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using TypeScript.Net.DScript;
using TypeScript.Net.UnitTests.TypeChecking;
using Xunit;

namespace Test.DScript.TypeChecking
{
    public sealed class EscapeIdentifiersTests
    {
        private const string BrandingAssignment =
@"interface One {
    __brand: any;
}
 
interface Two {
    __brand2: any;
}
 
const one: One = undefined;
const two: Two = one;";

        [Fact]
        public void IdentifiersAreEscaped()
        {
            var diagnostics = TypeCheckingHelper.GetSemanticDiagnostics(
                parsingOptions: ParsingOptions.DefaultParsingOptions,
                implicitReferenceModule: true,
                codes: BrandingAssignment);
            Assert.Single(diagnostics);
        }
    }
}
