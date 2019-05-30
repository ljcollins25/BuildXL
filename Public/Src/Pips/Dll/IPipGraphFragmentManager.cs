using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BuildXL.Utilities;

namespace BuildXL.Pips
{
    /// <summary>
    /// Manager to add binary pip fragments to a pip graph builder
    /// </summary>
    public interface IPipGraphFragmentManager
    {
        /// <summary>
        /// Add a pip graph fragment file to the graph.
        /// </summary>
        /// <param name="fragmentName">Name of the fragment</param>
        /// <param name="filePath">Path to the file to read.</param>
        /// <param name="dependencyNames">Name to the pip fragments this fragment depends on.</param>
        Task<bool> AddFragmentFileToGraph(string fragmentName, AbsolutePath filePath, string[] dependencyNames);

        /// <summary>
        /// Get the current status of a fragment with a given name (pips addded to the graph from the fragment, total pips in the fragment)
        /// </summary>
        (int,int) GetPipsAdded(string name);

        /// <summary>
        /// Get the task representing the addition of all pip fragments into the graph.
        /// </summary>
        Task<bool> WaitForAllFragmentsToLoad();
    }
}