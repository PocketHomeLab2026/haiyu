# 海域模型定向对比器

![版本](https://img.shields.io/badge/version-0.7.0-blue)
![平台](https://img.shields.io/badge/platform-Windows%20x64-0078D6)
![许可](https://img.shields.io/badge/license-MIT-green)

**版本：** 0.7.0 ｜ **适用平台：** Windows x64 ｜ **更新日期：** 2026-09-19

海域模型定向对比器是一款本地运行的模型结构与定向读取工具。它会扫描用户明确允许访问的本地模型目录，识别模型结构，生成结构因果位图，并按模型原生计算方向对指定张量路径进行比较和有限数值采样。

项目源码、安装脚本、构建脚本和验收脚本均在本仓库公开，采用 [MIT License](LICENSE)。

## 核心边界

- **不主动联网**：程序本身不发起网络请求。
- **不修改模型**：模型文件始终只读；程序只在输出目录和安装目录写入报告、位图与运行回执。
- **不加载完整权重**：结构扫描只读取必要元数据；数值测量只读取选定路径上的有限浮点窗口。
- **不调用 GPU**：当前版本使用 CPU 和本地文件 I/O，不要求显卡。
- **未知结构严格停止**：未审查架构会标记为 `fail-closed`，不会猜测方向。
- **不夸大结论**：结构命中、抽样相似度和字节规避率不等于语义正确率或完整推理加速。

## 系统要求

- Windows 10 / 11，64 位；
- 使用安装包时不需要预装 .NET；
- 从源码构建时需要 .NET 8 SDK；
- 至少预留约 100 MB 磁盘空间，模型及报告空间另计。

支持从常见本地模型目录以及以下环境变量指向的缓存中发现模型：

- `HF_HOME`
- `HUGGINGFACE_HUB_CACHE`
- `TRANSFORMERS_CACHE`
- `MODELSCOPE_CACHE`
- `OLLAMA_MODELS`

## 下载与安装

### 使用 Release 安装包

1. 从仓库的 **Releases** 页面下载 `haiyu-directional-compare-0.7.0-win-x64.zip`。
2. 核对 Release 页面公布的 SHA-256。
3. 解压 ZIP。
4. 双击 `安装海域定向对比器.cmd`。
5. 默认安装目录为：

   ```text
   %LOCALAPPDATA%\Haiyu\DirectionalCompare
   ```

安装程序不要求管理员权限。安装完成后，开始菜单会创建以下入口：

- 一键发现适配并验收本机模型
- 重新扫描与适配本机模型
- 重新绑定默认模型
- 定向对比
- 本机资源与定向验收
- 原始读取与定向读取 A/B 验收
- 定向读取生产稳健性验收
- 运行时定向对比与自动回退
- 卸载

> 当前公开二进制可能没有代码签名，因此 Windows SmartScreen 可能显示警告。请先核对 SHA-256，并仅在你信任本仓库及下载来源时运行；也可以按照下文从源码自行构建。

## 使用流程

### 1. 一键发现、适配与验收

首次使用先运行“**一键发现适配并验收本机模型**”。工具会：

1. 扫描常见模型目录和已配置的模型缓存路径；
2. 只读识别 `config.json + safetensors`、GGUF 和 Ollama GGUF；
3. 按模型原生架构生成独立的结构因果位图；
4. 绑定默认模型；
5. 对满足条件的 safetensors 路线执行原始顺序读取与定向读取 A/B；
6. 只有输出等价、读取量下降且墙钟实测更快时，才允许启用定向比较。

主要报告位于：

```text
%LOCALAPPDATA%\Haiyu\DirectionalCompare\data\客户模型适配报告.txt
%LOCALAPPDATA%\Haiyu\DirectionalCompare\data\customer-adaptation-report.json
%LOCALAPPDATA%\Haiyu\DirectionalCompare\data\客户模型绑定.txt
%LOCALAPPDATA%\Haiyu\DirectionalCompare\data\customer-model-binding.json
%LOCALAPPDATA%\Haiyu\DirectionalCompare\data\客户机一键部署验收.txt
%LOCALAPPDATA%\Haiyu\DirectionalCompare\data\customer-deployment-readiness.json
```

### 2. 定向对比

运行“**定向对比**”，可使用张量名或标准角色指定起点和终点。对 safetensors，工具只读取定向路径上的有限浮点窗口，并输出：

- 余弦相似度；
- 绝对误差；
- 符号一致率；
- 实际读取字节数；
- 路线和来源快照摘要。

### 3. A/B 与稳健性验收

项目分别提供：

- 所选张量完整顺序遍历与相同采样窗口定向跳读的 A/B；
- 多采样尺度、精度覆盖、内存噪声、分层覆盖和故障闭锁测试；
- 运行时身份门、来源快照验证和定向失败后的原顺序回退。

这些结果只证明对应读取路线与本次样本，不能替代真实业务任务的端到端 A/B。

## 支持的模型结构

当前已审查家族包括：

- Llama、Mistral、Qwen2、Qwen3、Granite；
- GPT-NeoX / Pythia；
- Phi、Phi-3、Phi-3 Small；
- StableLM、OLMo2、Bloom；
- Mamba、Falcon-Mamba；
- 实验性 OPT、Falcon、BERT、T5 映射。
- 接受定制模型定向对比
- 有需求可以发邮件619599587@qq.com

GGUF 可以识别结构，但当前版本不对量化块套用通用数值解码公式；相关数值路线会关闭并标记为 `fail-closed`。

## 从源码构建

```powershell
dotnet build .\src\Haiyu.DirectionalCompare\Haiyu.DirectionalCompare.csproj -c Release
dotnet run --project .\src\Haiyu.DirectionalCompare\Haiyu.DirectionalCompare.csproj -c Release -- self-test
```

生成自包含 Windows x64 安装包：

```powershell
powershell -NoProfile -File .\scripts\build-package.ps1
```

输出目录：

```text
artifacts\directional-compare-packages\
```

## 命令行示例

```powershell
haiyu-directional-compare scan --root D:\Models --output D:\HaiyuMaps
haiyu-directional-compare compare --map D:\HaiyuMaps\model.causal-position-map.json --from token_embedding --to language_head
haiyu-directional-compare measure --map D:\HaiyuMaps\model.causal-position-map.json --from attention.query_projection@0 --to attention.output_projection@0
haiyu-directional-compare self-test
```

运行 `haiyu-directional-compare --help` 可查看完整命令。

## 卸载

双击安装包或安装目录中的 `卸载海域定向对比器.cmd`，也可以使用开始菜单中的“卸载”入口。卸载脚本仅允许删除 `%LOCALAPPDATA%\Haiyu\DirectionalCompare` 下的安装目录。

## 输出解释

- `structural-ready`：结构路线成立，但未必有可安全解码的数值窗口；
- `numeric-ready`：结构路线和有限浮点采样均可用；
- `recognized-no-route`：识别了架构，但没有足够张量位置形成路线；
- `configuration-only`：只发现配置缓存，没有可读张量元数据；
- `fail-closed`：结构、边界、身份或数值门未通过，停止使用该路线。

字节规避率描述 I/O 范围，不等于端到端速度倍数。真实任务准确率和完整推理加速必须使用同一批任务进行独立 A/B 验证。

## 仓库结构

```text
src/Haiyu.DirectionalCompare/  核心 .NET 8 源码
package/                       Windows 安装与客户端脚本
scripts/build-package.ps1      可复现打包脚本
tests/                         安装、矩阵与包校验脚本
.github/workflows/             持续集成构建与自检
```

## 许可

MIT License。详见 [LICENSE](LICENSE)。

