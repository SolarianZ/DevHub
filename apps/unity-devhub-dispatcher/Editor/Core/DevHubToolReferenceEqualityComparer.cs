using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DevHubDispatcher.Editor
{
    internal sealed class DevHubToolReferenceEqualityComparer : IEqualityComparer<IDevHubTool>
    {
        public static readonly DevHubToolReferenceEqualityComparer Instance = new DevHubToolReferenceEqualityComparer();

        public bool Equals(IDevHubTool x, IDevHubTool y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(IDevHubTool tool)
        {
            return RuntimeHelpers.GetHashCode(tool);
        }
    }
}