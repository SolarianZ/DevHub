# DevHub 与 MCP 对比报告

> 生成日期：2026-03-17  
> 范围说明：本报告忽略 `m5` 里程碑范围限制，只基于当前仓库中的 DevHub 规范、Host/Core/SDK/测试耦合点，以及 MCP 官方文档与 `2025-11-25` 版规范评估。  
> 结论口径：使用“能力族对比计数 + 高/中/低改动量与差距”，不使用伪精确百分比。

## 1. 结论摘要

DevHub 和 MCP 的**表层相似性**确实存在：两者都以 JSON-RPC 2.0 为消息基线，都能以请求/响应形式暴露某种“可调用能力”，也都支持服务端向客户端发送通知。但从**协议角色、生命周期、能力模型和公开契约**来看，二者并不兼容。当前 DevHub Host 更像“本机编排 Hub + 应用注册中心 + 调用路由器 + 事件总线”，而 MCP Server 更像“向 Host/Client 暴露标准化 tools/resources/prompts 等能力的协议端点”。

按本报告选定的 12 个能力族统计，DevHub 与 MCP 的重合情况大致为：

- `1/12` 为**直接重合**：基础 JSON-RPC 2.0 基线。
- `7/12` 为**部分重合**：版本、传输、鉴权、工具式调用、异步任务、通知、发现/会话。
- `3/12` 为**MCP-only**：初始化/能力协商、资源、提示词。
- `1/12` 为**DevHub-only**：作用域/路由/启动编排。

因此：

- **功能重合度**：低到中低。
- **Spec 差距**：高。
- **改造建议**：不要把现有 DevHub 公共协议直接替换成 MCP；应优先采用**双栈聚合网关**方案，在保留 DevHub 协议的前提下新增 MCP 端点。

## 2. 调研依据

### 2.1 DevHub 侧依据

- 权威规范：[Spec.md](./Spec.md)
- 架构规划：[DevHub协议与开发规划.md](./DevHub%E5%8D%8F%E8%AE%AE%E4%B8%8E%E5%BC%80%E5%8F%91%E8%A7%84%E5%88%92.md)
- Host 入口与传输层：
  - [Program.cs](../host/src/DevHub.Host/Program.cs)
  - [RpcHttpEndpointHandler.cs](../host/src/DevHub.Host/RpcHttpEndpointHandler.cs)
  - [WebSocketSessionHandler.cs](../host/src/DevHub.Host/WebSocketSessionHandler.cs)
  - [DevHubTransportValidator.cs](../host/src/DevHub.Host/Transport/DevHubTransportValidator.cs)
- Core 调用编排：
  - [InvocationHandler.cs](../host/src/DevHub.Core/Services/Rpc/Handlers/InvocationHandler.cs)
  - [LaunchCoordinator.cs](../host/src/DevHub.Core/Services/Invocation/LaunchCoordinator.cs)
  - [AppRegistry.cs](../host/src/DevHub.Core/Services/AppRegistry.cs)
- SDK/测试耦合点：
  - [.NET RuntimeDiscovery](../sdks/dotnet/src/DevHub.Sdk/Internal/RuntimeDiscovery.cs)
  - [.NET JsonRpcHttpTransport](../sdks/dotnet/src/DevHub.Sdk/Internal/JsonRpcHttpTransport.cs)
  - [Python HTTP transport](../sdks/python/src/devhub_sdk/_http_transport.py)
  - [JavaScript HTTP transport](../sdks/javascript/src/http-transport.ts)
  - `host/tests/` 下的鉴权、发现、调用与 WS 相关集成测试

### 2.2 MCP 侧依据

- Intro: [Getting Started / Intro](https://modelcontextprotocol.io/docs/getting-started/intro)
- Core spec root: [Specification 2025-11-25](https://modelcontextprotocol.io/specification/2025-11-25)
- Lifecycle: [Basic / Lifecycle](https://modelcontextprotocol.io/specification/2025-11-25/basic/lifecycle)
- Transports: [Basic / Transports](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports)
- Authorization: [Basic / Authorization](https://modelcontextprotocol.io/specification/2025-11-25/basic/authorization)
- Server overview: [Server](https://modelcontextprotocol.io/specification/2025-11-25/server)
- Tools: [Server / Tools](https://modelcontextprotocol.io/specification/2025-11-25/server/tools)
- Resources: [Server / Resources](https://modelcontextprotocol.io/specification/2025-11-25/server/resources)
- Prompts: [Server / Prompts](https://modelcontextprotocol.io/specification/2025-11-25/server/prompts)
- Pagination: [Server / Utilities / Pagination](https://modelcontextprotocol.io/specification/2025-11-25/server/utilities/pagination)
- Roots: [Client / Roots](https://modelcontextprotocol.io/specification/2025-11-25/client/roots)
- Elicitation: [Server / Utilities / Elicitation](https://modelcontextprotocol.io/specification/2025-11-25/server/utilities/elicitation)
- Tasks: [Server / Tasks](https://modelcontextprotocol.io/specification/2025-11-25/server/tasks)
- Schema: [Specification 2025-11-25 / Schema](https://modelcontextprotocol.io/specification/2025-11-25/schema)

## 3. 当前 DevHub 功能与 MCP 功能的重合点和差异点

### 3.1 能力族矩阵

| 能力族 | DevHub 当前能力 | MCP 当前能力 | 判定 | 说明 |
| --- | --- | --- | --- | --- |
| 基础 JSON-RPC | HTTP/WS 都以 JSON-RPC 2.0 为基线，禁止 batch，统一 JSON 错误响应 | 协议基线同样是 JSON-RPC 2.0 | 直接重合 | 这是两者最明显、也是最稳定的重合点。 |
| 连接生命周期 / 初始化 | 无通用 `initialize`；HTTP 直接请求；WS 只要求首条 `hub.ws.authenticate` | 明确定义 `initialize`、能力协商、`notifications/initialized` | MCP-only | DevHub 缺少 MCP 最核心的会话初始化语义。 |
| 版本协商 | `X-DevHub-Protocol: 1` 或 WS `protocolVersion: 1`；规范明确禁止协商 | 使用日期格式协议版本并在初始化阶段协商 | 部分重合 | 都有版本概念，但 DevHub 是“固定版本校验”，MCP 是“协商后的会话版本”。 |
| 传输 | `POST /rpc` + `/ws`；HTTP 始终 `200 OK`；WS 用首消息鉴权 | 标准传输是 `stdio` 和 Streamable HTTP；单端点 `GET`/`POST`，可选 SSE/session header | 部分重合 | 都有 HTTP/消息流，但端点结构、状态码、会话模型差异很大。 |
| 鉴权 | 运行时 `token.txt` + Bearer token + `X-DevHub-*` 头；WS 首消息鉴权 | 受保护 HTTP 场景走 OAuth 2.1 / Protected Resource Metadata；`stdio` 常无鉴权 | 部分重合 | 都关注身份边界，但模型完全不同；DevHub 更像本机私有协议。 |
| 工具能力 | `hub.invoke.notify/request` 能把调用路由给 app；`AppDefinition` 只有 app 级能力开关 | `tools/list` + `tools/call`，工具需要可发现的名称、描述、JSON Schema、注解 | 部分重合 | DevHub 有“可调用能力”，但没有 MCP 式工具目录与 schema。 |
| 资源能力 | 无标准化的资源 URI、读取、模板、订阅模型 | `resources/list`、`resources/read`、模板、订阅/更新通知 | MCP-only | 当前 DevHub 没有与此直接等价的公开能力。 |
| 提示词能力 | 无标准化 prompts 公共能力 | `prompts/list`、`prompts/get` | MCP-only | 当前 DevHub 没有 prompt catalog。 |
| 异步执行 / 任务 | 有 queue/poll/respond、TTL、lease、timeout、pending/offline/autoLaunch | 有实验性的 tasks、progress、cancel 等异步模式 | 部分重合 | 语义方向相近，但 DevHub 偏“Hub 编排”，MCP 偏“tool/task 协议化”。 |
| 事件 / 通知 | `hub.events.subscribe` / `hub.event`；事件类型固定且 Hub 自定义 | `notifications/*`、list changed、progress、logging、resource updated 等 | 部分重合 | 都支持通知，但 DevHub 是一条通用 Hub 事件总线，MCP 是能力域通知。 |
| 发现 / 会话 | 客户端读 `hub.json` 和 `token.txt`，再带 `clientId/clientSessionId` 调用 | 规范定义会话初始化和 `Mcp-Session-Id`；未定义 `hub.json` 这类本地发现文件 | 部分重合 | 都有“连接前准备”和“会话上下文”，但公共契约完全不兼容。 |
| 作用域 / 路由 / 启动编排 | `AppInstance`、scope、launch、dedupe、offline queue、long poll 路由是核心能力 | MCP 无对应标准原语 | DevHub-only | 这是 DevHub 相对 MCP 的核心差异，也是其独特价值。 |

### 3.2 关键观察

1. **DevHub 的“像 MCP tools”之处，主要在 `hub.invoke.request/notify`。**  
   这部分可以类比为“可调用能力”，但它缺少 MCP 工具最关键的三个元素：可发现的工具目录、每个工具稳定的输入 schema、模型可读的语义元数据。

2. **DevHub 的主价值不在“工具协议”，而在“本机编排”。**  
   `AppDefinition`、`AppInstance`、scope、launch、dedupe、poll/respond、lease/TTL 这一整套更像本机进程与应用实例的编排层，而不是 MCP Server 的标准公开面。

3. **MCP 的主价值不在“把 RPC 包成 JSON-RPC”，而在“标准化会话与能力模型”。**  
   MCP 把初始化、能力协商、server/client 职责、tools/resources/prompts、分页、roots、elicitation、tasks 等纳入统一协议面，这部分是 DevHub 目前没有覆盖的。

4. **角色模型也不一致。**  
   从 MCP Intro 的 Host / Client / Server 架构看，MCP Server 通常向 Host/Client 暴露一个稳定的能力视图；而 DevHub Host 当前是“聚合多个 app 的 Hub”。  
   这意味着“让 DevHub Host 提供 MCP 服务”本质上是做一个**MCP 网关/适配层**，而不是把现有 `hub.*` 直接宣布为 MCP。

## 4. DevHub Spec 与 MCP Spec 的差距

### 4.1 章节映射表

| DevHub Spec 区域 | MCP 对应区域 | 结论 | 说明 |
| --- | --- | --- | --- |
| `§3.1 JSON-RPC 2.0 基准` | MCP base protocol / schema | 可复用 | JSON-RPC 2.0 与 UTF-8 JSON 文本基线可以保留。 |
| `§3.2 HTTP 传输` | MCP `basic/transports` | 需重写 | DevHub 的 `/rpc`、固定 `200 OK`、自定义请求头与 MCP Streamable HTTP 契约不同。 |
| `§3.3 WebSocket 传输` | MCP `basic/transports` | 需重写 | MCP 2025-11-25 的标准传输重点是 `stdio` 与 Streamable HTTP，不是 DevHub 现有 `/ws` 模型。 |
| `§4.1 运行时发现（hub.json/token.txt）` | 无直接等价章节 | 需重写 | MCP 规范没有 `hub.json` 等本地发现文件；这是 DevHub 特有部署约定。 |
| `§4.2 HTTP 请求头` | MCP `basic/transports` + `basic/authorization` | 需重写 | `X-DevHub-Protocol`、`X-DevHub-ClientId`、`X-DevHub-ClientSessionId` 都不是 MCP 标准头。 |
| `§4.3 WS 身份验证流程` | MCP `basic/lifecycle` + `basic/authorization` | 需重写 | DevHub 用 `hub.ws.authenticate`，MCP 用 `initialize`/会话协商；受保护 HTTP 还要接 OAuth 2.1。 |
| `§5.1 AppDefinition` | MCP tools/resources/prompts 元数据 | 需适配 | 可作为内部来源，但当前字段远不足以直接投影为 MCP 能力目录。 |
| `§5.2 AppInstance` | 无直接公共等价 | 需适配 | 更适合作为 DevHub 内部运行时模型；若要对外暴露，通常应变成 MCP resource 或内部状态。 |
| `§5.3 Invocation` + `§7 调用生命周期与路由` | MCP tools/tasks/progress/cancel | 需适配 | 可保留为内部实现，但对外公开面必须改成 MCP 方法和结果格式。 |
| `§5.4 HubRuntime (hub.json)` | 无直接等价 | 需重写 | MCP 没有公开要求这种发现文件。 |
| `§5.5 Scope 规则` | 无直接等价 | 需适配 | scope 可以继续作为 DevHub 内部调度语义，但不是 MCP 标准字段。 |
| `§6 RPC 方法（hub.*）` | MCP methods (`initialize`, `tools/list`, `tools/call`, etc.) | 需重写 | 方法名空间和调用语义几乎全部不同。 |
| `§8 错误代码` | JSON-RPC 标准错误 + MCP 特定错误 | 需适配 | 标准 `-326xx/-32700` 可继承；DevHub 自定义 `-320xx` 需要重映射或内部化。 |
| `§9 版本控制与兼容性` | MCP lifecycle/version negotiation | 需重写 | DevHub 的固定整数版本与 MCP 的日期版本协商模型不同。 |

### 4.2 差距判断

如果按“可直接沿用为 MCP 公共契约”的严格标准来看：

- **可直接复用**：基本只有 JSON-RPC 2.0 这层。
- **可保留为内部实现、但不能原样公开**：`AppDefinition`、`AppInstance`、`Invocation`、scope、launch、poll/respond、事件总线。
- **必须重写成 MCP 公开面**：初始化、会话、版本、传输、鉴权、方法名空间、能力协商、工具/资源/提示词目录、分页和客户端反向能力。

所以，“DevHub Spec 与 MCP Spec 的差距有多少”如果用工程语言表达，可以概括为：

- **底层消息层差距小**；
- **公开协议层差距非常大**；
- **运行时编排层有一部分可以保留为 Host 内部实现**。

## 5. 如果把 DevHub Host 改成能够提供 MCP 服务，改动量有多大

### 5.1 先给直接答案

- **只做一个最小 MCP 协议桥接**：改动量 `中到偏大`
- **做成像样的双栈聚合网关（推荐）**：改动量 `大`
- **直接把 DevHub 公共协议替换成 MCP**：改动量 `很大`

### 5.2 三种改造层级

| 方案 | 目标 | 主要改动面 | 复用度 | 改动量 | 建议 |
| --- | --- | --- | --- | --- | --- |
| MVP 协议桥接 | 让 Host 至少能被 MCP Client 调用 | 新增 MCP endpoint、会话层、`initialize`、`tools/list`、`tools/call`；先暴露少量 Host 自有 tools | Core 可部分复用，传输层基本不可复用 | 中到偏大 | 可作为第一阶段 |
| 双栈聚合网关 | 让 Host 成为“DevHub 内核 + MCP 对外适配层” | 在 MVP 基础上，进一步把 app 能力、事件或只读状态投影成更像 MCP 的 tools/resources | Core 复用较高，但需新增元数据与适配层 | 大 | **推荐** |
| 纯 MCP 替换 | 把现有 DevHub 公开协议整体换成 MCP | 公开接口、发现、鉴权、SDK、测试、文档全部重做 | 仅底层部分可复用 | 很大 | 不建议 |

### 5.3 现有代码里哪些能复用，哪些不能

**较适合复用的部分：**

- `AppRegistry`：实例在线状态与筛选逻辑仍可作为内部运行时。
- `DefinitionProvider` / `AppDefinition`：可继续作为 app 元数据来源。
- `LaunchCoordinator`：仍可作为内部启动编排能力。
- `InvocationRoutingService` / `InvocationStore` / `InvocationHandler` 背后的队列、租约、超时模型：可继续做内部执行引擎。
- `HubEventBus`：可继续作为内部事件源。

**不适合直接当作 MCP 公共层复用的部分：**

- `Program.cs` 中当前的端点布局只暴露 `/rpc` 和 `/ws`。
- `RpcHttpEndpointHandler`、`WebSocketSessionHandler`、`DevHubTransportValidator` 都是**强 DevHub 协议耦合**。
- 三套 SDK 和大量 `host/tests/` 已把 `hub.json`、`/rpc`、`/ws`、`X-DevHub-*`、`hub.ws.authenticate` 固化为公开契约。

换句话说，**可以复用 DevHub 的“内核服务”，但不能把现有传输层稍改一下就叫 MCP**。

### 5.4 推荐怎么改

推荐的高层方向是：

1. **保留现有 DevHub 公共协议不动。**
2. **在 Host 上新增独立的 MCP Streamable HTTP 端点。**
3. **新增一个协议无关的应用服务层**，把当前分散在 `IRpcHandler` 里的业务能力提炼出来，然后由：
   - DevHub RPC 层继续调用它；
   - 新的 MCP 适配层也调用它。
4. **MCP 首版只暴露少量 Host 自有 tools，不宣称未实现能力。**
5. **把 queue/poll/respond/scope/launch 继续留在 DevHub 内核里，作为 MCP `tools/call` 的内部执行机制。**

首版 MCP 可以只提供这类高层 tools：

- `devhub.list_definitions`
- `devhub.list_instances`
- `devhub.launch_app`
- `devhub.invoke`

这样做有两个好处：

- 不需要立刻把每个 app 的内部方法都“自动升格”为 MCP tool。
- 可以避免在 `AppDefinition` 尚未具备 method/schema 元数据之前，就强行做一层质量很差的 tool projection。

### 5.5 为什么不建议一开始就“把所有 app 自动变成 MCP tools”

当前 `AppDefinition` 只描述：

- `appId`
- `displayName`
- `description`
- `capabilities`
- `launch`

它**没有**描述：

- 这个 app 暴露哪些稳定可调用 operation
- 每个 operation 的输入 schema / 输出 schema
- 哪些字段适合让模型填写
- 哪些 tool 有副作用、是否只读、是否危险

这意味着如果现在强行把 DevHub app 自动投影成 MCP tools，只能得到两类结果：

- 要么是一个很笼统的 `devhub.invoke` 工具，参数里再塞 `appId`、`method`、`args`；
- 要么是质量很差、缺少 schema 和语义描述的“伪工具目录”。

所以，**当前最大的结构性缺口不是传输，而是能力元数据**。

### 5.6 为什么不把 `stdio` 作为 `DevHub.Host` 首版主路径

MCP 规范支持 `stdio`，它对做“被 Host 拉起的工具服务器”很友好；但当前 `DevHub.Host` 是一个本机常驻 HTTP daemon，核心价值就在于：

- 单实例常驻
- 本地发现
- 多 app 聚合
- launch 与路由编排

因此，若在现有 `DevHub.Host` 上做 MCP：

- **首版更适合走 Streamable HTTP**，因为它符合当前宿主模型；
- `stdio` 更适合未来拆出一个独立的 `DevHub.McpHost` 或单进程 mode，而不是现在的第一优先级。

## 6. 会有哪些 Breaking Changes

### 6.1 如果只是“新增 MCP 端点”

如果采用推荐的双栈方案，只是在现有 Host 上**新增** MCP 端点，而不移除或修改 DevHub 既有公开协议，那么：

- 对现有 DevHub 客户端、SDK、测试，**默认没有 breaking change**。
- 主要新增的是：
  - 新端点与新文档
  - MCP 适配层与其测试
  - 可能新增的配置项

这也是本报告推荐双栈而不是替换的根本原因。

### 6.2 如果“用 MCP 替换 DevHub 公共协议”

这会产生实质性的 breaking changes，主要包括：

1. **运行时发现失效**  
   `hub.json` / `token.txt` / `httpBaseUrl` / `wsUrl` 将不再是公开契约，现有三套 SDK 与大量测试会直接失效。

2. **HTTP/WS 契约失效**  
   当前的 `/rpc`、`/ws`、固定 `200 OK`、`hub.ws.authenticate`、`X-DevHub-*` 头、Bearer token 约定都会失效。

3. **方法名空间失效**  
   `hub.ping`、`hub.apps.*`、`hub.invoke.*`、`hub.events.*` 都不再是公开方法；MCP 会换成 `initialize`、`tools/list`、`tools/call` 等标准方法。

4. **数据模型失效**  
   `AppDefinition`、`AppInstance`、`Invocation`、`HubRuntime` 这些模型不再是公共协议模型；最多只能作为 Host 内部状态存在。

5. **错误语义变化**  
   DevHub 的自定义 `-320xx` 错误码、`error.data.reason`、`hub.event` 事件类型都不再稳定可依赖，需要改为 MCP 兼容语义或内部错误映射。

6. **调用语义变化**  
   `queue/poll/respond/lease/ttl/autoLaunch/scope` 这些对 DevHub 很核心的语义，在 MCP 中不是标准公共字段；要么内化，要么额外扩展。

7. **鉴权模型变化**  
   如果目标是标准的受保护 HTTP MCP 服务，就不能继续把 `token.txt` + 自定义请求头当成通用互操作方案，而应转向 MCP 文档定义的 OAuth 2.1 / Protected Resource Metadata 模型。

8. **测试与 SDK 资产大面积重写**  
   不只是 Host 代码要改，`.NET / Python / JavaScript` SDK 以及 `host/tests/` 的黑盒验证都要同步重做。

## 7. 推荐方案

推荐采用 **双栈聚合网关**：

- DevHub 继续保留现有 `/rpc`、`/ws`、`hub.json`、`hub.*` 公开协议，服务既有客户端和工具链。
- Host 新增一个独立的 MCP Streamable HTTP 入口，对外提供标准 MCP 生命周期与 tools 能力。
- MCP 首版只声明已经实现的能力，至少包括：
  - `initialize`
  - `notifications/initialized`
  - `ping`
  - `tools/list`
  - `tools/call`
- 首版**不声明** `resources`、`prompts`、`sampling`、`roots`、`elicitation`、`tasks` 等未实现能力。
- `AppRegistry`、`LaunchCoordinator`、`InvocationStore` 等保留为内核；MCP `tools/call` 只是在协议层包装这些内部能力。

这条路径的好处是：

- 复用 DevHub 现有内核价值；
- 不打断已有 SDK 与测试；
- 能较快获得 MCP 互操作性；
- 为后续把 `AppDefinition` 扩展成真正的 MCP 能力元数据预留空间。

## 8. 对三个问题的直接回答

### 8.1 当前的 DevHub 功能与 MCP 功能重合点和差异点有多少

- **重合点**：主要是 JSON-RPC 2.0 基线、工具式调用的轮廓、服务端通知能力。
- **部分重合**：版本、传输、鉴权、异步执行、会话/发现概念。
- **差异点**：MCP 有标准初始化、能力协商、tools/resources/prompts、roots、elicitation、tasks；DevHub 有 app 实例注册、scope 路由、launch 编排、poll/respond、lease/TTL，这些都不是对方的标准核心面。
- **总体判断**：重合度低到中低。

### 8.2 DevHub Spec 与 MCP Spec 的差距有多少

- **消息层差距小**：JSON-RPC 基线可以保留。
- **公开协议层差距大**：生命周期、会话、传输、鉴权、方法名空间、能力目录几乎都要重做。
- **内部执行层可保留一部分**：Invocation/launch/routing/scope 更适合做 MCP 适配层背后的内部实现。
- **总体判断**：Spec 差距高。

### 8.3 如果把 DevHub Host 改成能够提供 MCP 服务，改动量有多大，建议怎么改，会有哪些 breaking changes

- **改动量**：  
  - 最小桥接：中到偏大  
  - 推荐双栈聚合网关：大  
  - 直接替换协议：很大
- **建议怎么改**：  
  - 保留 DevHub 原协议  
  - 新增 MCP Streamable HTTP 端点  
  - 提炼协议无关应用服务层  
  - 首版只做 Host 自有 tools  
  - 后续再考虑 resources/prompts 和 app-native tool schema
- **主要 breaking changes**：  
  - 只有在“替换现有 DevHub 公共协议”时才会大面积出现，影响发现、传输、鉴权、方法名、错误语义、SDK 和测试。

## 9. 最后结论

DevHub 与 MCP 的关系，更像是“**内核编排能力可以承载 MCP 适配层**”，而不是“**当前 DevHub 协议已经很接近 MCP，只差改几个方法名**”。  
如果目标是让 DevHub Host 尽快获得 MCP 互操作性，最稳妥的路线不是替换，而是：

- **保留 DevHub**
- **新增 MCP**
- **先桥接，再原生化**

其中最需要尽早补齐的，不是传输层，而是**面向 MCP 的能力元数据模型**：operation 列表、输入输出 schema、注解和安全提示。没有这层，DevHub 很难把当前 app 能力高质量地暴露成 MCP tools。
