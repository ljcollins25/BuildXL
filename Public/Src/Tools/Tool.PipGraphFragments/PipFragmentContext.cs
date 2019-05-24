using System;
using BuildXL.Utilities;
using BuildXL.Utilities.Collections;

namespace PipGraphFragments
{
    public class PipFragmentContext
    {
        private ConcurrentBigMap<DirectoryArtifact, DirectoryArtifact> _directoryMap = new ConcurrentBigMap<DirectoryArtifact, DirectoryArtifact>();
        

        internal DirectoryArtifact Remap(DirectoryArtifact directoryArtifact)
        {
            if (_directoryMap.TryGetValue(directoryArtifact, out var mappedDirectory))
            {
                return mappedDirectory;
            }

            return directoryArtifact;
        }

        internal void AddMapping(DirectoryArtifact oldDirectory, DirectoryArtifact mappedDirectory)
        {
            _directoryMap[oldDirectory] = mappedDirectory;
        }
    }
}
