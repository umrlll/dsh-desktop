# 第三方组件与商标声明

本文件是 [`LICENSE`](LICENSE)（MIT）的补充说明。**MIT 许可只覆盖本仓库自有代码**；
下列第三方组件与名称另有其自身的许可与权利义务。

## 随本仓库分发或引用的第三方组件

| 组件 | 说明 | 许可 |
|---|---|---|
| DeepSeek Harness（`@deepseek-ai/dsh` 及上游 Web UI / Host / 插件系统） | 本项目**不修改**其源码，以固定版本原样运行；随包分发的部分遵循其上游许可 | 见上游仓库 |
| Microsoft.Web.WebView2 | WPF/WinForms 的 WebView2 托管封装（NuGet `Microsoft.Web.WebView2`） | 随包附带的 Microsoft 许可条款 |
| xunit / Microsoft.NET.Test.Sdk | 仅测试工程使用，不参与发布产物 | 各自许可 |

## 商标

“DeepSeek Harness”是深度求索公司的注册商标。本仓库仅为**准确说明技术来源与兼容性**而使用
该名称，**不表示任何隶属、合作、授权或背书关系**。

## 特别说明

本项目的 `DSHDesktop/` 为独立实现的 Windows 桌面壳，与上游 `deepseek-harness` 项目无隶属关系；
上游版本由其自身发布节奏维护，本项目不承诺与其同步。
