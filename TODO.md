# 待办事项

## 待处理问题

### Host

#### 其他

- 用了内存明文密码
- 没有限制 `queueIfOffline` 的最大等待时间

### .NET SDK

- 各个 SDK 发布后，包里带 README.md ，使用的仍是仓库里的相对路径，发布后会失效，应该改成 url 路径（需要等合并 main 之后）。

### JS/TS SDK

- sdks/javascript 当前依赖树里有 6 个已知的 moderate 级别安全通告，来源是 npm audit，升级 vitest 到 4.1.5 能解决这个问题吗？影响范围有多大？（Monitor依赖JS SDK）

### Python SDK

### Monitor

- 【测试】页面的请求文本输入框每次输入文本后，光标都会自动跳到最后一个字符后面，影响正常输入

## 待实现功能

### Host

### Monitor
