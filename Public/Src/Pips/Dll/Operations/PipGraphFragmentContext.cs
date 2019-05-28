using BuildXL.Utilities;
using BuildXL.Utilities.Collections;

namespace BuildXL.Pips.Operations
{
    /// <summary>
    /// PipGraphFragmentContext
    /// </summary>
    public class PipGraphFragmentContext
    {
        private ConcurrentBigMap<DirectoryArtifact, DirectoryArtifact> m_directoryMap = new ConcurrentBigMap<DirectoryArtifact, DirectoryArtifact>();

        internal DirectoryArtifact Remap(DirectoryArtifact directoryArtifact)
        {
            if (m_directoryMap.TryGetValue(directoryArtifact, out var mappedDirectory))
            {
                return mappedDirectory;
            }

            return directoryArtifact;
        }

        /// <summary>
        /// PipGraphFragmentContext
        /// </summary>
        public void AddMapping(DirectoryArtifact oldDirectory, DirectoryArtifact mappedDirectory)
        {
            m_directoryMap[oldDirectory] = mappedDirectory;
        }
    }
}
