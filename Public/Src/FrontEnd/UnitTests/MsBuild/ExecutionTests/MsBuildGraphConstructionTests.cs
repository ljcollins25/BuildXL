// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using BuildXL.FrontEnd.MsBuild;
using BuildXL.Pips.Operations;
using BuildXL.Utilities;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Tracing;
using BuildXL.Utilities.Configuration;
using BuildXL.FrontEnd.MsBuild.Tracing;
using TypeScript.Net.Extensions;
using Xunit;
using Xunit.Abstractions;
using System.IO;
using BuildXL.Utilities.Configuration.Mutable;

namespace Test.BuildXL.FrontEnd.MsBuild
{
    /// <summary>
    /// Here we use the fact that there is an output directory prediction for OutputPath environment variable/property. So
    /// if OutputPath reaches the corresponding predictor, we should be able to observe an OutputDirectory being added for it
    /// in the corresponding process
    /// </summary>
    public sealed class MsBuildGraphConstructionTests : MsBuildPipExecutionTestBase
    {
        public MsBuildGraphConstructionTests(ITestOutputHelper output)
            : base(output)
        {
        }

        /// <summary>
        /// We just need the engine to schedule pips
        /// </summary>
        protected override EnginePhases Phase => EnginePhases.Schedule;

        [Fact]
        public void EnvironmentIsUsedDuringConstruction()
        {
            // We expect the resulting process to contain an output directory matching the content of OutputPath
            var process = CreateDummyProjectWithEnvironment(new Dictionary<string, string> { ["OutputPath"] = @"Z:\\foo" });
            Assert.True(process.DirectoryOutputs.Any(dir => dir.Path == AbsolutePath.Create(PathTable, @"Z:\foo")));
        }

        [Fact]
        public void PassthroughEnvironmentIsUsedDuringConstruction()
        {
            // We expect the resulting process to contain an output directory matching the content of OutputPath
            Environment.SetEnvironmentVariable("OutputPath", @"Z:\\foo");
            var env = new Dictionary<string, DiscriminatingUnion<string, UnitValue>> { ["OutputPath"] = new DiscriminatingUnion<string, UnitValue>(UnitValue.Unit)};

            var process = CreateDummyProjectWithEnvironment(env);
            Assert.True(process.DirectoryOutputs.Any(dir => dir.Path == AbsolutePath.Create(PathTable, @"Z:\foo")));
        }

        [Fact]
        public void EnvironmentBlocksExposingTheProcessEnvironment()
        {
            // Even though the output path is set in the environment, since we are passing an empty dictionary for the environment, it
            // shouldn't reach the predictor
            Environment.SetEnvironmentVariable("OutputPath", @"Z:\\foo");

            var process = CreateDummyProjectWithEnvironment(new Dictionary<string, string>());
            Assert.False(process.DirectoryOutputs.Any(dir => dir.Path == AbsolutePath.Create(PathTable, @"Z:\foo")));
        }

        [Fact]
        public void AbsenceOfEnvironmentExposesTheProcess()
        {
            // Null environment should expose the environment
            Environment.SetEnvironmentVariable("OutputPath", @"Z:\\foo");

            var process = CreateDummyProjectWithEnvironment(environment: (Dictionary<string, string>) null);
            Assert.True(process.DirectoryOutputs.Any(dir => dir.Path == AbsolutePath.Create(PathTable, @"Z:\foo")));
        }

        [Fact]
        public void GlobalPropertiesAreUsedDuringConstruction()
        {
            var config = Build(globalProperties: new Dictionary<string, string> { ["OutputPath"] = @"Z:\\foo" })
                .AddSpec(R("TestProject.proj"), CreateHelloWorldProject())
                .PersistSpecsAndGetConfiguration();

            var process = RunEngineAndRetrieveProcess(config);

            Assert.True(process.DirectoryOutputs.Any(dir => dir.Path == AbsolutePath.Create(PathTable, @"Z:\foo")));
        }

        [Theory]
        [InlineData("enableBinLogTracing: true", "/binaryLogger")]
        [InlineData("logVerbosity: \"diagnostic\"", "msbuild.log;Verbosity=Diagnostic")]
        [InlineData("logVerbosity: undefined", "msbuild.log;Verbosity=Normal")]
        public void ResolverConfigurationIsPropagatedToMSBuildCall(string configArguments, string expectedMSBuildArguments)
        {
            var config = Build(configArguments)
                .AddSpec(R("TestProject.proj"), CreateHelloWorldProject())
                .PersistSpecsAndGetConfiguration();

            var process = RunEngineAndRetrieveProcess(config);
            string arguments = RetrieveProcessArguments(process);

            Assert.Contains(expectedMSBuildArguments, arguments);
        }

        [Theory]
        [InlineData("enableEngineTracing: true", BuildEnvironmentConstants.MsBuildDebug)]
        [InlineData("enableEngineTracing: true", BuildEnvironmentConstants.MsBuildDebugPath)]
        public void ResolverConfigurationIsPropagatedToMSBuildEnvironment(string configArguments, string expectedEnvironmentVariable)
        {
            var config = Build(configArguments)
                .AddSpec(R("TestProject.proj"), CreateHelloWorldProject())
                .PersistSpecsAndGetConfiguration();

            var process = RunEngineAndRetrieveProcess(config);

            Assert.Contains(expectedEnvironmentVariable, process.EnvironmentVariables.Select(var => var.Name.ToString(PathTable.StringTable)));
        }

        [Fact]
        public void ResolverInitialTargetsAreHonored()
        {
            var config = Build(@"initialTargets: [""foo""]")
                .AddSpec(R("TestProject.proj"), CreateProjectWithTarget("foo"))
                .PersistSpecsAndGetConfiguration();

            var process = RunEngineAndRetrieveProcess(config);
            string arguments = RetrieveProcessArguments(process);

            Assert.Contains("/t:foo", arguments);
        }

        [Fact]
        public void ResolverMultipleEntryFilesAreHonored()
        {
            var config = Build(@"fileNameEntryPoints: [r`ProjectA.proj`, r`ProjectB.proj`]")
                .AddSpec(R("ProjectA.proj"), CreateProjectWithTarget("foo"))
                .AddSpec(R("ProjectB.proj"), CreateProjectWithTarget("bar"))
                .PersistSpecsAndGetConfiguration();

            var engineResult = RunEngineWithConfig(config);
            Assert.True(engineResult.IsSuccess);

            var pipGraph = engineResult.EngineState.PipGraph;
            var arguments = pipGraph.RetrievePipsOfType(PipType.Process).Select(process => RetrieveProcessArguments((Process)process)).ToList();

            Assert.Equal(arguments.Count, 2);
            Assert.True(arguments.Any(argument => argument.Contains("/t:foo")));
            Assert.True(arguments.Any(argument => argument.Contains("/t:bar")));
        }

        [Fact]
        public void ProjectWithKnownEmptyTargetsIsNotScheduled()
        {
            // main project references another project, but explicitly doesn't call 'Build'
            // on the referenced project
            var mainProject =
@"<Project>
    <ItemGroup>
		<ProjectReferenceTargets Include=""Build"" Targets=""""/>
        <ProjectReference Include='ReferencedProject.proj'/>
    </ItemGroup >
    <Target Name=""Build"">
        <Message Text=""Hello world""/>
    </Target>
</Project>";

            var referencedProject =
@"<Project>
    <ItemGroup>
        <ProjectReferenceTargets Include=""Build"" Targets=""""/>
    </ItemGroup>
    <Target Name=""Build"">
        <Message Text=""Hello referenced world""/>
    </Target>
</Project>";

            var config = Build("fileNameEntryPoints: [ r`MainProject.proj` ]")
                .AddSpec(R("MainProject.proj"), mainProject)
                .AddSpec(R("ReferencedProject.proj"), referencedProject)
                .PersistSpecsAndGetConfiguration();

            // There should be a single process scheduled, the main one
            var mainProcess = RunEngineAndRetrieveProcess(config);
            var arguments = RetrieveProcessArguments(mainProcess);
            Assert.Contains("MainProject.proj", arguments);

            // The referenced process should have been skipped
            AssertVerboseEventLogged(LogEventId.ProjectWithEmptyTargetsIsNotScheduled);
        }

        [Fact]
        public void GlobalPropertyKeysAreCaseInsensitive()
        {
            var config = Build("globalProperties: Map.empty<string, string>().add('Blah', 'a').add('BLAH', 'b')")
                .AddSpec(R("A.proj"), CreateHelloWorldProject())
                .PersistSpecsAndGetConfiguration();

            var result = RunEngineWithConfig(config);
            Assert.False(result.IsSuccess);

            AssertErrorEventLogged(LogEventId.InvalidResolverSettings);
        }

        [Fact]
        public void MalformedPredictionsAreIgnored()
        {
            // We set the output path to a malformed directory.
            var process = CreateDummyProjectWithEnvironment(new Dictionary<string, string> { ["OutputPath"] = @"not,a,dir" });
            // The process should be computed succesfully with no directory outputs predicted. So the test root will be the only shared opaque
            Assert.Equal(AbsolutePath.Create(PathTable, TestRoot), process.DirectoryOutputs.Single().Path);
        }

        [Fact(Skip = "Suspected of causing the temp deletion logic to fail at removing files under the user profile (via a junction). Temporarily skipping the test.")]
        public void RedirectedUserProfileIsHonored()
        {
            // Create a project directly under the user profile
            var pathToTestProj = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "test.csproj");

            try
            {
                // We still need an entry point under the repo root, so we create a 'dirs' project for that
                var config = (CommandLineConfiguration)Build()
                        .AddSpec("dirs.proj", CreateDirsProject(pathToTestProj))
                        .AddSpec(pathToTestProj, CreateHelloWorldProject())
                        .PersistSpecsAndGetConfiguration();

                // Set a redirected profile root to point to the test root
                var redirectedProfile = AbsolutePath.Create(PathTable, TestRoot);
                config.Layout.RedirectedUserProfileJunctionRoot = redirectedProfile;

                var engineResult = RunEngineWithConfig(config);
                Assert.True(engineResult.IsSuccess);

                // The test project file for the corresponding pip should be located in the redirected user profile
                var testProj = (Process)engineResult.EngineState.PipGraph
                    .RetrievePipsOfType(PipType.Process)
                    .Single(p => RetrieveProcessArguments((Process)p).Contains(SpecialFolderUtilities.GetFolderPath(Environment.SpecialFolder.UserProfile)));

                // There shouldn't be a statically declared input for test.csproj under the redirected profile since the user profile has a corresponding mount
                // with hash source file disabled, so static declarations under it are skipped. But so the declaration was properly redirected
                Assert.False(testProj.Dependencies.Any(input => 
                    input.Path.IsWithin(PathTable, redirectedProfile) && 
                    input.Path.GetName(PathTable) == PathAtom.Create(StringTable, "test.csproj"))
                    );
            }
            finally
            {
                File.Delete(pathToTestProj);
            }
        }

        #region helpers

        private Process CreateDummyProjectWithEnvironment(Dictionary<string, string> environment)
        {
            Dictionary<string, DiscriminatingUnion<string, UnitValue>> fullEnvironment = null;
            if (environment != null)
            {
                fullEnvironment = environment.ToDictionary(kvp => kvp.Key, kvp => new DiscriminatingUnion<string, UnitValue>(kvp.Value));
            }

            return CreateDummyProjectWithEnvironment(fullEnvironment);
        }

        private Process CreateDummyProjectWithEnvironment(Dictionary<string, DiscriminatingUnion<string, UnitValue>> environment)
        {
            var config = BuildWithEnvironment(environment: environment)
                    .AddSpec(R("TestProject.proj"), CreateHelloWorldProject())
                    .PersistSpecsAndGetConfiguration();

            return RunEngineAndRetrieveProcess(config);
        }

        private Process RunEngineAndRetrieveProcess(ICommandLineConfiguration config)
        {
            var engineResult = RunEngineWithConfig(config);
            Assert.True(engineResult.IsSuccess);

            var pipGraph = engineResult.EngineState.PipGraph;
            var process = (Process)pipGraph.RetrievePipsOfType(PipType.Process).Single();
            return process;
        }

        #endregion
    }
}