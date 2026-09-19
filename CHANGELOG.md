# 更新记录

## v0.7.0 - 2026-09-19

- 开源海域模型定向对比器核心源码、安装脚本和测试脚本；
- 支持 Llama、Mistral、Qwen2、Qwen3、Granite、GPT-NeoX/Pythia、Phi、StableLM、OLMo2、Bloom、Mamba、Falcon-Mamba 等模型家族的审查适配；
- 增加 safetensors 定向有限浮点抽样、A/B 验收、运行时回退和稳健性审计；
- 对未知架构、未知量化解码和身份/摘要不一致执行 `fail-closed`；
- GitHub Actions 自动完成 Windows x64 构建、自检、打包、校验和产物上传。
