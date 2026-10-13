# 0.2.8 思考模式修复报告

## 请求差异

0.2.7请求字段：`model`、`messages`、`temperature`、`stream=false`。没有`thinking`字段。

0.2.8只新增：

```json
"thinking": {
  "type": "disabled"
}
```

模型仍为用户配置的`deepseek-v4-flash`；没有切换到旧模型。OCR原文、Group输入、提示词、temperature、stream、HttpClient、超时及取消逻辑均未改变。

## 响应处理

- 译文只读取`choices[0].message.content`。
- 独立统计`reasoning_content`字符数和UTF-8字节数，不把它作为译文。
- 记录`finish_reason`。
- 若`reasoning_content`非空，写入“思考模式未成功关闭”警告。
- 非流式响应只有在完整JSON到达后才能可靠取得`content`，因此日志中的`first_effective_content_ms`是非流式客户端首次可验证有效content的时间；网络首字节仍由`response first_byte`单独记录。

## 测试状态

- 本地回归：27通过，0失败。
- 真实API：0次。当前受控环境拒绝读取固定用户配置文件，在读取API Key之前终止，未发出网络请求。
- 三次真实耗时、reasoning/content长度及中位数：等待人工测试，未伪造。
