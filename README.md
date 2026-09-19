# 海域模型定向对比器

[![构建与自检](https://github.com/PocketHomeLab2026/haiyu/actions/workflows/build.yml/badge.svg)](https://github.com/PocketHomeLab2026/haiyu/actions/workflows/build.yml)
[![最新版本](https://img.shields.io/github/v/release/PocketHomeLab2026/haiyu?display_name=tag&sort=semver)](https://github.com/PocketHomeLab2026/haiyu/releases)
[![下载次数](https://img.shields.io/github/downloads/PocketHomeLab2026/haiyu/total)](https://github.com/PocketHomeLab2026/haiyu/releases)
[![许可证](https://img.shields.io/badge/license-MIT-green)](LICENSE)

**本地离线的 LLM 模型结构与张量定向对比工具，支持 safetensors、GGUF 结构识别和有限浮点抽样。**

海域模型定向对比器扫描用户明确允许访问的本地模型目录，识别模型架构，生成结构因果位图，并沿模型原生计算方向对指定张量路径进行比较。它适合需要检查本地模型结构、读取范围和定向路线的开发者、模型工程师与本地 AI 用户。

项目源码、安装脚本、构建脚本和验收脚本均公开，采用 [MIT License](LICENSE)。

## 立即开始

| 入口 | 用途 |
| --- | --- |
| [下载最新 Windows x64 版本](https://github.com/PocketHomeLab2026/haiyu/releases/latest) | 下载正式 Release 安装包 |
| [查看安装与使用说明](#下载与安装) | 完成首次运行 |
| [查看真实自检结果](examples/self-test-result.json) | 检查项目当前可验证能力 |
| [提交问题或模型适配请求](https://github.com/PocketHomeLab2026/haiyu/issues/new/choose) | 反馈错误、提供架构信息 |

> 当前版本：**0.7.0** ｜ 平台：**Windows 10/11 x64** ｜ 许可证：**MIT**

## 你能得到什么

- 识别 `config.json + safetensors`、GGUF 和 Ollama GGUF 模型结构；
- 按模型原生方向生成结构因果位图；
- 以张量名或标准角色进行定向路径比较；
- 输出余弦相似度、绝对误差、符号一致率和实际读取字节数；
- 对未知结构、身份不一致、摘要不一致或定向读取失败执行 `fail-closed`；
- 提供 A/B、运行时回退和稳健性验收脚本。

## 重要边界

- **本地运行**：程序本身不主动发起网络请求。
- **模型只读**：不改写原始模型文件，只在输出目录和安装目录写报告、位图与回执。
- **不加载完整权重**：结构扫描只读取必要元数据，数值测量只读取选定路径上的有限窗口。
- **不需要 GPU**：当前版本使用 CPU 和本地文件 I/O。
- **不等于完整推理**：结构命中率、抽样相似度和字节规避率不等于语义准确率或端到端加速倍数。
- **不猜测未知结构**：未审查的模型架构会停止并标记为 `fail-closed`。

英文搜索关键词：`LLM model analysis`、`safetensors tensor comparison`、`GGUF model inspection`、`offline local AI tool`、`Windows model diagnostics`。

## 下载与安装

1. 打开 [Releases](https://github.com/PocketHomeLab2026/haiyu/releases/latest) 页面。
2. 下载类似 `haiyu-directional-compare-0.7.0-win-x64.zip` 的 Windows x64 安装包。
3. 下载同页面的 SHA-256 校验文件并核对压缩包摘要。
4. 解压 ZIP，双击 `安装海域定向对比器.cmd`。
5. 默认安装目录：

   ```text
   %LOCALAPPDATA%\Haiyu\DirectionalCompare
   ```

安装完成后，开始菜单会创建适配、扫描、定向对比、A/B 验收、稳健性验收和卸载入口。安装程序不要求管理员权限。

> 未签名的 Windows 二进制可能触发 SmartScreen。请先核对 SHA-256，并只在你信任仓库和下载来源时运行；也可以从源码自行构建。

## 使用流程

### 1. 自动发现与适配

首次使用运行“**一键发现适配并验收本机模型**”。工具会扫描常见模型目录以及以下缓存环境变量：

```text
HF_HOME
HUGGINGFACE_HUB_CACHE
TRANSFORMERS_CACHE
MODELSCOPE_CACHE
OLLAMA_MODELS
```

主要报告位于：

```text
%LOCALAPPDATA%\Haiyu\DirectionalCompare\data\客户模型适配报告.txt
%LOCALAPPDATA%\Haiyu\DirectionalCompare\data\customer-adaptation-report.json
%LOCALAPPDATA%\Haiyu\DirectionalCompare\data\客户机一键部署验收.txt
%LOCALAPPDATA%\Haiyu\DirectionalCompare\data\customer-deployment-readiness.json
```

### 2. 定向对比

运行“**定向对比**”，使用张量名或标准角色指定起点和终点。示例输出包含：

- 余弦相似度；
- 绝对误差；
- 符号一致率；
- 实际读取字节数；
- 路线、来源快照和安全门结果。

### 3. 结果解读

| 状态 | 含义 |
| --- | --- |
| `structural-ready` | 结构路线成立，但不一定存在可安全解码的数值窗口 |
| `numeric-ready` | 结构路线和有限浮点采样均可用 |
| `recognized-no-route` | 识别了架构，但没有足够张量位置形成路线 |
| `configuration-only` | 只发现配置缓存，没有可读张量元数据 |
| `fail-closed` | 结构、安全、身份或数值门未通过，停止使用该路线 |

## 支持的模型结构

当前已审查家族包括：

- Llama、Mistral、Qwen2、Qwen3、Granite；
- GPT-NeoX / Pythia；
- Phi、Phi-3、Phi-3 Small；
- StableLM、OLMo2、Bloom；
- Mamba、Falcon-Mamba；
- 实验性 OPT、Falcon、BERT、T5 映射。

GGUF 可以自动识别结构，但当前版本不会对量化块套用未经审查的通用数值解码公式；相关数值路线会关闭并标记为 `fail-closed`。

## 从源码构建

环境要求：Windows 10/11 x64、.NET 8 SDK。

```powershell
dotnet build .\src\Haiyu.DirectionalCompare\Haiyu.DirectionalCompare.csproj -c Release
dotnet run --project .\src\Haiyu.DirectionalCompare\Haiyu.DirectionalCompare.csproj -c Release -- self-test
powershell -NoProfile -File .\scripts\build-package.ps1
```

输出目录：

```text
artifacts\directional-compare-packages\
```

命令行示例：

```powershell
haiyu-directional-compare scan --root D:\Models --output D:\HaiyuMaps
haiyu-directional-compare compare --map D:\HaiyuMaps\model.causal-position-map.json --from token_embedding --to language_head
haiyu-directional-compare measure --map D:\HaiyuMaps\model.causal-position-map.json --from attention.query_projection@0 --to attention.output_projection@0
haiyu-directional-compare self-test
```

## 可验证证据

当前 CI 会自动执行源码构建、内置自检、Windows x64 自包含打包、安装包清单校验和产物上传。当前公开自检结果为 **16/16 通过**，并明确记录：未加载完整权重、未调用 GPU、未联网、未修改原始权重。

- [真实自检结果 JSON](examples/self-test-result.json)
- [变更记录](CHANGELOG.md)
- [贡献指南](CONTRIBUTING.md)
- [安全边界](SECURITY.md)

## 定制模型适配

如果你的模型架构未被识别，可以提交 [模型适配请求](https://github.com/PocketHomeLab2026/haiyu/issues/new?template=model-adapter-request.yml)，请尽量提供 `model_type`、`architectures`、错误报告和脱敏后的配置片段。

也可以通过 `619599587@qq.com` 联系定制模型定向对比事宜。请不要发送模型权重、访问令牌或其他敏感文件。

## 参与贡献

欢迎贡献：

- 新模型架构适配；
- Windows 安装和升级测试；
- 报告解析或路径映射错误；
- 增加英文文档和示例；
- 提供真实模型回测结果。

提交代码前请阅读 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 许可

MIT License。详见 [LICENSE](LICENSE)。
