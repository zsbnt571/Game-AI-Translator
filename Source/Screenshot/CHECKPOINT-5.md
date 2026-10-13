# CHECKPOINT-5 — History / Cache / Runtime

- Build: PASS
- OCR LRU 上限 32；Translation Cache 上限 128，只缓存完整成功结果。
- 会话历史默认 20 项并受 96 MiB 内存预算限制；退出即释放，不写用户磁盘。
- Runtime 支持本地路径指针、状态检查、安装/修复脚本和安全退出。
- Settings schema 3、dirty guard、原子保存、未知字段保留均完成。
