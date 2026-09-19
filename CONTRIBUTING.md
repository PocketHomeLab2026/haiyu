# 贡献指南

感谢你为海域模型定向对比器提交反馈或代码。

## 适合提交的内容

- 新模型架构的只读识别和原生方向映射；
- 安装、打包、Windows 兼容性修复；
- 解析错误、路径映射错误和 `fail-closed` 边界问题；
- 文档、示例报告和英文翻译；
- 有明确测试口径的性能或读取范围改进。

## 提交问题前

1. 确认使用的版本和 Windows 版本；
2. 运行 `self-test`，附上通过数和失败项；
3. 附上脱敏后的错误信息、`model_type`、`architectures` 和相关配置字段；
4. 不要上传模型权重、访问令牌、私有路径或其他敏感数据。

## 本地验证

```powershell
dotnet build .\src\Haiyu.DirectionalCompare\Haiyu.DirectionalCompare.csproj -c Release
dotnet run --project .\src\Haiyu.DirectionalCompare\Haiyu.DirectionalCompare.csproj -c Release -- self-test
pwsh -NoProfile -File .\scripts\build-package.ps1
pwsh -NoProfile -File .\tests\verify-package.ps1 -PackageDirectory .\artifacts\directional-compare-packages\haiyu-directional-compare-0.7.0-win-x64
```

## 设计边界

本项目坚持：模型文件只读、未知结构停止、不把抽样相似度宣传成语义准确率、不把 I/O 范围规避率宣传成端到端加速倍数。涉及这些边界的修改必须附带测试和说明。
