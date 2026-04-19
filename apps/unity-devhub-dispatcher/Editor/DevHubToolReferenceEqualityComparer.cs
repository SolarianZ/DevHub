using System.Collections.Generic;

namespace DevHub.Editor
{
    internal sealed class DevHubToolReferenceEqualityComparer : IEqualityComparer<IDevHubTool>
    {
        public static readonly DevHubToolReferenceEqualityComparer Instance = new DevHubToolReferenceEqualityComparer();

        public bool Equals(IDevHubTool x, IDevHubTool y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(IDevHubTool obj)
        {
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
