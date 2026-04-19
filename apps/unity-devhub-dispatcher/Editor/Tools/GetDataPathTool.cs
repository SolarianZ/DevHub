using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DevHubDispatcher.Editor
{
    internal sealed class GetDataPathTool : DevHubBuiltInToolBase
    {
        internal const string FixedToolId = "get-data-path";

        internal GetDataPathTool() : base(FixedToolId) { }

        protected override object Execute(JToken payload)
        {
            return Application.dataPath;
        }
    }
}