# shell-music

音乐平台 API 基础设施包：提供酷狗音乐（Kugou）平台的元数据查询契约与实现，供未来桌宠/灵动岛等 UI 消费方使用。当前无 UI 消费方，仅提供服务层。

## 职责

- 定义 `IKugouMusicApi` 契约：歌曲搜索、播放信息获取、播放链接解析（含签名 key = MD5(hash + SIGN_KEY + appid + mid + userid)）、歌词获取（krcs search → download 链）。
- 实现 `KugouMusicApi`：基于 HttpClient 调用酷狗公开 API 端点，返回原始 JSON 字符串。
- 返回原始 JSON 而非固化 DTO：平台字段随版本漂移，提前固化 DTO 会随平台升级腐化；解析交由消费方按需处理（System.Text.Json）。

## 依赖

- `BetterDesktop.Kernel`

## 扩展点

- `IKugouMusicApi`：可新增其他音乐平台（QQ 音乐、网易云等）的同构契约，或替换实现以接入不同 API 版本。
- 消费方可通过内核服务图获取 `IKugouMusicApi`，自行解析 JSON 并构建 UI。

## Known Limitations

- 当前仅实现酷狗音乐平台，未覆盖 QQ 音乐、网易云音乐等其他平台。
- 仅提供元数据/搜索/播放链接/歌词查询，无 DRM 解密、无 VIP 绕过、无登录态、无音频下载缓存（合规红线）。
- 无 UI 消费方，服务加载后不产生可见功能；需桌宠/灵动岛等插件接入后才对用户可见。
- API 端点与签名算法依赖酷狗平台公开接口，平台变更可能导致调用失败，无自动降级或备用端点。
- 不使用 WPF（`<UseWPF>false</UseWPF>`），纯服务层包，无窗口或控件。
