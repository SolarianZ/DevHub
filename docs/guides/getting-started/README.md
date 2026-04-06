# DevHub Host 上手入口

本分组用于承载首次使用 DevHub Host 的文档，面向需要完成环境准备、启动 Host、读取运行时发现文件并做最小调用验证的外部用户。

## 当前入口

- [`../../operations/部署与运行指南.md`](../../operations/部署与运行指南.md)：当前可直接参考的启动方式、数据根目录布局与运行说明。
- [`../../spec/Spec.md`](../../spec/Spec.md)：`hub.json`、`tokenFile` 与公开协议行为的权威来源。
- [`../../README.md`](../../README.md)：文档总入口。

## 正式发布前占位

在首个正式 GitHub Release 发布前，Host 下载入口、平台资产名称与安装命令统一使用以下占位写法：

```text
TODO(devhub-release): 首个正式 GitHub Release 发布后，在此补充 Host 下载资产、平台对应压缩包名称与启动命令；当前阶段不要填写未发布的版本号、下载链接或安装命令。
```

后续 Host 快速上手、发现文件读取和最小 `hub.ping` 验证文档都会落在本分组下。
