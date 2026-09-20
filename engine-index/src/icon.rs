//! 图标提取与缓存（M4）。
//!
//! **只产一档 256×256 PNG 字节**（2026-09-14 用户硬约束）：不做「用多大就申请多大」的多档图标，
//! 显示端一律按目标尺寸倍缩。收益：引擎只提取一次、只缓存一份、缓存上界可估。
//!
//! **内存纪律**：缓存里驻留的是**编码后的 PNG 字节**（≈5–40KB/张），**不是**解码后的 RGBA
//! （256×256 RGBA = 256KB/张，300 张即 ≈76MB）。解码与缩放都留在 C# 侧（`DecodePixelWidth`）。
//!
//! **提取链**（与 C# `HighResIconExtractor.cs` 逐条对齐，7444 契约 A / Open-Shell ResourceHelper 移植）：
//!   ① `SHExtractIconsW` 是 shell32 **私有导出** → 必须 `GetProcAddress` 动态解析（静态导入入口点不存在）；
//!   ② 私有导出缺失 / 提取失败 → 降级公开 `ExtractIconExW`（降级链是契约的一部分）；
//!   ③ `SHExtractIconsW` 返回 0 = 提取失败（**不是**图标数——错误写法 4）；
//!   ④ 签名含 pid 出参 + flags（比 `ExtractIconExW` 多 pid）；
//!   ⑤ 函数指针静态缓存，只解析一次；
//!   ⑥ `HICON` 用后必须 `DestroyIcon`（本模块用 `IconHandle` 的 `Drop` 保证**所有**返回路径都释放）。
//!
//! **失败即 None**：不返回占位字节，由调用方走 glyph 兜底（M10 禁止静默）。

use std::collections::{HashMap, VecDeque};
use std::ffi::c_void;
use std::sync::{Mutex, OnceLock};

use windows::core::{s, w, PCWSTR};
use windows::Win32::Foundation::HANDLE;
use windows::Win32::Graphics::Gdi::{
    CreateCompatibleDC, CreateDIBSection, DeleteDC, DeleteObject, SelectObject, BITMAPINFO,
    BITMAPINFOHEADER, BI_RGB, DIB_RGB_COLORS, HBITMAP, HBRUSH, HDC, HGDIOBJ,
};
use windows::Win32::System::LibraryLoader::{GetModuleHandleW, GetProcAddress};
use windows::Win32::UI::Shell::ExtractIconExW;
use windows::Win32::UI::WindowsAndMessaging::{DestroyIcon, DrawIconEx, DI_NORMAL, HICON};

/// 空句柄构造：windows-rs 0.58 的句柄类型（`pub struct HDC(pub *mut c_void)` 等）
/// **不实现 `Default`**，传「无父 DC / 无画刷」只能显式给空指针。
fn null_hdc() -> HDC {
    HDC(std::ptr::null_mut())
}

fn null_brush() -> HBRUSH {
    HBRUSH(std::ptr::null_mut())
}

fn null_icon() -> HICON {
    HICON(std::ptr::null_mut())
}

fn null_handle() -> HANDLE {
    HANDLE(std::ptr::null_mut())
}

/// 句柄是否有效：空指针与 `(HANDLE)-1` 都是无效值（与 `is_invalid` 同判定，
/// 但这里不依赖各句柄类型是否都生成了 `is_invalid`）。
fn is_valid(h: *mut std::ffi::c_void) -> bool {
    !h.is_null() && h as isize != -1
}

/// 唯一图标尺寸（用户硬约束：只取一档大图）。
pub const ICON_SIZE: i32 = 256;

/// 图标字节缓存上限（§5.4 内存治理：常驻进程必须自管，不进 `IResourceGovernor` 域）。
///
/// 口径：PNG 字节 ≈5–40KB/张 → 32MB ≈ 800–6500 张，足够覆盖 dock + 开始菜单 + 应用提取器
/// 的可见区，同时给引擎留出明确的硬上界（不再是「用多大申请多大」的无界增长）。
pub const MAX_CACHE_BYTES: usize = 32 * 1024 * 1024;

/// 单张超过上限的 1/4 → **不缓存**（只本次返回）。
///
/// 为什么需要：一张异常大的图会把缓存整体冲空（淘汰掉几十张正常图），
/// 而它自己也留不住——净效果是缓存反复失效。宁可这一张每次都重取。
const MAX_SINGLE_CACHE_BYTES: usize = MAX_CACHE_BYTES / 4;

/// LRU 图标缓存（复刻 502「多尺寸 LRU」的**淘汰语义**；尺寸维度按用户硬约束收敛为一档）。
///
/// 纯数据结构、无 Windows 依赖 → 可直接单测淘汰行为。
pub struct IconCache {
    cap_bytes: usize,
    bytes: usize,
    map: HashMap<String, Vec<u8>>,
    order: VecDeque<String>,
}

impl IconCache {
    pub fn new(cap_bytes: usize) -> Self {
        Self {
            cap_bytes,
            bytes: 0,
            map: HashMap::new(),
            order: VecDeque::new(),
        }
    }

    /// 命中：返回字节副本并**提升为最近使用**（LRU 的 L）。
    pub fn get(&mut self, key: &str) -> Option<Vec<u8>> {
        let hit = self.map.get(key)?.clone();
        self.touch(key);
        Some(hit)
    }

    /// 写入并按需淘汰。
    pub fn put(&mut self, key: String, png: Vec<u8>) {
        // 【2026-09-14 修复】记账改为用**本次插入值的长度**，不再回查 `self.order.back()`。
        // 旧写法依赖「touch/push_back 之后 key 必在队尾」这一隐式不变量，且带
        // `expect("just pushed")` —— 本函数经 `png_bytes` 在 **IPC 连接线程**内被调用，
        // 一旦该不变量被破坏（panic）会 unwind 掉整条连接线程，4 个连接槽会被逐步耗尽，
        // 引擎"看着活着"但谁也连不上。记账本不需要任何不变量。
        let len = png.len();
        if len > self.cap_bytes {
            return;
        }

        if let Some(old) = self.map.insert(key.clone(), png) {
            // 同键覆盖：先扣旧字节，避免重复计数。
            self.bytes = self.bytes.saturating_sub(old.len());
            self.touch(&key);
        } else {
            self.order.push_back(key);
        }

        self.bytes = self.bytes.saturating_add(len);
        self.evict_over_cap();
    }

    /// 只读统计 `(条目数, 字节数)`，**不提升 recency** —— `status` 查询不得改变淘汰顺序。
    pub fn stats(&self) -> (usize, usize) {
        (self.map.len(), self.bytes)
    }

    pub fn clear(&mut self) {
        self.map.clear();
        self.order.clear();
        self.bytes = 0;
    }

    fn touch(&mut self, key: &str) {
        // position() 未命中直接返回；命中则移出并移到队尾。
        // 不用 `expect`：本函数经 `png_bytes` 在 IPC 连接线程上被调用，panic 会 unwind 掉整条
        // 连接线程（4 个连接槽会被逐步耗尽，引擎"看着活着"但谁也连不上）。
        let pos = match self.order.iter().position(|k| k == key) {
            Some(pos) => pos,
            None => return,
        };
        if let Some(k) = self.order.remove(pos) {
            self.order.push_back(k);
        }
    }

    /// 从队首（最久未用）开始淘汰，直到字节数回到上限内。
    fn evict_over_cap(&mut self) {
        while self.bytes > self.cap_bytes {
            let Some(oldest) = self.order.pop_front() else {
                // 兜底：order 与 map 脱钩时不能再死循环（清空优于卡死）。
                self.clear();
                return;
            };
            if let Some(removed) = self.map.remove(&oldest) {
                self.bytes = self.bytes.saturating_sub(removed.len());
            }
        }
    }
}

// ---------------------------------------------------------------- 全局入口

static CACHE: OnceLock<Mutex<IconCache>> = OnceLock::new();

fn cache() -> &'static Mutex<IconCache> {
    CACHE.get_or_init(|| Mutex::new(IconCache::new(MAX_CACHE_BYTES)))
}

/// 取指定路径的 256×256 PNG 图标字节（命中缓存直接返回；提取失败返回 `None`）。
///
/// `key` 同时作为缓存键：调用方传**稳定**的键（目标 exe 路径优先；UWP 传 AUMID），
/// 不要传会变的临时路径，否则缓存命中率归零。
pub fn png_bytes(key: &str) -> Option<Vec<u8>> {
    if key.trim().is_empty() {
        return None;
    }

    let normalized = normalize_key(key);
    if let Some(hit) = lock().get(&normalized) {
        return Some(hit);
    }

    let png = extract_png(key)?;

    // 超大单张不缓存（见 MAX_SINGLE_CACHE_BYTES 说明），避免冲空缓存。
    if png.len() <= MAX_SINGLE_CACHE_BYTES {
        lock().put(normalized, png.clone());
    }

    Some(png)
}

/// 缓存统计（`(条目数, 字节数)`）——供 `status` 上报（§5.4 内存治理 / M5 状态行）。
pub fn cache_stats() -> (usize, usize) {
    lock().stats()
}

/// 清空缓存（设置变更 / 测试）。
pub fn clear_cache() {
    lock().clear();
}

fn lock() -> std::sync::MutexGuard<'static, IconCache> {
    cache().lock().unwrap_or_else(|e| e.into_inner())
}

/// 缓存键归一：Windows 路径大小写不敏感，统一小写避免同一程序两份缓存。
fn normalize_key(key: &str) -> String {
    key.trim().to_lowercase()
}

// ---------------------------------------------------------------- 提取

/// 提取 → PNG。任一步失败返回 `None`（调用方 glyph 兜底）。
fn extract_png(path: &str) -> Option<Vec<u8>> {
    let hicon = extract_hicon(path)?;
    let guard = IconHandle(hicon);

    let rgba = hicon_to_rgba(guard.0, ICON_SIZE)?;
    encode_png(rgba, ICON_SIZE as u32)
}

/// `HICON` 所有权守卫：**所有**返回路径都会 DestroyIcon（红线 6）。
struct IconHandle(HICON);

impl Drop for IconHandle {
    fn drop(&mut self) {
        unsafe {
            let _ = DestroyIcon(self.0);
        }
    }
}

/// shell32 私有导出 `SHExtractIconsW` 的函数签名（红线 4：含 pid 出参 + flags）。
type SHExtractIconsWFn =
    unsafe extern "system" fn(PCWSTR, i32, i32, i32, *mut HICON, *mut u32, u32, u32) -> u32;

/// 解析私有导出（红线 1/5：GetProcAddress 动态解析 + 结果静态缓存，只解析一次）。
///
/// 返回 `None` = 该导出在当前系统上不存在 → 调用方走公开降级链（红线 2）。
fn private_extract() -> Option<SHExtractIconsWFn> {
    static FN: OnceLock<Option<SHExtractIconsWFn>> = OnceLock::new();

    *FN.get_or_init(|| unsafe {
        let module = GetModuleHandleW(w!("shell32.dll")).ok()?;
        let proc = GetProcAddress(module, s!("SHExtractIconsW"))?;
        Some(std::mem::transmute::<
            unsafe extern "system" fn() -> isize,
            SHExtractIconsWFn,
        >(proc))
    })
}

/// 提取链：私有 `SHExtractIconsW(256)` → 公开 `ExtractIconExW`。
fn extract_hicon(path: &str) -> Option<HICON> {
    let wide: Vec<u16> = path.encode_utf16().chain(std::iter::once(0)).collect();
    let pcwstr = PCWSTR(wide.as_ptr());

    if let Some(fn_ptr) = private_extract() {
        let mut hicon = null_icon();
        let mut pid: u32 = 0;
        // 红线 3：返回 0 = 提取失败（不是图标数），此时 hicon 无效、不可 Destroy。
        let extracted =
            unsafe { fn_ptr(pcwstr, 0, ICON_SIZE, ICON_SIZE, &mut hicon, &mut pid, 1, 0) };
        if extracted != 0 && is_valid(hicon.0) {
            return Some(hicon);
        }
    }

    // 降级：公开 API 取大图标（红线 2 的回退半边）。返回 1 = 成功。
    let mut large = null_icon();
    let count = unsafe { ExtractIconExW(pcwstr, 0, Some(&mut large as *mut HICON), None, 1) };
    if count >= 1 && is_valid(large.0) {
        return Some(large);
    }

    None
}

/// `HICON` → 直通 alpha 的 RGBA 字节（`size × size × 4`）。
///
/// 做法：建 32bpp **top-down** DIB section → `DrawIconEx(DI_NORMAL)` 把图标（含掩码）画进去
/// → 读回 BGRA。`DrawIconEx` 写出的是**预乘 alpha**，故需要反预乘还原直通 alpha；
/// 若整图 alpha 全 0（图标无 alpha 通道，仅靠掩码）→ 按不透明处理。
fn hicon_to_rgba(hicon: HICON, size: i32) -> Option<Vec<u8>> {
    if size <= 0 {
        return None;
    }

    unsafe {
        let mem_dc: HDC = CreateCompatibleDC(null_hdc());
        if !is_valid(mem_dc.0) {
            return None;
        }

        let mut bmi = BITMAPINFO::default();
        bmi.bmiHeader.biSize = std::mem::size_of::<BITMAPINFOHEADER>() as u32;
        bmi.bmiHeader.biWidth = size;
        // 负高度 = top-down：位图首行就是图像首行，省一次翻转。
        bmi.bmiHeader.biHeight = -size;
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        // 0.58 里 `biCompression` 是裸 u32（不是 BI_COMPRESSION 新类型）→ 取内值。
        bmi.bmiHeader.biCompression = BI_RGB.0;

        let mut bits: *mut c_void = std::ptr::null_mut();
        let dib: HBITMAP =
            match CreateDIBSection(mem_dc, &bmi, DIB_RGB_COLORS, &mut bits, null_handle(), 0) {
                Ok(b) if is_valid(b.0) && !bits.is_null() => b,
                _ => {
                    let _ = DeleteDC(mem_dc);
                    return None;
                }
            };

        let old = SelectObject(mem_dc, HGDIOBJ(dib.0));

        let drawn = DrawIconEx(mem_dc, 0, 0, hicon, size, size, 0, null_brush(), DI_NORMAL).is_ok();

        // 【生死线】必须在**删除 DIB 之前**把像素拷出来：`bits` 指向 DIB section 自身的内存，
        // DeleteObject 之后该内存即失效 —— 先删后读是 use-after-free（真机表现为测试进程直接
        // 崩掉、没有任何断言输出，2026-09-14 实测踩到）。
        let pixels = if drawn {
            let len = (size as usize) * (size as usize) * 4;
            Some(std::slice::from_raw_parts(bits as *const u8, len).to_vec())
        } else {
            None
        };

        // 先还原 DC 旧对象再删位图（顺序反了会在某些 GDI 实现上泄漏选中的位图）。
        if is_valid(old.0) {
            SelectObject(mem_dc, old);
        }
        let _ = DeleteObject(HGDIOBJ(dib.0));
        let _ = DeleteDC(mem_dc);

        let pixels = pixels?;
        Some(bgra_premultiplied_to_rgba(&pixels))
    }
}

/// BGRA（预乘）→ RGBA（直通）。整图 alpha 全 0 时按不透明处理（掩码图标的常见情形）。
fn bgra_premultiplied_to_rgba(src: &[u8]) -> Vec<u8> {
    let mut out = vec![0u8; src.len()];
    let mut any_alpha = false;

    for (s, d) in src.chunks_exact(4).zip(out.chunks_exact_mut(4)) {
        let (b, g, r, a) = (s[0], s[1], s[2], s[3]);
        if a == 0 {
            // 预乘下 a=0 表示完全透明：像素值无意义，写 0。
            d[0] = 0;
            d[1] = 0;
            d[2] = 0;
            d[3] = 0;
            continue;
        }

        any_alpha = true;
        // 反预乘：c_straight = c_premult * 255 / a（饱和到 255）。
        d[0] = unpremultiply(r, a);
        d[1] = unpremultiply(g, a);
        d[2] = unpremultiply(b, a);
        d[3] = a;
    }

    if !any_alpha {
        // 整图无 alpha 信息 → 全部不透明（否则会渲染成一张全透明的空图）。
        for d in out.chunks_exact_mut(4) {
            d[3] = 255;
        }
    }

    out
}

fn unpremultiply(channel: u8, alpha: u8) -> u8 {
    let value = (channel as u32 * 255) / alpha as u32;
    value.min(255) as u8
}

/// RGBA → PNG 字节（与 `engine/src/thumb.rs` 同一编码 idiom）。
fn encode_png(rgba: Vec<u8>, size: u32) -> Option<Vec<u8>> {
    let img = image::RgbaImage::from_raw(size, size, rgba)?;
    let mut out = Vec::new();
    image::DynamicImage::ImageRgba8(img)
        .write_to(
            &mut std::io::Cursor::new(&mut out),
            image::ImageFormat::Png,
        )
        .ok()?;
    Some(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    // ---------------------------------------------------------- LRU（纯数据，无 Windows 依赖）

    #[test]
    fn cache_evicts_oldest_when_over_cap() {
        let mut c = IconCache::new(10);
        c.put("a".into(), vec![0u8; 4]);
        c.put("b".into(), vec![0u8; 4]);
        assert_eq!(c.stats(), (2, 8));

        // 第三个 4 字节 → 超上限 → 淘汰最久未用的 a。
        c.put("c".into(), vec![0u8; 4]);
        assert_eq!(c.stats(), (2, 8));
        assert!(c.get("a").is_none(), "最久未用的 a 必须被淘汰");
        assert!(c.get("b").is_some());
        assert!(c.get("c").is_some());
    }

    #[test]
    fn cache_get_promotes_recency() {
        let mut c = IconCache::new(10);
        c.put("a".into(), vec![0u8; 4]);
        c.put("b".into(), vec![0u8; 4]);

        // 读 a → a 变最近使用 → 接下来应淘汰 b（而不是 a）。
        assert!(c.get("a").is_some());
        c.put("c".into(), vec![0u8; 4]);

        assert!(c.get("a").is_some(), "被读过的 a 不应再是最久未用");
        assert!(c.get("b").is_none(), "b 成了最久未用，应被淘汰");
        assert!(c.get("c").is_some());
    }

    #[test]
    fn cache_overwrites_same_key_without_double_counting() {
        let mut c = IconCache::new(100);
        c.put("a".into(), vec![0u8; 8]);
        c.put("a".into(), vec![0u8; 3]);
        assert_eq!(c.stats(), (1, 3), "同键覆盖必须扣掉旧字节，否则计数虚高提前淘汰");
    }

    #[test]
    fn cache_rejects_single_entry_larger_than_cap() {
        let mut c = IconCache::new(10);
        c.put("big".into(), vec![0u8; 11]);
        assert_eq!(c.stats(), (0, 0), "超过总上限的单张直接不收（收了也立刻被自己挤掉）");
    }

    #[test]
    fn cache_clear_resets_counters() {
        let mut c = IconCache::new(100);
        c.put("a".into(), vec![0u8; 8]);
        c.clear();
        assert_eq!(c.stats(), (0, 0));
    }

    // ---------------------------------------------------------- alpha 还原（纯函数）

    #[test]
    fn rgba_conversion_unpremultiplies() {
        // 预乘后的半透明红：alpha=128, r=128 → 直通 r=255。
        let src = [0u8, 0, 128, 128];
        let out = bgra_premultiplied_to_rgba(&src);
        assert_eq!(out, vec![255, 0, 0, 128]);
    }

    #[test]
    fn rgba_conversion_marks_transparent_pixel_as_zero_rgb() {
        // 只要**存在**不透明像素，就说明这张图有 alpha 信息 → 透明像素必须被清成 0
        // （否则透明区会残留预乘前的颜色，缩小时渗出彩边）。
        let src = [200u8, 100, 50, 0, 0, 0, 0, 255];
        let out = bgra_premultiplied_to_rgba(&src);
        assert_eq!(&out[0..4], &[0, 0, 0, 0], "透明像素必须清零");
        assert_eq!(&out[4..8], &[0, 0, 0, 255]);
    }

    #[test]
    fn rgba_conversion_treats_all_zero_alpha_as_opaque() {
        // 掩码图标（无 alpha 通道）经 DrawIconEx 后常见整图 alpha=0：必须兜成不透明，
        // 否则渲染出一张全透明空图（用户看到「图标没了」）。
        let src = [0u8, 0, 255, 0, 0, 255, 0, 0];
        let out = bgra_premultiplied_to_rgba(&src);
        assert_eq!(out[3], 255);
        assert_eq!(out[7], 255);
    }

    #[test]
    fn rgba_conversion_keeps_opaque_pixels() {
        let src = [10u8, 20, 30, 255];
        let out = bgra_premultiplied_to_rgba(&src);
        assert_eq!(out, vec![30, 20, 10, 255]);
    }

    // ---------------------------------------------------------- 键归一

    #[test]
    fn key_normalization_is_case_insensitive() {
        assert_eq!(
            normalize_key(r"C:\Windows\System32\NOTEPAD.EXE"),
            normalize_key(r"c:\windows\system32\notepad.exe")
        );
    }

    // ---------------------------------------------------------- 真机提取（Windows 上恒可用）

    #[test]
    fn png_bytes_from_system_binary_is_valid_256_png() {
        let path = r"C:\Windows\System32\notepad.exe";
        if !std::path::Path::new(path).exists() {
            // 非 Windows / 精简系统：不发假通过，直接跳过。
            return;
        }

        let png = png_bytes(path).expect("系统自带 exe 必须能取到图标");
        assert!(
            png.starts_with(&[0x89, b'P', b'N', b'G']),
            "必须是 PNG 字节（PNG magic）"
        );

        let decoded = image::load_from_memory(&png).expect("PNG 必须可解码");
        assert_eq!(decoded.width(), ICON_SIZE as u32);
        assert_eq!(decoded.height(), ICON_SIZE as u32, "只产一档 256×256");
    }

    #[test]
    fn png_bytes_is_cached_on_second_call() {
        let path = r"C:\Windows\System32\notepad.exe";
        if !std::path::Path::new(path).exists() {
            return;
        }

        clear_cache();
        let first = png_bytes(path).expect("首次提取");
        assert_eq!(cache_stats().0, 1, "首次必须落缓存");
        let second = png_bytes(path).expect("第二次命中缓存");
        assert_eq!(first, second);
        assert_eq!(cache_stats().0, 1, "同键不得产生第二份缓存");
    }

    #[test]
    fn missing_file_returns_none_without_panic() {
        clear_cache();
        assert!(png_bytes(r"C:\__no_such_dir__\__no_such_app__.exe").is_none());
        assert!(png_bytes("   ").is_none(), "空键直接 None，不去问系统");
    }
}
