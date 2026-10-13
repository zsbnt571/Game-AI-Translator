# Screenshot Translator 0.4.2.7 人工验收候选

状态：独立人工验收候选，不是 Accepted。0.4.2.6 人工验收结论为 FAIL。

## 结果

- Release：0 warning / 0 error。
- 0.4.2.7 专项：128 PASS / 0 FAIL。
- 冻结回归：351 PASS / 0 FAIL。
- Settings 1.4：65 PASS / 0 FAIL。
- 总计：544 PASS / 0 FAIL。
- 真实翻译 API：0；ActiveOperations：0；Worker residue：NONE。

## UI 修复

- Settings Tab 为深色 Normal / Selected 两态标题，无 Hover、MouseMove 重绘或布局修改。
- PreviewForm 与 0.4.2.4 UI diff 已恢复为零；Theme 不再接触 Preview Toolbar。
- AppDialog 使用普通 WinForms Form，无 OwnerDraw；运行期 OCR/Vision 错误正文中文化，技术信息置于详情。
- OCR、Translation、Renderer、Preview State、Preview Size、Settings 1.4 未修改。

## Runtime 修复与目录 Diff

0.4.2.6 交付目录仅有占位 `vision-runtime/location.txt`，缺少以下真实依赖：

- `vision-runtime/python/python.exe` 与标准库；
- `vision-runtime/venv/Scripts/python.exe`；
- Paddle/PaddleX、CUDA/cuDNN `.dll` / `.pyd` / site-packages；
- `vision-runtime/paddlex/official_models/PP-DocLayout-S`；
- OCR `runtime/python` 与 `runtime/venv`。

0.4.2.7 最终目录已包含上述项目。由于 Paddle Windows 原生层无法可靠处理中文模型根路径，最终外层目录使用 ASCII 名称；程序 EXE 仍为中文名。

## 最终目录隔离实测

- 程序自身运行参数：`PPDocLayoutS + Rapid`。
- PP-S：Status=OK，Worker PID=10844，Vision=4547ms，模型加载=4179ms，推理=303ms。
- Rapid OCR：2022ms，30 Blocks；总流程 4763ms。
- Python executable 来自最终目录 `vision-runtime/venv/Scripts/python.exe`；未使用系统 Python、Conda 或 PATH Python。
- 最终目录未出现 `V2 Vision Python runtime is missing`。
- 独立 ASCII 目录中 PP-S 实际推理：`PP_S_OK 1 1`。

## 人工验收保留项

Settings 鼠标移动 10 秒、Preview Resize 10 秒、ComboBox 展开 20 次、最小化/恢复、多 Preview、FollowPrevious 与 Fixed→FollowPrevious 仍需人工肉眼验收。自动测试不替代人工流畅度结论。

## 关键回答

1. 0.4.2.6 验收失败：YES
2. Settings Tab 白色：NO
3. Tab Hover 重绘：NO
4. Tab Theme 改布局：NO
5. 输入控件强制全深色：NO
6. 白底白字：NO
7. 运行期错误继续使用混色 MessageBox：NO
8. AppDialog：YES，无复杂 OwnerDraw
9. 错误正文中文：YES
10. 0.4.2.6 缺 V2 Runtime：YES
11. 缺失项已列出并完成目录 Diff：YES
12. 最终目录包含 V2 Runtime：YES
13. Rapid + PP-S 无需系统 Python：YES
14. 最终目录真实运行成功：YES
15. Theme 接触 Preview Toolbar：NO
16. Preview Toolbar 恢复 0.4.2.4：YES
17. Page Host Diagnostics / Safe Recovery：保留
18. Renderer / OCR Core / Translation Core / Settings 1.4：未修改
19. API Calls：0
20. ActiveOperations：0
21. Worker residue：NONE
