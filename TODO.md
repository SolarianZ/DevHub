# 待办事项

## 待处理问题

### Host

#### 其他

- 用了内存明文密码
- 一些无 id 请求在失败路径上失败路径上会得到空 200 响应，请求方有办法判断发生错误了吗？有没有错误码等信息？
- `queueIfOffline` 似乎没必要存在？
- 现在做不到在已有 app definition （appid+scope) 的情况下，注册一个相同 appid 但 scope 不同的 app instance

### .NET SDK

### JS/TS SDK

- abandonedRequests 会保留到 session 结束，如果单个长连接上持续产生大量超时且始终不重连，这个集合会增长。

### Python SDK

### Monitor


## 待实现功能

### Host

- 获取 host 的版本
- hub.apps.getInstance 获取 app 实例的详细信息？

### Monitor

#### 增加【测试】页面

- 发送请求
- 显示回报内容
