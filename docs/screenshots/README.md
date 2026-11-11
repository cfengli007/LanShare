# docs/screenshots

界面截图存放目录。README 的「截图」一节直接引用这里的文件。

## 命名约定

```
<页面>-<light|dark>.png
```

## 取图清单

| 文件名 | 界面 | 主题 | 尺寸 | 要拍到的内容 |
|---|---|---|---|---|
| `overview-dark.png` | 概览 | 深色 | 窗口默认大小 | 服务状态卡（运行中）+ 访问地址卡（内网 / 外网两行）+ 最近分享 |
| `overview-light.png` | 概览 | 浅色 | 同上 | 同上，用于浅色对照 |
| `shares-dark.png` | 我的分享 | 深色 | 同上 | 至少 3 行、含不同来源（桌面 / 网页）与操作按钮 |
| `newshare-light.png` | 新建分享 | 浅色 | 同上 | 待分享列表 + 有效期 + 结果卡（含链接） |
| `settings-dark.png` | 设置 | 深色 | 同上 | 外观 / 网络 / 存储 三张卡 |
| `web-upload.png` | 网页端首页 | — | 手机宽度 430 px | 上传区 + 有效期选择 |

## 截图要求

- **不要最大化窗口**，保留圆角与一像素描边——最大化后看不出自绘标题栏的效果
- 浅色 / 深色各截一次，用来验证「没有白底控件、没有看不清的文字」
- 网页端用手机宽度（430 px）截，验证响应式布局
- 图片用 PNG，不加边框、不加阴影、不加标注

## 当前已有的图

上表 6 张**已经出好并入库**，README 的「截图」一节直接引用它们：

| 文件 | 尺寸 | 大小 | 说明 |
|---|---|---|---|
| `overview-dark.png` | 1120×780 | 29 KB | 离屏渲染，深色 |
| `overview-light.png` | 1120×780 | 29 KB | 离屏渲染，浅色 |
| `shares-dark.png` | 1120×780 | 16 KB | 离屏渲染，深色 |
| `settings-dark.png` | 1120×780 | 60 KB | 离屏渲染，深色 |
| `newshare-light.png` | 1120×780 | 34 KB | 离屏渲染，浅色 |
| `web-upload.png` | 645×697 | 154 KB | 网页端，手机宽度 |

## 注意

**不要用 `PrintWindow` API 截 WPF 窗口**——它返回的是缓存的表面，可能截到旧画面
（JavaDeck 那边连续三次截出的字节完全相同才发现）。

本项目实际用的是**离屏渲染** `RenderTargetBitmap`，原因是：

- 不受桌面干扰（无其他窗口、无鼠标、无通知气泡）
- 不依赖窗口是否可见，可在无头环境里批量出图
- 深浅主题各渲染一次即可，不用真的切主题再看一眼

另一条独立的坑：**`Popup` 是独立 HWND，`RenderTargetBitmap` 拍不到窗口里的下拉层**。
`ComboBox` 下拉、`ContextMenu`、`ToolTip` 必须单独渲染 `Popup.Child`，
详见 [../UI-DESIGN.md](../UI-DESIGN.md) 的「Popup 是独立 HWND，截图截不到」。

## 验证配套

截图本身不是验收标准，配合 [../UI-DESIGN.md](../UI-DESIGN.md) 的「验证方法」一节一起看：
`RenderTargetBitmap` 数像素 > 全屏截图 > 读控件属性（属性值 ≠ 渲染结果）。

## 重出方法

- **网页端**：`node scripts/static-preview.mjs` 起静态预览（`http://127.0.0.1:8899`），
  再用 `scripts/cdp-shot.mjs` 通过 Chrome DevTools Protocol 出图（`PAGE_URL` / `OUT` 环境变量控制）。
- **桌面端**：没有独立的出图脚本，是在验收流程里用 `RenderTargetBitmap` 离屏渲染的
  （截图时的临时工程不入库）。重出时按上表文件名覆盖即可，README 不用改。
