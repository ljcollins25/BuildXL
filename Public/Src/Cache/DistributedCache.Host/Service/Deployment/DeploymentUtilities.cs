// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Distributed.NuCache;
using BuildXL.Cache.ContentStore.Distributed.Utilities;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Utils;
using BuildXL.Cache.ContentStore.Stores;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.UtilitiesCore;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Cache.Host.Configuration;
using BuildXL.Utilities;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Serialization;
using static BuildXL.Cache.Host.Configuration.DeploymentManifest;

namespace BuildXL.Cache.Host.Service
{
    /// <summary>
    /// Utilities for interacting with deployment root populated by <see cref="DeploymentIngester"/>
    /// </summary>
    public static class DeploymentUtilities
    {
        /// <summary>
        /// Options used when deserializing deployment configuration
        /// </summary>
        public static JsonSerializerOptions ConfigurationSerializationOptions => JsonUtilities.DefaultSerializationOptions;

        /// <summary>
        /// Special synthesized drop url for config files added by DeploymentRunner
        /// </summary>
        public static Uri ConfigDropUri { get; } = new Uri("config://files/");

        /// <summary>
        /// Options used when reading deployment configuration
        /// </summary>
        public static JsonDocumentOptions ConfigurationDocumentOptions => JsonUtilities.DefaultDocumentOptions;

        /// <summary>
        /// Default name of the deployment manifest file in storage
        /// </summary>
        public static string DeploymentManifestFileName { get; } = "DeploymentManifest.json";

        /// <summary>
        /// Suffix of deployment manifest id file. Deployment manifest id file name is Path.GetFileNameWithoutExtension(manifestFilePath) + <see cref="DeploymentManifestIdSuffix"/>
        /// </summary>
        public static string DeploymentManifestIdSuffix { get; } = "Id.txt";

        /// <summary>
        /// Relative path to root of CAS for deployment files
        /// </summary>
        private static RelativePath CasRelativeRoot { get; } = new RelativePath("cas");

        /// <summary>
        /// Default relative path to deployment manifest reference file from deployment root
        /// </summary>
        private static RelativePath DeploymentManifestIdRelativePath { get; } = new RelativePath("DeploymentManifestId.txt");

        /// <summary>
        /// Relative path to deployment manifest from deployment root
        /// </summary>
        public static RelativePath DeploymentManifestRelativePath { get; } = new RelativePath(DeploymentManifestFileName);

        /// <summary>
        /// File name of deployment configuration in synthesized config drop
        /// </summary>
        public static string DeploymentConfigurationFileName { get; } = "DeploymentConfiguration.json";

        /// <summary>
        /// File name of the ingester configuration file
        /// </summary>
        public static string IngesterConfigurationFileName { get; } = "IngesterConfiguration.json";

        /// <summary>
        /// Name of root json property specifying supplemental preprocessor parameters.
        /// </summary>
        public const string PreprocessorParametersJsonPropertyName = "#Parameters";

        /// <summary>
        /// Gets the relative from deployment root to the file with given hash in CAS
        /// </summary>
        public static RelativePath GetContentRelativePath(ContentHash hash)
        {
            return CasRelativeRoot / FileSystemContentStoreInternal.GetPrimaryRelativePath(hash);
        }

        /// <summary>
        /// Gets the absolute path to the CAS root given the deployment root
        /// </summary>
        public static AbsolutePath GetCasRootPath(AbsolutePath deploymentRoot)
        {
            return deploymentRoot / CasRelativeRoot;
        }

        /// <summary>
        /// Gets the absolute path to the deployment manifest
        /// </summary>
        public static AbsolutePath GetDeploymentManifestPath(AbsolutePath deploymentRoot)
        {
            return deploymentRoot / DeploymentManifestRelativePath;
        }

        /// <summary>
        /// Gets the absolute path to the deployment manifest reference file
        /// </summary>
        public static string GetDeploymentManifestIdPath(string manifestPath)
        {
            var extension = System.IO.Path.GetExtension(manifestPath);
            return manifestPath.Substring(0, manifestPath.Length - extension.Length) + DeploymentManifestIdSuffix;
        }

        /// <summary>
        /// Gets the absolute path to the deployment manifest reference file
        /// </summary>
        public static AbsolutePath GetDeploymentManifestIdPath(AbsolutePath manifestPath)
        {
            return new AbsolutePath(GetDeploymentManifestIdPath(manifestPath.Path));
        }

        /// <summary>
        /// Gets the absolute path to the deployment configuration under the deployment root
        /// </summary>
        public static AbsolutePath GetDeploymentConfigurationPath(AbsolutePath deploymentRoot, DeploymentManifest manifest)
        {
            var configFileSpec = manifest.GetDeploymentConfigurationSpec();
            return deploymentRoot / GetContentRelativePath(configFileSpec.Hash);
        }

        /// <summary>
        /// Gets the file spec for the deployment configuration file
        /// </summary>
        public static FileSpec GetDeploymentConfigurationSpec(this DeploymentManifest manifest)
        {
            var configDropLayout = manifest.Drops[ConfigDropUri.OriginalString];
            return configDropLayout[DeploymentConfigurationFileName];
        }

        /// <summary>
        /// Serialize the value to json using <see cref="ConfigurationSerializationOptions"/>
        /// </summary>
        public static string JsonSerialize<T>(T value)
        {
            return JsonSerializer.Serialize<T>(value, ConfigurationSerializationOptions);
        }

        /// <summary>
        /// Deserialize the value to json using <see cref="ConfigurationSerializationOptions"/>
        /// </summary>
        public static T JsonDeserialize<T>(string value)
        {
            return JsonSerializer.Deserialize<T>(value, ConfigurationSerializationOptions);
        }

#pragma warning disable AsyncFixer03 // Fire & forget async void methods
        public static async void WatchFilesAsync(IReadOnlyList<string> paths, CancellationToken token, TimeSpan pollingInterval, Action<int> onChanged, Action<Exception> onError)
#pragma warning restore AsyncFixer03 // Fire & forget async void methods
        {
            int retries;
            var info = getChangeInfo(paths);
            while (true)
            {
                try
                {
                    await Task.Delay(pollingInterval, token);

                    var newInfo = getChangeInfo(paths);
                    for (int i = 0; i < paths.Count; i++)
                    {
                        if (newInfo[i] != info[i])
                        {
                            info[i] = newInfo[i];
                            onChanged(i);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    retries--;
                    if (retries == 0)
                    {
                        onError(ex);
                    }
                }
            }

            (long Length, DateTime LastWriteTimeUtc, DateTime CreationTimeUtc)[] getChangeInfo(IReadOnlyList<string> paths)
            {
                var result = paths.SelectArray(path =>
                {
                    var fileInfo = new System.IO.FileInfo(path);
                    return (fileInfo.Length, fileInfo.LastWriteTimeUtc, fileInfo.CreationTimeUtc);
                });

                // On success, reset number of retries
                retries = 5;

                return result;
            }
        }

        /// <summary>
        /// Computes an hexidecimal content id for the given string
        /// </summary>
        public static string ComputeContentId(string value)
        {
            return HashInfoLookup.GetContentHasher(HashType.Murmur).GetContentHash(Encoding.UTF8.GetBytes(value)).ToHex();
        }

        /// <summary>
        /// Computes an fractional number between [0, 1] value based on the hash of the given string
        /// </summary>
        public static double ComputeContentHashFraction(this string value)
        {
            var hash = HashInfoLookup.GetContentHasher(HashType.Murmur).GetContentHash(Encoding.UTF8.GetBytes(value));
            SpanReader reader = hash.ToFixedBytes().ToByteArray().AsSpan();
            double numerator = reader.Read<uint>();
            return numerator / uint.MaxValue;
        }

        /// <summary>
        /// Gets augmenting parameters with inlined values in the form of root '#parameters' property of type <see cref="PreprocessorParameters"/>.
        /// </summary>
        public static bool TryGetPreprocessorParameters(this JsonElement element, HostParameters inputParams, out PreprocessorParameters parameters)
        {
            parameters = null;
            if (element.TryGetProperty(PreprocessorParametersJsonPropertyName, out var preprocessorElement))
            {
                var preprocessor = GetHostJsonPreprocessor(inputParams);
                var json = preprocessor.Preprocess(preprocessorElement);

                preprocessorElement = JsonDeserialize<JsonElement>(json);
                parameters = preprocessorElement.Deserialize<PreprocessorParameters>(JsonUtilities.DefaultSerializationOptions);
            }

            return parameters != null;
        }

        /// <summary>
        /// Preprocesses the given json with specified parameters. Additionally allows augmenting parameters
        /// with inlined values in the form of root '#parameters' property of type <see cref="PreprocessorParameters"/>.
        /// </summary>
        public static string Preprocess(string json, HostParameters parameters, bool useInlinedParameters = false, AsyncOut<HostParameters> outParameters = null)
        {
            using var document = JsonDeserialize<JsonDocument>(json);
            var originalParameters = parameters;
            if (useInlinedParameters
                && document.RootElement.TryGetPreprocessorParameters(parameters, out var additionalParameters))
            {
                parameters = parameters.Copy(additionalParameters);
            }

            outParameters?.Set(parameters);
            var preprocessor = GetHostJsonPreprocessor(parameters);
            var preprocessedJson = preprocessor.Preprocess(document.RootElement);

            return preprocessedJson;
        }


        

        /// <summary>
        /// Gets a json preprocessor for the given host parameters
        /// </summary>
        public static JsonPreprocessor GetHostJsonPreprocessor(HostParameters parameters)
        {
            var commonProperties = new Dictionary<string, string>()
            {
                { "Stamp", parameters.Stamp },
                { "OperatingSystem", parameters.OS },
                { "OS", parameters.OS },
                { "Env", parameters.Environment },
                { "Environment", parameters.Environment },
                { "Machine", parameters.Machine },
                { "Region", parameters.Region },
                { "Ring", parameters.Ring },
                { "BuildXLVersion", Utilities.Branding.Version },
                { "UtcNow", parameters.UtcNow.ToReadableString() },
                { "ServiceVersion", parameters.ServiceVersion },
            };

            return new JsonPreprocessor(
                constraintDefinitions: new Dictionary<string, string>()
                    {
                        { "MachineFunction", parameters.MachineFunction },
                        { "MachineFraction", parameters.Machine?.ComputeContentHashFraction().ToString("f8") /* Fraction with 8 point precision i.e. 0.12345678 */ },

                        // Backward compatibility where machine function was not
                        // its own constraint
                        { "Feature", parameters.MachineFunction == null ? null : "MachineFunction_" + parameters.MachineFunction },
                    }
                    .ConcatIfNotNull(commonProperties)
                    .ConcatIfNotNull(parameters.Properties)
                    .Where(e => !string.IsNullOrEmpty(e.Value))
                    .Select(e => new ConstraintDefinition(e.Key, new[] { e.Value }))
                    .ConcatIfNotNull(parameters.Flags?.Where(f => f.Value != null).Select(f => new ConstraintDefinition(f.Key, f.Value))),
                replacementMacros: new Dictionary<string, string>()
                    {
                        { "StampId", parameters.Stamp },
                        { "RegionId", parameters.Region },
                        { "ServiceDir", parameters.ServiceDir },
                    }
                .ConcatIfNotNull(commonProperties)
                .ConcatIfNotNull(parameters.Properties)
                .Concat(GetEnvironmentVariableMacros())
                .Where(e => !string.IsNullOrEmpty(e.Value)));
        }

        private static IEnumerable<T> ConcatIfNotNull<T>(this IEnumerable<T> first, IEnumerable<T> second)
        {
            if (second == null)
            {
                return first;
            }

            return first.Concat(second);
        }

        private static IEnumerable<KeyValuePair<string, string>> GetEnvironmentVariableMacros()
        {
            string homeDirectory;
            try
            {
                homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
            }
            catch(Exception ex)
            {
                Utilities.Core.Analysis.IgnoreArgument(ex);
                homeDirectory = null;
            }

            if (homeDirectory != null)
            {
                yield return new KeyValuePair<string, string>("$HOME", homeDirectory);
            }
        }
    }
}
