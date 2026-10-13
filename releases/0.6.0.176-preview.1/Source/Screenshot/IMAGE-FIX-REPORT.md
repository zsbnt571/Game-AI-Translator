# 0.2.9 译图修复报告

## 翻译路径

`TranslationService.cs`与0.2.8的SHA-256完全相同：`19C7339D8EE91771E31DE878AC2F2B1FDFEFF18E5FCBC75304918D1F90F36057`。本轮没有修改模型、thinking、提示词、协议、HttpClient、超时或取消。

## Mouth固定样本

实际Windows OCR：12条原始行，整理为4个可翻译Group：

- `GRP001 Title`：The Mouth in the Hole of the wall，矩形`70,55,625,30`
- `GRP002 Body`：完整正文，矩形`373,95,468,343`
- `GRP003 Button`：继续，矩形`623,536,68,34`
- `GRP004 MessageCount`：43条消息，矩形`53,555,157,36`

单字符近方形图标从翻译Group隔离，仍保留在OCR原始快照。本次Windows OCR没有得到有效时间文本，因此没有虚构时间Group。

使用本地模拟译文生成的译图位于测试源码`work/group-report-029/mouth-translated-029.png`。标题和正文分别绘制，正文完整显示；原英文区域先用边缘背景估色的不透明遮盖层覆盖。

## April固定目标

工作区没有“Bitchy Ex | April Torres”原始图片，不能声称完成真实图片人工检查。使用相同目标的合成OCR行和矩形验证得到独立的Title、ImageCaption、Dialogue、CharacterInfo、Body、Time、MessageCount及Button Group，36项回归均通过。

## 渲染规则

- 每个Group按GroupId读取自身Translation，不按数组下标补位。
- SourceRectangle保留OCR区域；RenderRectangle独立计算且不得与其他Group相交。
- RenderRectangle扩张受限：正文最多约15%，其他区域最多约25%。
- 每个类型使用独立最大/最小字号；二分搜索最大可容纳字号。
- 测量和绘制使用同一字体、字重和GDI+布局；不再用`LineLimit`隐藏溢出。
- 只有完整测量通过后才绘制不透明遮盖和全部译文。
- 最小字号仍不适合时，不画遮盖、不画部分译文，完整保留原文并在状态栏列出溢出Group数量。
- Group调试矩形、高亮和内部ID只通过PictureBox Paint显示，不写入原图或译图Bitmap。

## 测试

- Release编译：0错误、0警告。
- 自动回归：36通过、0失败。
- 真实DeepSeek API：0次。
