# 0.3.0 RC2 Temporary WDA Settings Safe

- 基线：RC2 Temporary WDA。
- Temporary WDA 未解决截图闪屏；本轮不再调查闪屏。
- 修复：配置加载成功且用户实际修改后，关闭窗口才保存。
- 保存方式：`settings.json.tmp` 校验后原子替换，并保留 `settings.json.bak`。
- 已知空配置恢复：首次启动检测到全部关键字段为空时，先生成时间戳 blank backup，再从完整的 0.2.2 配置恢复；Key只记录存在状态和长度。
- 诊断程序使用独立临时配置路径，不触碰正式用户配置。
- 自动测试：110 通过、0 失败；真实 API 0 次。
