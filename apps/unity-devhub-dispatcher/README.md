# DevHub Dispatcher for Unity Editor

`devhub.dispatcher` 是 Unity Editor 侧的 DevHub 桥接包。Host 可见的 app instance 始终只有当前 Unity Editor 进程；Unity 内部 Tool 只注册到 dispatcher 本地路由表，不会注册成 Host 侧 app definition 或 app instance。

## 前置条件

- Unity 版本基线为 2019.4。
- 使用方需要把 `DevHub.Sdk` 及其运行所需 DLL 放到 Unity 工程的 `Assets/Plugins/Editor` 目录。
- 本包不内置 `DevHub.Sdk`、`Newtonsoft.Json`、`Microsoft.Bcl.AsyncInterfaces`、`System.Threading.Channels` 或其他 DLL，也不在 `package.json` 中声明运行依赖。

## Tool 注册

Tool 需要实现 `IDevHubTool`，并通过 `DevHubDispatcher.RegisterTool(this)` 注册：

```csharp
using DevHub.Editor;
using Newtonsoft.Json.Linq;

public sealed class SampleTool : IDevHubTool
{
    public string ToolId { get { return "sample-tool"; } }

    public JToken HandleDevHubRequest(string method, JToken payload)
    {
        return new JObject
        {
            ["ok"] = true,
            ["method"] = method,
            ["payload"] = payload
        };
    }

    public void HandleDevHubNotify(string method, JToken payload)
    {
        UnityEngine.Debug.Log("收到 DevHub notify: " + method + " " + payload);
    }
}
```

`toolId` 只在 Unity 进程内用于路由。重复 `toolId`、空 `toolId`、未实现 `IDevHubTool` 的对象都会被拒绝。

## 消息信封

Dispatcher 统一使用如下信封收发 Tool 消息：

```json
{
  "toolId": "sample-tool",
  "payload": {}
}
```

Tool 主动发送 notify/request 时，调用 `DevHubDispatcher.NotifyAsync(...)` 或 `DevHubDispatcher.RequestAsync(...)`，dispatcher 会写入 `toolId` 并把原始业务载荷放到 `payload` 字段。Host 发往 Unity 的 invocation 也需要使用相同结构，dispatcher 会把 invocation 的 `method` 和 `payload` 转发给匹配 Tool。

Request 路由失败时 dispatcher 返回固定 callee error：`1001 invalid_dispatcher_message`、`1002 tool_not_found`、`1003 tool_handler_failed`。Notify 路由失败只记录日志，不影响后续 heartbeat、poll 或其他消息处理。

## 生命周期

Dispatcher 在 Unity Editor 加载后自动读取 DevHub runtime discovery，upsert 当前 Unity 项目的 `AppDefinition`，并注册一个具备 `poll/respond` 能力的 `AppInstance`。`appId` 优先使用命令行 `-devhubAppId <value>`，否则读取 `EditorUserSettings`，首次运行时生成并保存。`instanceId` 与 `instancePassword` 同样保存到 `EditorUserSettings`，用于 Domain Reload 后保持同一逻辑实例身份。

Tool 注册表不持久化。Domain Reload 后，Tool 需要自行重新调用 `RegisterTool`。

## 状态窗口

Unity 菜单 `Window/DevHub/Dispatcher Status` 会打开只读状态窗口，显示当前 Host 连接状态、已注册 Tool 数量、appId、instanceId 和最近错误。

## 验证

最小编译验证命令如下：

```powershell
"D:\GameEngines\Unity\2019.4.40f1\Editor\Unity.exe" -batchMode -quit -projectPath "D:\Projects\UnityToolProject2019" -logFile "D:\Projects\DevHub\temp\unity-dispatcher-batchmode.log"
```

如果 batch mode 日志表明根因是外部 DLL 缺失、版本冲突或引用布局错误，应先人工修复 Unity 工程 `Assets/Plugins/Editor` 的依赖布局，再继续实施。
