# Settings Migration Report

- Schema: 3。
- 迁移保留 API 地址、API Key、模型、目标语言、OCR 语言、文字整理强度和未知未来字段。
- 新字段缺失时：OCR 默认 Windows、译图默认关闭、缓存默认启用、历史上限 20。
- 超时规范化为首字节 10～90 秒、完整请求 30～180 秒，默认 30/90，且完整请求大于首字节。
- 加载未完成/失败不保存；只有加载成功且用户真实修改后才保存；保存采用 tmp 校验 + bak + 原子替换。
- 自动测试全部使用 Preflight 安全副本、临时配置目录或 synthetic settings；未直接写真实用户 settings。
- Codex执行环境无法直接读取真实AppData settings；Preflight SHA256和安全备份由用户在正常Windows环境完成，原配置与备份SHA256一致。
- Preflight 原配置与备份 SHA256：`04CD9F489B9E77AA44A821785591805B10584A23F33A9526E4586C2FEE983F0C`。
- API Key 仅验证“存在”和长度，未写入报告或日志。
# Settings Migration Report — Translation Settings 1

Schema advances from 4 to 5. Existing API URL, API key, model, target language, OCR settings, timeout values, cache settings and preview settings are preserved.

Old files without new fields receive:

- provider kind: OpenAI Compatible;
- provider display name: DeepSeek (OpenAI Compatible);
- style: GameLocalization;
- custom prompt: empty;
- preserve IDs/numbers/variables: enabled;
- prompt version: `settings1-v1`.

Migration continues to use the existing validated temporary file, backup, and atomic replacement flow. Tests use temporary synthetic settings only; the real user settings file was not read or written.

Preflight note: Codex execution cannot directly read the real AppData settings. The user previously created a safety backup in a normal Windows environment and verified identical SHA-256 values.
