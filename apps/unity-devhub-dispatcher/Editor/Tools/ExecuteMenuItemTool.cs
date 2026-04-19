using Newtonsoft.Json.Linq;
using UnityEditor;

namespace DevHubDispatcher.Editor
{
    internal sealed class ExecuteMenuItemTool : DevHubBuiltInToolBase
    {
        public const string FixedToolId = "execute-menu-item";

        internal ExecuteMenuItemTool() : base(FixedToolId) { }

        protected override object Execute(JToken payload)
        {
            string menuItemPath = RequireNonEmptyString(payload, "menuItemPath");
            return EditorApplication.ExecuteMenuItem(menuItemPath);
        }
    }
}