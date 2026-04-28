# DevHub Dispatcher for Unity Editor

`devhub.dispatcher` 是 Unity Editor 侧的 DevHub 桥接包。Host 可见的 app instance 始终只有当前 Unity Editor 进程；Unity 内部 Tool 只注册到 dispatcher 本地路由表，不会注册成 Host 侧 app definition 或 app instance。

## 前置条件

- Unity 版本基线为 2019.4。
- 使用方需要把 `python3 scripts/sdk/publish_unity_dotnet_sdk.py` 生成的 `DevHub.Sdk` 运行所需 DLL 放到 Unity 工程的 `Assets/Plugins/Editor` 目录。
- 若 Unity 工程已经通过 `com.unity.nuget.newtonsoft-json` 提供 `Newtonsoft.Json`，导入 publish 目录时不要再复制其中的 `Newtonsoft.Json.dll`，避免重复程序集来源。
- 本包不内置 `DevHub.Sdk`、`Newtonsoft.Json`、`Microsoft.Bcl.AsyncInterfaces`、`System.Threading.Channels` 或其他 DLL，也不在 `package.json` 中声明运行依赖。

## Tool 注册

Dispatcher 支持两类等价注册入口：
- 自动可用的内置 Tool
- 直接注册实现 `IDevHubTool` 的对象
- 按 `toolId` 注册 request handler、notify handler，或两者组合。
无论走哪条入口，Tool 都进入同一个本地注册表，复用同一套冲突检测、路由和错误语义。

### 内置 Tool

Dispatcher 在 Unity Editor 加载和 Domain Reload 后会自动注册以下固定 `toolId`。这些标识属于 dispatcher 保留本地能力，不应用于自定义 Tool：

- `execute-menu-item`
  `payload` 必须是非空 JSON 字符串，内容为 Unity 菜单路径；request 返回 `EditorApplication.ExecuteMenuItem(string)` 的 `bool` 结果，notify 执行同一逻辑但丢弃返回值。

  ```json
  {
    "toolId": "execute-menu-item",
    "payload": "Assets/Reimport"
  }
  ```

- `execute-method`
  `payload` 必须是包含 `TypeName` 和 `MethodName` 的 JSON 对象，两个字段都要求非空字符串并按大小写精确匹配。`TypeName` 可使用 `Type.FullName` 或 `AssemblyQualifiedName`；仅允许调用 `public static` 无参方法。request 会返回方法结果对应的 JSON 值；若方法返回 `JToken`，则原样返回；若返回 `void` 或 `null`，则返回 JSON `null`。notify 会执行该方法，但不会返回额外响应。

  ```json
  {
    "toolId": "execute-method",
    "payload": {
      "TypeName": "UnityEditor.AssetDatabase, UnityEditor",
      "MethodName": "Refresh"
    }
  }
  ```

- `get-data-path`
  不要求额外参数；若提供 `payload` 会被忽略。request 返回当前 `Application.dataPath` 字符串，notify 不返回额外响应。

  ```json
  {
    "toolId": "get-data-path",
    "payload": {
      "ignored": true
    }
  }
  ```

### 对象注册

实现 `IDevHubTool` 后，可通过 `DevHubDispatcher.RegisterTool(this)` 注册：

```csharp
using DevHubDispatcher.Editor;
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

### 委托注册

简单 Tool 不必手写完整 `IDevHubTool`。Dispatcher 支持 request-only、notify-only 和双 handler 注册：

```csharp
using DevHubDispatcher.Editor;
using Newtonsoft.Json.Linq;

Result requestOnly = DevHubDispatcher.RegisterTool(
    "sample-request",
    (method, payload) => new JObject
    {
        ["ok"] = true,
        ["method"] = method
    });

Result notifyOnly = DevHubDispatcher.RegisterTool(
    "sample-notify",
    (DevHubNotifyHandler)((method, payload) =>
    {
        UnityEngine.Debug.Log("收到 DevHub notify: " + method + " " + payload);
    }));

Result dualHandlers = DevHubDispatcher.RegisterTool(
    "sample-dual",
    (method, payload) => JValue.CreateNull(),
    (method, payload) => UnityEngine.Debug.Log("收到 notify: " + method));
```

`toolId` 只在 Unity 进程内用于路由。重复 `toolId`、空 `toolId`、空 Tool 实例，以及 request/notify handler 同时为空的委托注册都会被拒绝。

### 注销

已持有对象实例时，可以继续调用 `DevHubDispatcher.UnregisterTool(IDevHubTool tool)`。若调用方只知道 `toolId`，或注册的是委托包装 Tool，则应调用 `DevHubDispatcher.UnregisterTool(string toolId)`：

```csharp
DevHubDispatcher.UnregisterTool("sample-dual");
```

Tool 主动调用时，`NotifyAsync` 返回 `Task<Result>`，`RequestAsync` 返回 `Task<Result<JToken>>`。调用失败不会向 Unity 编辑器继续抛异常，而是返回失败结果并记录统一格式日志：

```csharp
var requestResult = await DevHubDispatcher.RequestAsync(this, "sample.app", "sample.echo", new JObject());
if (!requestResult.Success)
{
    UnityEngine.Debug.LogError(requestResult.Message);
    return;
}

UnityEngine.Debug.Log("收到返回值: " + requestResult.Value);
```

Dispatcher 日志统一使用如下格式：

```text
[DevHub.Dispatcher][Error][Outbound] Tool 调用失败。toolId=sample-tool, method=sample.echo, reason=DevHub dispatcher 尚未建立 Host 连接。
```

## 消息信封

Dispatcher 统一使用如下信封收发 Tool 消息：

```json
{
  "toolId": "sample-tool",
  "payload": {}
}
```

Tool 主动发送 notify/request 时，调用 `DevHubDispatcher.NotifyAsync(...)` 或 `DevHubDispatcher.RequestAsync(...)`，dispatcher 会写入 `toolId` 并把原始业务载荷放到 `payload` 字段。Host 发往 Unity 的 invocation 也需要使用相同结构，dispatcher 会把 invocation 的 `method` 和 `payload` 转发给匹配 Tool。

Request 路由失败时 dispatcher 返回固定 callee error：`1001 invalid_dispatcher_message`、`1002 tool_not_found`、`1003 tool_handler_failed`。Notify 路由失败只记录错误日志，不影响后续 heartbeat、poll 或其他消息处理。

## 生命周期

Dispatcher 在 Unity Editor 加载后自动读取 DevHub runtime discovery，upsert 当前 Unity 项目的 `AppDefinition`，并注册一个具备 `poll/respond` 能力的 `AppInstance`。Definition 与 Instance 都使用显式 Global scope `""`；真实 Unity `projectPath` 通过 launch `argsTemplate` 与 instance `meta.projectPath` 传递。`appId` 优先使用命令行 `-devhubAppId <value>` 的 canonical override；当该参数缺少值或不满足 canonical `appId` grammar 时，会忽略 override 并回退到 `EditorUserSettings` 中已存储的身份，首次运行时生成并保存。`instanceId` 与 `instancePassword` 同样保存到 `EditorUserSettings`，用于 Domain Reload 后保持同一逻辑实例身份。

Tool 注册表不持久化。Domain Reload 后，内置 Tool 会自动重新注册；自定义 Tool 仍需要自行调用 `RegisterTool`。

## 状态窗口

Unity 菜单 `Window/DevHub/Dispatcher Status` 会打开只读状态窗口，显示当前 Host 连接状态、已注册 Tool 数量、appId、instanceId 和最近错误。

## 验证

默认自动化验证入口为 `.tests` 工程。该工程的职责边界限定为 dispatcher 与 `.NET SDK` / Host 交界面的非 Unity seam 回归，适合纳入的内容包括：

- `-devhubAppId` 解析、canonical grammar 校验与缺省回退。
- dispatcher 面向 `.NET SDK` / Host 的纯逻辑契约整形，例如消息信封、invoke request 选项映射、`AppDefinition` / `AppInstanceRegistration` 构造。
- 为上述契约整形提供的纯 helper、value object 或 mapper。

以下内容归入其他验证路径，不作为 `.tests` 的默认覆盖目标：

- `UnityEditor` / `UnityEngine` 运行时采集、Editor hooks、Domain Reload、状态窗口与内置 Tool 路由。
- Unity 工程导入、`Assets/Plugins` 依赖布局、asmdef 解析、批处理编译与其他 Unity 专属集成问题。
- 与 Host / `.NET SDK` 契约无直接关系的 Unity-only 生命周期、界面或编辑器行为。

当改动仅影响 Unity 专属路径且不改变 Host / `.NET SDK` 契约时，优先走人工 Unity 校验或后续专项测试，而不是扩张 `.tests` 的职责范围。

当前 `.tests` 的最小自动化命令如下：

```powershell
dotnet test apps/unity-devhub-dispatcher/.tests/DevHubDispatcher.Tests/DevHubDispatcher.Tests.csproj -c Release
```

以下 Unity Editor 专属检查属于按需人工验证路径，不作为 dispatcher 最小自动化验收前置条件，可在真实 Unity 工程中执行类似 batch mode 命令：

```powershell
"D:\GameEngines\Unity\2019.4.40f1\Editor\Unity.exe" -batchMode -quit -projectPath "D:\Projects\UnityToolProject2019" -logFile "D:\Projects\DevHub\temp\unity-dispatcher-batchmode.log"
```

若 batch mode 日志表明根因是外部 DLL 缺失、版本冲突或引用布局错误，应先人工修复 Unity 工程 `Assets/Plugins/Editor` 的依赖布局，再继续排查。
