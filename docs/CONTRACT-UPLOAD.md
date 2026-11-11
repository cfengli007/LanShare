# 分片续传契约（v1，冻结）

本文件补充 `docs/CONTRACT.md`，只描述新增的分片上传会话与下载断点续传。所有 JSON 字段 camelCase，时间 ISO-8601（UTC）。身份仍由 Cookie `lanshare_uid` 决定；桌面端 owner 为 `desktop`。

## 1. 上传会话

存储目录：`<StorageDirectory>/.tmp/uploads/<uploadId>/`
- `manifest.json`：`{ uploadId, ownerId, createdAt, expiresAt, expiryMinutes, burn, title, files:[{index,name,length,lastModified}] }`
- `<index>.part`：每个文件已接收的字节，顺序追加。
- 会话有效期 24 小时；`LanShareRuntime` 的 10 秒清理循环同时删除过期会话。
- uploadId 使用现有 `RandomToken.UrlSafe(16)`（22 字符）。

### POST /api/uploads
请求 JSON：
```json
{"files":[{"name":"report.pdf","length":123456,"lastModified":1690000000000}],"expiryMinutes":1440,"burn":false,"title":""}
```
校验：
- files 数量 1..200；name 经现有 `NormalizeUploadedName` 清洗后非空。
- length >= 0；总大小 > 0 且 <= `MaxUploadBytes`。
- expiryMinutes 1..43200；burn 可选；title 可空或 <=120。
- 只接受 `application/json`；格式错误 400 `{error,code:"invalid_request"}`；超限 413 `{error,code:"too_large"}`。

响应 201：
```json
{"uploadId":"...","chunkSize":8388608,"expiresAt":"2026-10-24T...Z","files":[{"index":0,"name":"report.pdf","length":123456,"received":0}]}
```

### GET /api/uploads/{uploadId}
- 200：同上结构，`received` 从已有 `<index>.part` 文件长度读取（不超过 length）。
- 不存在、已过期、非当前 owner：一律 404 `{error,code:"upload_not_found"}`。
- 响应头 `Cache-Control: no-store`。

### PUT /api/uploads/{uploadId}/{index}?offset=N
- 请求体：`application/octet-stream` 原始字节；`Content-Length` 必须存在且 1..`chunkSize`（8388608）。
- `offset` 必须等于该文件当前已接收字节数，否则 409 `{error,code:"offset_mismatch",received:N}`。
- 追加写入 `<index>.part`；若写入后超过声明的 `length`，删除本次追加内容并 400 `{error,code:"chunk_too_large"}`。
- 成功 200：`{"index":0,"received":N,"length":123456,"complete":false}`；该文件收满（`received == length`）时 `complete` 为 `true`。
- 会话不存在/过期/非 owner 404；index 越界 404。

### POST /api/uploads/{uploadId}/complete
- 请求体可空（忽略内容）。
- 全部文件 `received == length` 才继续；否则 409 `{error,code:"upload_incomplete",received:N,total:N}`。
- 调用现有 `IShareService.CreateFromUploadsAsync`：把每个 `<index>.part` 作为 `UploadedFile(TempPath, originalName, length)` 传入，使用 manifest 中 owner/expiryMinutes/burn/title。
- 成功 201 `{share:ShareDto}`（与 POST /api/shares 一致），随后删除整个会话目录。
- 创建失败 500 `{error,code:"create_failed"}`，保留会话以便重试。
- 并发 complete 由会话级锁或“先改名 manifest 为 completing”保护，避免重复创建；本次实现最低要求：串行处理同一 uploadId 的写操作（一个 `SemaphoreSlim` 字典或全局锁即可）。

### DELETE /api/uploads/{uploadId}
- 204；删除会话目录；非 owner/不存在 404。

### 兼容性
- 保留旧 `POST /api/shares` multipart 接口，现有 E2E 必须继续通过。

## 2. 下载断点续传

`GET/HEAD /d/{id}` 修改：
- 始终返回 `Accept-Ranges: bytes`。
- 强 ETag：`"<first8 of id>-<length>-<lastWriteTimeUtc.Ticks>"`，响应头 `ETag`；`If-None-Match` 匹配时 304（无 body，不算下载）。
- 支持 `Range: bytes=start-end` / `bytes=start-` / `bytes=-suffix`：
  - 合法：206 + `Content-Range: bytes start-end/length` + `Content-Length: end-start+1`。
  - 不可满足：416 + `Content-Range: bytes */length`。
  - 语法错误：忽略 Range，按 200 整文件。
- 阅后即焚分享也允许 Range；但只有“完整发送”才销毁：
  - 完整发送定义：无 Range（整文件 200），或 `start==0 && end==length-1`（206 覆盖全文件）。
  - 部分 Range 只增加下载计数一次（首次带 Range 且 `start==0` 时 RegisterDownloadAsync），**不调用 CompleteDownloadAsync**。
  - 续传的最后一段若从 0 开始且覆盖全文件，仍算完整；浏览器通常不会这样，故 burn + 断点续传下载完成后也不销毁，直到一次完整 200 下载。README 需说明。
- HEAD 不计数、不销毁。
- 空文件：GET 200，`Content-Length:0`；burn 时直接 CompleteDownloadAsync。
- 保留 `Content-Disposition`（RFC5987）、`Cache-Control: no-store`、`Last-Modified`。

## 3. 网页客户端要求

- 创建会话后把 `{uploadId, files:[{name,size,lastModified}], createdAt}` 存 `localStorage["lanshare.pendingUpload.v1"]`。
- 顺序上传：对每个文件循环 `PUT ...?offset=received`，body 为 `file.slice(received, received+chunkSize)`；每片失败重试 3 次（指数退避 500/1000/2000ms）；`offset_mismatch` 时采用返回的 `received` 继续。
- 进度 = 所有文件 received 之和 / 总大小；显示当前文件名与百分比。
- 完成后 `POST complete`，成功清除 localStorage，展示分享结果并刷新“我的分享”。
- 页面加载时若 localStorage 有会话：`GET /api/uploads/{id}`；200 显示“发现未完成的上传”提示条，含已上传百分比和“继续上传 / 放弃”按钮；404 清除 localStorage。
- 上传中断（网络错误/关闭页面）保留 localStorage 与服务端会话；UI 提示“上传已中断，可稍后继续”。
- “取消上传”= abort 当前 fetch + `DELETE /api/uploads/{id}` + 清除 localStorage。
- 下载页：普通 `<a href="/d/{id}">` 由浏览器处理 Range/ETag；可显示“支持断点续传”文案。

## 4. 验收
- `dotnet build LanShare.sln -c Debug` 0 警告 0 错误。
- DevServer 8911 上 Node 脚本：创建 2 文件会话 → 分片 PUT → GET 校验 received → complete 得到 share → 下载得到 7z；中断后 GET 仍能恢复；offset 错误 409；DELETE 后 404。
- 现有 `scripts/e2e.mjs` 41 项仍全过。
- 网页 Chrome headless（CDP）实测：大文件上传进度、刷新后“继续上传”、完成后链接可下载。
