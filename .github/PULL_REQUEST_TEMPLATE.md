## 变更内容

<!-- 简要说明本 PR 解决的问题和修改范围。 -->

## 验证结果

- [ ] `dotnet build .\src\Haiyu.DirectionalCompare\Haiyu.DirectionalCompare.csproj -c Release`
- [ ] `dotnet run --project .\src\Haiyu.DirectionalCompare\Haiyu.DirectionalCompare.csproj -c Release -- self-test`
- [ ] 如涉及打包，已运行 `tests/verify-package.ps1`

## 边界确认

- [ ] 没有上传模型权重、令牌或其他敏感数据
- [ ] 没有把抽样相似度宣传为语义准确率
- [ ] 没有把 I/O 规避率宣传为端到端加速倍数
- [ ] 未知结构仍然安全停止，而不是猜测方向
