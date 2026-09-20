//! 音视频元数据提取：运行时加载 FFmpeg dll，手写最小 FFI。
//!
//! 不做转码，只读：时长、流数量、每流编码类型/分辨率/采样率/码率。
//! 结构体字段偏移直接按 FFmpeg 9.x 头文件手工对齐（64-bit Windows）。

use std::ffi::{c_char, c_int, c_void, CString};
use std::path::{Path, PathBuf};

use libloading::{Library, Symbol};

// ---- 函数指针类型 ----

type AvformatOpenInput = unsafe extern "C" fn(
    *mut *mut AVFormatContext,
    *const c_char,
    *mut c_void,
    *mut c_void,
) -> c_int;
type AvformatFindStreamInfo = unsafe extern "C" fn(*mut AVFormatContext, *mut *mut AVDictionary) -> c_int;
type AvformatCloseInput = unsafe extern "C" fn(*mut *mut AVFormatContext);
type AvformatAllocContext = unsafe extern "C" fn() -> *mut AVFormatContext;
type AvformatFreeContext = unsafe extern "C" fn(*mut AVFormatContext);

// 不透明类型（只用于类型标记，不访问内部）
#[repr(C)]
pub struct AVFormatContext {
    _priv: [u8; 0],
}
#[repr(C)]
pub struct AVStream {
    _priv: [u8; 0],
}
#[repr(C)]
pub struct AVCodecParameters {
    _priv: [u8; 0],
}
#[repr(C)]
pub struct AVDictionary {
    _priv: [u8; 0],
}

pub struct AvLib {
    _avutil: Library,
    _avcodec: Library,
    _avformat: Library,
    open_input: AvformatOpenInput,
    find_stream_info: AvformatFindStreamInfo,
    close_input: AvformatCloseInput,
    alloc_context: AvformatAllocContext,
    free_context: AvformatFreeContext,
}

/// 在 exe 同目录或 third_party 找 ffmpeg bin。
fn find_ffmpeg_bin() -> Result<PathBuf, String> {
    let exe_dir = std::env::current_exe()
        .map_err(|e| format!("exe 路径失败：{e}"))?
        .parent()
        .ok_or("exe 无父目录")?
        .to_path_buf();

    // 候选：exe 同目录（部署时 dll 放这里），third_party 解压目录
    let candidates = [
        exe_dir.join("bin"),
        PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("third_party")
            .join("ffmpeg-master-latest-win64-gpl-shared")
            .join("bin"),
    ];
    for c in &candidates {
        if c.join("avformat-63.dll").exists() {
            return Ok(c.clone());
        }
    }
    Err("找不到 FFmpeg dll（avformat-63.dll）。请把 ffmpeg bin 目录放在 exe 同目录或 third_party 下".to_string())
}

impl AvLib {
    pub fn load() -> Result<Self, String> {
        let bin = find_ffmpeg_bin()?;
        // libloading 需要在 DLL 搜索路径里找到依赖 dll
        std::env::set_var(
            "PATH",
            format!("{};{}", bin.display(), std::env::var("PATH").unwrap_or_default()),
        );

        let avutil = unsafe { Library::new(bin.join("avutil-61.dll")) }
            .map_err(|e| format!("加载 avutil-61.dll 失败：{e}"))?;
        let avcodec = unsafe { Library::new(bin.join("avcodec-63.dll")) }
            .map_err(|e| format!("加载 avcodec-63.dll 失败：{e}"))?;
        let avformat = unsafe { Library::new(bin.join("avformat-63.dll")) }
            .map_err(|e| format!("加载 avformat-63.dll 失败：{e}"))?;

        macro_rules! sym {
            ($lib:expr, $name:expr, $ty:ty) => {
                unsafe {
                    *$lib
                        .get::<$ty>($name.as_bytes())
                        .map_err(|e| format!("找不到符号 {}：{e}", $name))?
                }
            };
        }

        let open_input = sym!(avformat, "avformat_open_input", AvformatOpenInput);
        let find_stream_info = sym!(avformat, "avformat_find_stream_info", AvformatFindStreamInfo);
        let close_input = sym!(avformat, "avformat_close_input", AvformatCloseInput);
        let alloc_context = sym!(avformat, "avformat_alloc_context", AvformatAllocContext);
        let free_context = sym!(avformat, "avformat_free_context", AvformatFreeContext);

        Ok(Self {
            _avutil: avutil,
            _avcodec: avcodec,
            _avformat: avformat,
            open_input,
            find_stream_info,
            close_input,
            alloc_context,
            free_context,
        })
    }

    /// 读取音视频文件元数据。
    pub fn probe(&self, path: &Path) -> Result<serde_json::Value, String> {
        let path_c = CString::new(path.to_string_lossy().as_ref())
            .map_err(|e| format!("路径含 NUL：{e}"))?;

        unsafe {
            let mut ic: *mut AVFormatContext = (self.alloc_context)();
            if ic.is_null() {
                return Err("avformat_alloc_context 失败".to_string());
            }

            let rc = (self.open_input)(&mut ic, path_c.as_ptr(), std::ptr::null_mut(), std::ptr::null_mut());
            if rc < 0 {
                (self.free_context)(ic);
                return Err(format!("avformat_open_input 失败（rc={rc}），不是音视频文件或格式不支持"));
            }

            // 找流信息
            (self.find_stream_info)(ic, std::ptr::null_mut());

            // 读字段（按 FFmpeg 9.x 头文件偏移，64-bit）
            let nb_streams = std::ptr::read((ic as *const u8).add(44) as *const u32) as usize;

            let streams_arr: *const *const AVStream = std::ptr::read((ic as *const u8).add(48) as *const *const *const AVStream);

            let duration_us = std::ptr::read((ic as *const u8).add(104) as *const i64);
            let duration_s = if duration_us > 0 { duration_us as f64 / 1_000_000.0 } else { 0.0 };

            let bit_rate = std::ptr::read((ic as *const u8).add(112) as *const i64);

            let mut streams_json = Vec::new();
            for i in 0..nb_streams {
                let stream = *streams_arr.add(i);
                if stream.is_null() {
                    continue;
                }
                let par = std::ptr::read((stream as *const u8).add(16) as *const *mut AVCodecParameters);
                if par.is_null() {
                    continue;
                }
                let p = par as *const u8;
                let codec_type = std::ptr::read(p.add(0) as *const i32);
                let codec_id = std::ptr::read(p.add(4) as *const i32);
                let width = std::ptr::read(p.add(72) as *const i32);
                let height = std::ptr::read(p.add(76) as *const i32);
                let par_bit_rate = std::ptr::read(p.add(48) as *const i64);

                let type_str = match codec_type {
                    0 => "video",
                    1 => "audio",
                    2 => "subtitle",
                    3 => "attachment",
                    _ => "other",
                };

                let mut st = serde_json::json!({
                    "index": i,
                    "type": type_str,
                    "codec_id": codec_id,
                });
                if codec_type == 0 {
                    st["width"] = serde_json::json!(width);
                    st["height"] = serde_json::json!(height);
                } else if codec_type == 1 {
                    let sample_rate = std::ptr::read(p.add(152) as *const i32);
                    let channels = std::ptr::read(p.add(132) as *const i32);
                    if sample_rate > 0 { st["sample_rate"] = serde_json::json!(sample_rate); }
                    if channels > 0 { st["channels"] = serde_json::json!(channels); }
                }
                if par_bit_rate > 0 {
                    st["bit_rate"] = serde_json::json!(par_bit_rate);
                }
                streams_json.push(st);
            }

            let mut obj = serde_json::Map::new();
            obj.insert("duration_sec".to_string(), serde_json::json!(duration_s));
            if bit_rate > 0 {
                obj.insert("bit_rate".to_string(), serde_json::json!(bit_rate));
            }
            obj.insert("streams".to_string(), serde_json::Value::Array(streams_json));

            (self.close_input)(&mut ic);

            Ok(serde_json::Value::Object(obj))
        }
    }
}

pub fn av_probe(path: &Path) -> Result<serde_json::Value, String> {
    let lib = AvLib::load()?;
    lib.probe(path)
}

// ==== 抽帧：视频第一帧 → JPEG ====

#[repr(C)]
pub struct AVCodecContext { _priv: [u8; 0] }
pub struct AVIOContext { _priv: [u8; 0] }
#[repr(C)]
pub struct AVPacket { _priv: [u8; 0] }
#[repr(C)]
pub struct AVFrame { _priv: [u8; 0] }
#[repr(C)]
pub struct AVCodec { _priv: [u8; 0] }
#[repr(C)]
pub struct SwsContext { _priv: [u8; 0] }

type FindDecoder = unsafe extern "C" fn(c_int) -> *const AVCodec;
type AllocCodecCtx = unsafe extern "C" fn(*const AVCodec) -> *mut AVCodecContext;
type ParamsToCtx = unsafe extern "C" fn(*mut AVCodecContext, *const AVCodecParameters) -> c_int;
type OpenCodec2 = unsafe extern "C" fn(*mut AVCodecContext, *const AVCodec, *mut c_void) -> c_int;
type FreeCodecCtx = unsafe extern "C" fn(*mut *mut AVCodecContext);
type ReadFrame = unsafe extern "C" fn(*mut AVFormatContext, *mut AVPacket) -> c_int;
type PacketAlloc = unsafe extern "C" fn() -> *mut AVPacket;
type PacketFree = unsafe extern "C" fn(*mut *mut AVPacket);
type FrameAlloc = unsafe extern "C" fn() -> *mut AVFrame;
type FrameFree = unsafe extern "C" fn(*mut *mut AVFrame);
type SendPacket = unsafe extern "C" fn(*mut AVCodecContext, *const AVPacket) -> c_int;
type ReceiveFrame = unsafe extern "C" fn(*mut AVCodecContext, *mut AVFrame) -> c_int;
type SwsGetCtx = unsafe extern "C" fn(c_int, c_int, c_int, c_int, c_int, c_int, c_int, *mut c_void, *mut c_void, *const f64) -> *mut SwsContext;
type SwsScale = unsafe extern "C" fn(*mut SwsContext, *const *const u8, *const c_int, c_int, c_int, *const *mut u8, *const c_int) -> c_int;
type SwsFreeCtx = unsafe extern "C" fn(*mut SwsContext);

/// 抽视频第一帧为 JPEG。
pub fn av_extract_frame(path: &Path) -> Result<Vec<u8>, String> {
    let bin = find_ffmpeg_bin()?;
    std::env::set_var(
        "PATH",
        format!("{};{}", bin.display(), std::env::var("PATH").unwrap_or_default()),
    );

    let _avutil = unsafe { Library::new(bin.join("avutil-61.dll")) }.map_err(|e| e.to_string())?;
    let avcodec = unsafe { Library::new(bin.join("avcodec-63.dll")) }.map_err(|e| e.to_string())?;
    let avformat = unsafe { Library::new(bin.join("avformat-63.dll")) }.map_err(|e| e.to_string())?;
    let swscale = unsafe { Library::new(bin.join("swscale-10.dll")) }.map_err(|e| e.to_string())?;

    macro_rules! s {
        ($lib:expr, $name:expr, $ty:ty) => {
            unsafe {
                *$lib.get::<$ty>($name.as_bytes())
                    .map_err(|e| format!("找不到符号 {}：{e}", $name))?
            }
        };
    }

    let open_input = s!(avformat, "avformat_open_input", AvformatOpenInput);
    let find_stream_info = s!(avformat, "avformat_find_stream_info", AvformatFindStreamInfo);
    let close_input = s!(avformat, "avformat_close_input", AvformatCloseInput);
    let alloc_fmt = s!(avformat, "avformat_alloc_context", AvformatAllocContext);
    let read_frame = s!(avformat, "av_read_frame", ReadFrame);

    let find_decoder = s!(avcodec, "avcodec_find_decoder", FindDecoder);
    let alloc_ctx = s!(avcodec, "avcodec_alloc_context3", AllocCodecCtx);
    let params_to_ctx = s!(avcodec, "avcodec_parameters_to_context", ParamsToCtx);
    let open2 = s!(avcodec, "avcodec_open2", OpenCodec2);
    let free_ctx = s!(avcodec, "avcodec_free_context", FreeCodecCtx);
    let pkt_alloc = s!(avcodec, "av_packet_alloc", PacketAlloc);
    let pkt_free = s!(avcodec, "av_packet_free", PacketFree);
    let frame_alloc = s!(_avutil, "av_frame_alloc", FrameAlloc);
    let frame_free = s!(_avutil, "av_frame_free", FrameFree);
    let send_pkt = s!(avcodec, "avcodec_send_packet", SendPacket);
    let recv_frame = s!(avcodec, "avcodec_receive_frame", ReceiveFrame);

    let sws_get = s!(swscale, "sws_getContext", SwsGetCtx);
    let sws_scale = s!(swscale, "sws_scale", SwsScale);
    let sws_free = s!(swscale, "sws_freeContext", SwsFreeCtx);

    let path_c = CString::new(path.to_string_lossy().as_ref()).map_err(|e| e.to_string())?;

    unsafe {
        let mut ic: *mut AVFormatContext = alloc_fmt();
        let rc = open_input(&mut ic, path_c.as_ptr(), std::ptr::null_mut(), std::ptr::null_mut());
        if rc < 0 { return Err(format!("open_input 失败 rc={rc}")); }
        find_stream_info(ic, std::ptr::null_mut());

        let nb_streams = std::ptr::read((ic as *const u8).add(44) as *const u32) as usize;
        let streams_arr: *const *const AVStream = std::ptr::read((ic as *const u8).add(48) as *const *const *const AVStream);

        let mut video_idx: isize = -1;
        let mut codecpar_ptr: *mut AVCodecParameters = std::ptr::null_mut();
        for i in 0..nb_streams {
            let stream = *streams_arr.add(i);
            if stream.is_null() { continue; }
            let par = std::ptr::read((stream as *const u8).add(16) as *const *mut AVCodecParameters);
            if par.is_null() { continue; }
            let codec_type = std::ptr::read((par as *const u8).add(0) as *const c_int);
            if codec_type == 0 {
                video_idx = i as isize;
                codecpar_ptr = par;
                break;
            }
        }
        if video_idx < 0 {
            close_input(&mut ic);
            return Err("没有视频流".to_string());
        }

        let codec_id = std::ptr::read((codecpar_ptr as *const u8).add(4) as *const c_int);
        let codec = find_decoder(codec_id);
        if codec.is_null() {
            close_input(&mut ic);
            return Err(format!("不支持的解码器 codec_id={codec_id}"));
        }

        let mut ctx = alloc_ctx(codec);
        if ctx.is_null() {
            close_input(&mut ic);
            return Err("alloc_context 失败".to_string());
        }
        params_to_ctx(ctx, codecpar_ptr);
        if open2(ctx, codec, std::ptr::null_mut()) < 0 {
            free_ctx(&mut ctx);
            close_input(&mut ic);
            return Err("open2 失败".to_string());
        }

        let pkt = pkt_alloc();
        let frame = frame_alloc();
        let mut jpeg_out: Option<Vec<u8>> = None;

        for _ in 0..100 {
            let rc = read_frame(ic, pkt);
            if rc < 0 { break; }
            // 不检查 stream_index（AVPacket 不透明），直接 send
            if send_pkt(ctx, pkt) < 0 { continue; }
            loop {
                let rc2 = recv_frame(ctx, frame);
                if rc2 < 0 { break; }
                let fw = std::ptr::read((frame as *const u8).add(104) as *const c_int);
                let fh = std::ptr::read((frame as *const u8).add(108) as *const c_int);
                let fmt = std::ptr::read((frame as *const u8).add(112) as *const c_int);
                let mut src_data: [*const u8; 8] = [std::ptr::null(); 8];
                let mut src_stride: [c_int; 8] = [0; 8];
                for k in 0..8 {
                    src_data[k] = std::ptr::read((frame as *const u8).add(k * 8) as *const *const u8);
                    src_stride[k] = std::ptr::read((frame as *const u8).add(64 + k * 4) as *const c_int);
                }
                let mut rgb = vec![0u8; (fw * 3 * fh) as usize];
                let dst_stride: [c_int; 8] = [fw * 3, 0, 0, 0, 0, 0, 0, 0];
                let mut dst_data: [*mut u8; 8] = [std::ptr::null_mut(); 8];
                dst_data[0] = rgb.as_mut_ptr();
                let sws = sws_get(fw, fh, fmt, fw, fh, 2, 2, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null());
                if !sws.is_null() {
                    sws_scale(sws, src_data.as_ptr(), src_stride.as_ptr(), 0, fh, dst_data.as_ptr() as *const *mut u8, dst_stride.as_ptr());
                    sws_free(sws);
                }
                let img = image::RgbImage::from_raw(fw as u32, fh as u32, rgb).ok_or("RgbImage::from_raw 失败")?;
                let mut buf = Vec::new();
                image::DynamicImage::ImageRgb8(img)
                    .write_to(&mut std::io::Cursor::new(&mut buf), image::ImageFormat::Jpeg)
                    .map_err(|e| e.to_string())?;
                jpeg_out = Some(buf);
                break;
            }
            if jpeg_out.is_some() { break; }
        }

        let mut p2 = pkt;
        pkt_free(&mut p2);
        let mut f2 = frame;
        frame_free(&mut f2);
        let mut c2 = ctx;
        free_ctx(&mut c2);
        close_input(&mut ic);

        jpeg_out.ok_or_else(|| "解码失败：没读到帧".to_string())
    }
}

// ==== 抽多帧：seek 到 0/50/95%，各抽一帧 ====

/// 抽多帧，返回输出文件路径列表。
pub fn av_extract_frames(path: &Path, out_prefix: &Path) -> Result<Vec<PathBuf>, String> {
    let bin = find_ffmpeg_bin()?;
    std::env::set_var("PATH", format!("{};{}", bin.display(), std::env::var("PATH").unwrap_or_default()));

    let _avutil = unsafe { Library::new(bin.join("avutil-61.dll")) }.map_err(|e| e.to_string())?;
    let avcodec = unsafe { Library::new(bin.join("avcodec-63.dll")) }.map_err(|e| e.to_string())?;
    let avformat = unsafe { Library::new(bin.join("avformat-63.dll")) }.map_err(|e| e.to_string())?;
    let swscale = unsafe { Library::new(bin.join("swscale-10.dll")) }.map_err(|e| e.to_string())?;

    macro_rules! s {
        ($lib:expr, $name:expr, $ty:ty) => {
            unsafe { *$lib.get::<$ty>($name.as_bytes()).map_err(|e| format!("找不到符号 {}：{e}", $name))? }
        };
    }

    let open_input = s!(avformat, "avformat_open_input", AvformatOpenInput);
    let find_stream_info = s!(avformat, "avformat_find_stream_info", AvformatFindStreamInfo);
    let close_input = s!(avformat, "avformat_close_input", AvformatCloseInput);
    let alloc_fmt = s!(avformat, "avformat_alloc_context", AvformatAllocContext);
    let read_frame = s!(avformat, "av_read_frame", ReadFrame);
    let seek_frame = s!(avformat, "av_seek_frame", unsafe extern "C" fn(*mut AVFormatContext, c_int, i64, c_int) -> c_int);

    let find_decoder = s!(avcodec, "avcodec_find_decoder", FindDecoder);
    let alloc_ctx = s!(avcodec, "avcodec_alloc_context3", AllocCodecCtx);
    let params_to_ctx = s!(avcodec, "avcodec_parameters_to_context", ParamsToCtx);
    let open2 = s!(avcodec, "avcodec_open2", OpenCodec2);
    let free_ctx = s!(avcodec, "avcodec_free_context", FreeCodecCtx);
    let pkt_alloc = s!(avcodec, "av_packet_alloc", PacketAlloc);
    let pkt_free = s!(avcodec, "av_packet_free", PacketFree);
    let frame_alloc = s!(_avutil, "av_frame_alloc", FrameAlloc);
    let frame_free = s!(_avutil, "av_frame_free", FrameFree);
    let send_pkt = s!(avcodec, "avcodec_send_packet", SendPacket);
    let recv_frame = s!(avcodec, "avcodec_receive_frame", ReceiveFrame);
    let flush = s!(avcodec, "avcodec_flush_buffers", unsafe extern "C" fn(*mut AVCodecContext));

    let sws_get = s!(swscale, "sws_getContext", SwsGetCtx);
    let sws_scale = s!(swscale, "sws_scale", SwsScale);
    let sws_free = s!(swscale, "sws_freeContext", SwsFreeCtx);

    let path_c = CString::new(path.to_string_lossy().as_ref()).map_err(|e| e.to_string())?;

    unsafe {
        let mut ic: *mut AVFormatContext = alloc_fmt();
        if open_input(&mut ic, path_c.as_ptr(), std::ptr::null_mut(), std::ptr::null_mut()) < 0 {
            return Err("open_input 失败".to_string());
        }
        find_stream_info(ic, std::ptr::null_mut());

        let nb_streams = std::ptr::read((ic as *const u8).add(44) as *const u32) as usize;
        let streams_arr: *const *const AVStream = std::ptr::read((ic as *const u8).add(48) as *const *const *const AVStream);
        let duration_us = std::ptr::read((ic as *const u8).add(104) as *const i64);

        let mut video_idx: isize = -1;
        let mut codecpar_ptr: *mut AVCodecParameters = std::ptr::null_mut();
        for i in 0..nb_streams {
            let stream = *streams_arr.add(i);
            if stream.is_null() { continue; }
            let par = std::ptr::read((stream as *const u8).add(16) as *const *mut AVCodecParameters);
            if par.is_null() { continue; }
            let ct = std::ptr::read((par as *const u8).add(0) as *const c_int);
            if ct == 0 { video_idx = i as isize; codecpar_ptr = par; break; }
        }
        if video_idx < 0 { close_input(&mut ic); return Err("没有视频流".to_string()); }

        let codec_id = std::ptr::read((codecpar_ptr as *const u8).add(4) as *const c_int);
        let codec = find_decoder(codec_id);
        if codec.is_null() { close_input(&mut ic); return Err(format!("找不到解码器 codec_id={codec_id}")); }
        let mut ctx = alloc_ctx(codec);
        params_to_ctx(ctx, codecpar_ptr);
        open2(ctx, codec, std::ptr::null_mut());

        let pkt = pkt_alloc();
        let frame = frame_alloc();
        let mut outputs = Vec::new();

        // 抽 0%, 50%, 95%
        let positions = [0.0, 0.5, 0.95];
        for (n, &frac) in positions.iter().enumerate() {
            let target_us = (duration_us as f64 * frac) as i64;
            seek_frame(ic, -1, target_us, 1); // AVSEEK_FLAG_BACKWARD
            flush(ctx);

            let mut got: Option<Vec<u8>> = None;
            for _ in 0..50 {
                if read_frame(ic, pkt) < 0 { break; }
                if send_pkt(ctx, pkt) < 0 { continue; }
                loop {
                    if recv_frame(ctx, frame) < 0 { break; }
                    let fw = std::ptr::read((frame as *const u8).add(104) as *const c_int);
                    let fh = std::ptr::read((frame as *const u8).add(108) as *const c_int);
                    let fmt = std::ptr::read((frame as *const u8).add(112) as *const c_int);
                    let mut src_data: [*const u8; 8] = [std::ptr::null(); 8];
                    let mut src_stride: [c_int; 8] = [0; 8];
                    for k in 0..8 {
                        src_data[k] = std::ptr::read((frame as *const u8).add(k * 8) as *const *const u8);
                        src_stride[k] = std::ptr::read((frame as *const u8).add(64 + k * 4) as *const c_int);
                    }
                    let mut rgb = vec![0u8; (fw * 3 * fh) as usize];
                    let dst_stride: [c_int; 8] = [fw * 3, 0, 0, 0, 0, 0, 0, 0];
                    let mut dst_data: [*mut u8; 8] = [std::ptr::null_mut(); 8];
                    dst_data[0] = rgb.as_mut_ptr();
                    let sws = sws_get(fw, fh, fmt, fw, fh, 2, 2, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null());
                    if !sws.is_null() {
                        sws_scale(sws, src_data.as_ptr(), src_stride.as_ptr(), 0, fh, dst_data.as_ptr() as *const *mut u8, dst_stride.as_ptr());
                        sws_free(sws);
                    }
                    let img = image::RgbImage::from_raw(fw as u32, fh as u32, rgb).ok_or("from_raw 失败")?;
                    let mut buf = Vec::new();
                    image::DynamicImage::ImageRgb8(img)
                        .write_to(&mut std::io::Cursor::new(&mut buf), image::ImageFormat::Jpeg)
                        .map_err(|e| e.to_string())?;
                    got = Some(buf);
                    break;
                }
                if got.is_some() { break; }
            }
            if let Some(jpg) = got {
                let p = out_prefix.with_extension(format!("{:03}.jpg", n + 1));
                std::fs::write(&p, &jpg).map_err(|e| e.to_string())?;
                outputs.push(p);
            }
        }

        let mut p2 = pkt; pkt_free(&mut p2);
        let mut f2 = frame; frame_free(&mut f2);
        let mut c2 = ctx; free_ctx(&mut c2);
        close_input(&mut ic);

        if outputs.is_empty() { Err("没抽到任何帧".to_string()) } else { Ok(outputs) }
    }
}

// ==== 最小转码：mp4 -> mp4（H.264 解码→重编码→封装，仅视频流） ====

pub fn av_transcode_mp4(input: &Path, output: &Path) -> Result<(), String> {
    let bin = find_ffmpeg_bin()?;
    std::env::set_var("PATH", format!("{};{}", bin.display(), std::env::var("PATH").unwrap_or_default()));

    let _avutil = unsafe { Library::new(bin.join("avutil-61.dll")) }.map_err(|e| e.to_string())?;
    let avcodec = unsafe { Library::new(bin.join("avcodec-63.dll")) }.map_err(|e| e.to_string())?;
    let avformat = unsafe { Library::new(bin.join("avformat-63.dll")) }.map_err(|e| e.to_string())?;

    macro_rules! s {
        ($lib:expr, $name:expr, $ty:ty) => {
            unsafe { *$lib.get::<$ty>($name.as_bytes()).map_err(|e| format!("找不到符号 {}：{e}", $name))? }
        };
    }

    let open_input = s!(avformat, "avformat_open_input", AvformatOpenInput);
    let find_stream_info = s!(avformat, "avformat_find_stream_info", AvformatFindStreamInfo);
    let close_input = s!(avformat, "avformat_close_input", AvformatCloseInput);
    let read_frame = s!(avformat, "av_read_frame", ReadFrame);
    let free_fmt = s!(avformat, "avformat_free_context", AvformatFreeContext);
    let alloc_out = s!(avformat, "avformat_alloc_output_context2", unsafe extern "C" fn(*mut *mut AVFormatContext, *const u8, *const u8, *const u8) -> c_int);
    let new_stream = s!(avformat, "avformat_new_stream", unsafe extern "C" fn(*mut AVFormatContext, *const AVCodec) -> *mut AVStream);
    let write_header = s!(avformat, "avformat_write_header", unsafe extern "C" fn(*mut AVFormatContext, *mut *mut u8) -> c_int);
    let write_frame = s!(avformat, "av_write_frame", unsafe extern "C" fn(*mut AVFormatContext, *mut AVPacket) -> c_int);
    let write_trailer = s!(avformat, "av_write_trailer", unsafe extern "C" fn(*mut AVFormatContext) -> c_int);
    let avio_open = s!(avformat, "avio_open", unsafe extern "C" fn(*mut *mut AVIOContext, *const u8, c_int) -> c_int);
    let avio_close = s!(avformat, "avio_closep", unsafe extern "C" fn(*mut *mut AVIOContext) -> c_int);

    let find_dec = s!(avcodec, "avcodec_find_decoder", FindDecoder);
    let find_enc = s!(avcodec, "avcodec_find_encoder", FindDecoder);
    let alloc_ctx = s!(avcodec, "avcodec_alloc_context3", AllocCodecCtx);
    let params_to_ctx = s!(avcodec, "avcodec_parameters_to_context", ParamsToCtx);
    let params_from_ctx = s!(avcodec, "avcodec_parameters_from_context", unsafe extern "C" fn(*mut AVCodecParameters, *const AVCodecContext) -> c_int);
    let params_alloc = s!(avcodec, "avcodec_parameters_alloc", unsafe extern "C" fn() -> *mut AVCodecParameters);
    let open2 = s!(avcodec, "avcodec_open2", OpenCodec2);
    let free_ctx = s!(avcodec, "avcodec_free_context", FreeCodecCtx);
    let pkt_alloc = s!(avcodec, "av_packet_alloc", PacketAlloc);
    let pkt_free = s!(avcodec, "av_packet_free", PacketFree);
    let frame_alloc = s!(_avutil, "av_frame_alloc", FrameAlloc);
    let frame_free = s!(_avutil, "av_frame_free", FrameFree);
    let send_pkt = s!(avcodec, "avcodec_send_packet", SendPacket);
    let recv_frame = s!(avcodec, "avcodec_receive_frame", ReceiveFrame);
    let send_frame = s!(avcodec, "avcodec_send_frame", unsafe extern "C" fn(*mut AVCodecContext, *const AVFrame) -> c_int);
    let recv_pkt_enc = s!(avcodec, "avcodec_receive_packet", unsafe extern "C" fn(*mut AVCodecContext, *mut AVPacket) -> c_int);

    let in_c = CString::new(input.to_string_lossy().as_ref()).map_err(|e| e.to_string())?;
    let out_c = CString::new(output.to_string_lossy().as_ref()).map_err(|e| e.to_string())?;

    unsafe {
        // 1. 打开输入
        let mut ic: *mut AVFormatContext = std::ptr::null_mut();
        open_input(&mut ic, in_c.as_ptr(), std::ptr::null_mut(), std::ptr::null_mut());
        find_stream_info(ic, std::ptr::null_mut());

        let nb_streams = std::ptr::read((ic as *const u8).add(44) as *const u32) as usize;
        let streams_arr: *const *const AVStream = std::ptr::read((ic as *const u8).add(48) as *const *const *const AVStream);

        let mut video_idx: isize = -1;
        let mut in_par: *mut AVCodecParameters = std::ptr::null_mut();
        let mut in_stream: *const AVStream = std::ptr::null();
        for i in 0..nb_streams {
            let stream = *streams_arr.add(i);
            if stream.is_null() { continue; }
            let par = std::ptr::read((stream as *const u8).add(16) as *const *mut AVCodecParameters);
            if par.is_null() { continue; }
            let ct = std::ptr::read((par as *const u8).add(0) as *const c_int);
            if ct == 0 { video_idx = i as isize; in_par = par; in_stream = stream; break; }
        }
        if video_idx < 0 { close_input(&mut ic); return Err("没有视频流".to_string()); }

        // 2. 打开解码器
        let codec_id = std::ptr::read((in_par as *const u8).add(4) as *const c_int);
        let dec_codec = find_dec(codec_id);
        let dec_ctx = alloc_ctx(dec_codec);
        params_to_ctx(dec_ctx, in_par);
        open2(dec_ctx, dec_codec, std::ptr::null_mut());

        // 3. 从解码器 context 导出参数
        let par = params_alloc();
        params_from_ctx(par, dec_ctx);

        // 4. 打开输出
        let mut oc: *mut AVFormatContext = std::ptr::null_mut();
        alloc_out(&mut oc, std::ptr::null(), std::ptr::null(), out_c.as_ptr() as *const u8);
        if oc.is_null() { return Err("alloc_output_context 失败".to_string()); }

        // 5. 创建输出视频流
        let out_stream = new_stream(oc, std::ptr::null());
        // 复制 codecpar 到 out_stream
        let out_par = std::ptr::read((out_stream as *const u8).add(16) as *const *mut AVCodecParameters);
        // 用 avcodec_parameters_copy
        let params_copy: unsafe extern "C" fn(*mut AVCodecParameters, *const AVCodecParameters) -> c_int =
            *avcodec.get(b"avcodec_parameters_copy\0").map_err(|e| format!("找不到 avcodec_parameters_copy：{e}"))?;
        params_copy(out_par, par);

        // 6. 打开编码器（H.264）
        let enc_codec = find_enc(codec_id);
        if enc_codec.is_null() { free_fmt(oc); close_input(&mut ic); return Err("找不到 H.264 编码器".to_string()); }
        let enc_ctx = alloc_ctx(enc_codec);
        params_to_ctx(enc_ctx, out_par);
        // time_base：从输入流读（AVStream+8: num@8, den@12）
        let num = std::ptr::read((in_stream as *const u8).add(8) as *const c_int);
        let den = std::ptr::read((in_stream as *const u8).add(12) as *const c_int);
        // enc_ctx->time_base 偏移未知，跳过（用默认）
        open2(enc_ctx, enc_codec, std::ptr::null_mut());

        // 7. 打开输出文件
        let pb_slot = (oc as *mut u8).add(128) as *mut *mut AVIOContext;
        if *pb_slot == std::ptr::null_mut() {
            if avio_open(pb_slot, out_c.as_ptr() as *const u8, 2) < 0 {
                free_fmt(oc); close_input(&mut ic);
                return Err("avio_open 失败".to_string());
            }
        }

        write_header(oc, std::ptr::null_mut());

        // 8. 转码循环
        let in_pkt = pkt_alloc();
        let out_pkt = pkt_alloc();
        let frame = frame_alloc();
        let mut frame_idx: i64 = 0;

        loop {
            if read_frame(ic, in_pkt) < 0 { break; }
            let stream_idx = std::ptr::read((in_pkt as *const u8).add(0) as *const c_int);
            if stream_idx != video_idx as c_int { continue; }

            if send_pkt(dec_ctx, in_pkt) < 0 { continue; }
            loop {
                if recv_frame(dec_ctx, frame) < 0 { break; }
                // 设置 pts
                std::ptr::write((frame as *mut u8).add(16) as *mut i64, frame_idx);
                frame_idx += 1;

                if send_frame(enc_ctx, frame) < 0 { break; }
                loop {
                    if recv_pkt_enc(enc_ctx, out_pkt) < 0 { break; }
                    write_frame(oc, out_pkt);
                }
            }
        }

        // flush 解码器
        send_pkt(dec_ctx, std::ptr::null());
        loop {
            if recv_frame(dec_ctx, frame) < 0 { break; }
            std::ptr::write((frame as *mut u8).add(16) as *mut i64, frame_idx);
            frame_idx += 1;
            if send_frame(enc_ctx, frame) < 0 { break; }
            loop {
                if recv_pkt_enc(enc_ctx, out_pkt) < 0 { break; }
                write_frame(oc, out_pkt);
            }
        }

        // flush 编码器
        send_frame(enc_ctx, std::ptr::null());
        loop {
            if recv_pkt_enc(enc_ctx, out_pkt) < 0 { break; }
            write_frame(oc, out_pkt);
        }

        write_trailer(oc);
        let mut pb = std::ptr::read((oc as *const u8).add(128) as *mut *mut AVIOContext);
        avio_close(&mut pb);
        free_fmt(oc);

        let mut ip = in_pkt; pkt_free(&mut ip);
        let mut op = out_pkt; pkt_free(&mut op);
        let mut f = frame; frame_free(&mut f);
        let mut dc = dec_ctx; free_ctx(&mut dc);
        let mut ec = enc_ctx; free_ctx(&mut ec);
        close_input(&mut ic);
    }
    Ok(())
}

// ==== mp4 -> GIF：解码 H.264 -> RGB -> image crate GIF 编码 ====

pub fn av_to_gif(input: &Path, output: &Path, fps: u32) -> Result<(), String> {
    let bin = find_ffmpeg_bin()?;
    std::env::set_var("PATH", format!("{};{}", bin.display(), std::env::var("PATH").unwrap_or_default()));

    let _avutil = unsafe { Library::new(bin.join("avutil-61.dll")) }.map_err(|e| e.to_string())?;
    let avcodec = unsafe { Library::new(bin.join("avcodec-63.dll")) }.map_err(|e| e.to_string())?;
    let avformat = unsafe { Library::new(bin.join("avformat-63.dll")) }.map_err(|e| e.to_string())?;
    let swscale = unsafe { Library::new(bin.join("swscale-10.dll")) }.map_err(|e| e.to_string())?;

    macro_rules! s {
        ($lib:expr, $name:expr, $ty:ty) => {
            unsafe { *$lib.get::<$ty>($name.as_bytes()).map_err(|e| format!("找不到符号 {}：{e}", $name))? }
        };
    }

    let open_input = s!(avformat, "avformat_open_input", AvformatOpenInput);
    let find_stream_info = s!(avformat, "avformat_find_stream_info", AvformatFindStreamInfo);
    let close_input = s!(avformat, "avformat_close_input", AvformatCloseInput);
    let alloc_fmt = s!(avformat, "avformat_alloc_context", AvformatAllocContext);
    let read_frame = s!(avformat, "av_read_frame", ReadFrame);

    let find_decoder = s!(avcodec, "avcodec_find_decoder", FindDecoder);
    let alloc_ctx = s!(avcodec, "avcodec_alloc_context3", AllocCodecCtx);
    let params_to_ctx = s!(avcodec, "avcodec_parameters_to_context", ParamsToCtx);
    let open2 = s!(avcodec, "avcodec_open2", OpenCodec2);
    let free_ctx = s!(avcodec, "avcodec_free_context", FreeCodecCtx);
    let pkt_alloc = s!(avcodec, "av_packet_alloc", PacketAlloc);
    let pkt_free = s!(avcodec, "av_packet_free", PacketFree);
    let frame_alloc = s!(_avutil, "av_frame_alloc", FrameAlloc);
    let frame_free = s!(_avutil, "av_frame_free", FrameFree);
    let send_pkt = s!(avcodec, "avcodec_send_packet", SendPacket);
    let recv_frame = s!(avcodec, "avcodec_receive_frame", ReceiveFrame);

    let sws_get = s!(swscale, "sws_getContext", SwsGetCtx);
    let sws_scale = s!(swscale, "sws_scale", SwsScale);
    let sws_free = s!(swscale, "sws_freeContext", SwsFreeCtx);

    let path_c = CString::new(input.to_string_lossy().as_ref()).map_err(|e| e.to_string())?;

    unsafe {
        let mut ic: *mut AVFormatContext = alloc_fmt();
        open_input(&mut ic, path_c.as_ptr(), std::ptr::null_mut(), std::ptr::null_mut());
        find_stream_info(ic, std::ptr::null_mut());

        let nb_streams = std::ptr::read((ic as *const u8).add(44) as *const u32) as usize;
        let streams_arr: *const *const AVStream = std::ptr::read((ic as *const u8).add(48) as *const *const *const AVStream);

        let mut video_idx: isize = -1;
        let mut codecpar_ptr: *mut AVCodecParameters = std::ptr::null_mut();
        for i in 0..nb_streams {
            let stream = *streams_arr.add(i);
            if stream.is_null() { continue; }
            let par = std::ptr::read((stream as *const u8).add(16) as *const *mut AVCodecParameters);
            if par.is_null() { continue; }
            let ct = std::ptr::read((par as *const u8).add(0) as *const c_int);
            if ct == 0 { video_idx = i as isize; codecpar_ptr = par; break; }
        }
        if video_idx < 0 { close_input(&mut ic); return Err("没有视频流".to_string()); }

        let codec_id = std::ptr::read((codecpar_ptr as *const u8).add(4) as *const c_int);
        let codec = find_decoder(codec_id);
        let mut ctx = alloc_ctx(codec);
        params_to_ctx(ctx, codecpar_ptr);
        open2(ctx, codec, std::ptr::null_mut());

        let pkt = pkt_alloc();
        let frame = frame_alloc();

        // 收集帧（最多 fps*duration 帧，但我们抽 12 帧）
        let mut frames: Vec<image::RgbImage> = Vec::new();
        let target_frames = 12u32;
        let mut frame_count = 0u32;

        // GIF 编码器提前建，边解码边编码
        let out_file = std::fs::File::create(output).map_err(|e| e.to_string())?;
        let mut encoder = image::codecs::gif::GifEncoder::new(out_file);

        while frame_count < target_frames {
            if read_frame(ic, pkt) < 0 { break; }
            if send_pkt(ctx, pkt) < 0 { continue; }
            loop {
                if recv_frame(ctx, frame) < 0 { break; }
                let fw = std::ptr::read((frame as *const u8).add(104) as *const c_int);
                let fh = std::ptr::read((frame as *const u8).add(108) as *const c_int);
                let fmt = std::ptr::read((frame as *const u8).add(112) as *const c_int);
                let mut src_data: [*const u8; 8] = [std::ptr::null(); 8];
                let mut src_stride: [c_int; 8] = [0; 8];
                for k in 0..8 {
                    src_data[k] = std::ptr::read((frame as *const u8).add(k * 8) as *const *const u8);
                    src_stride[k] = std::ptr::read((frame as *const u8).add(64 + k * 4) as *const c_int);
                }
                let mut rgb = vec![0u8; (fw * 3 * fh) as usize];
                let dst_stride: [c_int; 8] = [fw * 3, 0, 0, 0, 0, 0, 0, 0];
                let mut dst_data: [*mut u8; 8] = [std::ptr::null_mut(); 8];
                dst_data[0] = rgb.as_mut_ptr();
                let sws = sws_get(fw, fh, fmt, fw, fh, 2, 2, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null());
                if !sws.is_null() {
                    sws_scale(sws, src_data.as_ptr(), src_stride.as_ptr(), 0, fh, dst_data.as_ptr() as *const *mut u8, dst_stride.as_ptr());
                    sws_free(sws);
                }
                if let Some(img) = image::RgbImage::from_raw(fw as u32, fh as u32, rgb) {
                    let rgba: image::RgbaImage = image::DynamicImage::ImageRgb8(img).to_rgba8();
                    encoder.encode_frame(image::Frame::new(rgba)).map_err(|e| e.to_string())?;
                    frame_count += 1;
                }
                break;
            }
        }

        let mut p2 = pkt; pkt_free(&mut p2);
        let mut f2 = frame; frame_free(&mut f2);
        let mut c2 = ctx; free_ctx(&mut c2);
        close_input(&mut ic);

        if frame_count == 0 { return Err("没解码到帧".to_string()); }
    }
    Ok(())
}

// ==== 音频提取：mp4/mp3/aac -> wav（FFmpeg 解码 -> PCM -> 手写 WAV） ====

pub fn av_to_wav(input: &Path, output: &Path) -> Result<(), String> {
    let bin = find_ffmpeg_bin()?;
    std::env::set_var("PATH", format!("{};{}", bin.display(), std::env::var("PATH").unwrap_or_default()));

    let _avutil = unsafe { Library::new(bin.join("avutil-61.dll")) }.map_err(|e| e.to_string())?;
    let avcodec = unsafe { Library::new(bin.join("avcodec-63.dll")) }.map_err(|e| e.to_string())?;
    let avformat = unsafe { Library::new(bin.join("avformat-63.dll")) }.map_err(|e| e.to_string())?;

    macro_rules! s {
        ($lib:expr, $name:expr, $ty:ty) => {
            unsafe { *$lib.get::<$ty>($name.as_bytes()).map_err(|e| format!("找不到符号 {}：{e}", $name))? }
        };
    }

    let open_input = s!(avformat, "avformat_open_input", AvformatOpenInput);
    let find_stream_info = s!(avformat, "avformat_find_stream_info", AvformatFindStreamInfo);
    let close_input = s!(avformat, "avformat_close_input", AvformatCloseInput);
    let alloc_fmt = s!(avformat, "avformat_alloc_context", AvformatAllocContext);
    let read_frame = s!(avformat, "av_read_frame", ReadFrame);

    let find_decoder = s!(avcodec, "avcodec_find_decoder", FindDecoder);
    let alloc_ctx = s!(avcodec, "avcodec_alloc_context3", AllocCodecCtx);
    let params_to_ctx = s!(avcodec, "avcodec_parameters_to_context", ParamsToCtx);
    let open2 = s!(avcodec, "avcodec_open2", OpenCodec2);
    let free_ctx = s!(avcodec, "avcodec_free_context", FreeCodecCtx);
    let pkt_alloc = s!(avcodec, "av_packet_alloc", PacketAlloc);
    let pkt_free = s!(avcodec, "av_packet_free", PacketFree);
    let frame_alloc = s!(_avutil, "av_frame_alloc", FrameAlloc);
    let frame_free = s!(_avutil, "av_frame_free", FrameFree);
    let send_pkt = s!(avcodec, "avcodec_send_packet", SendPacket);
    let recv_frame = s!(avcodec, "avcodec_receive_frame", ReceiveFrame);

    let path_c = CString::new(input.to_string_lossy().as_ref()).map_err(|e| e.to_string())?;

    unsafe {
        let mut ic: *mut AVFormatContext = alloc_fmt();
        open_input(&mut ic, path_c.as_ptr(), std::ptr::null_mut(), std::ptr::null_mut());
        find_stream_info(ic, std::ptr::null_mut());

        let nb_streams = std::ptr::read((ic as *const u8).add(44) as *const u32) as usize;
        let streams_arr: *const *const AVStream = std::ptr::read((ic as *const u8).add(48) as *const *const *const AVStream);

        let mut audio_idx: isize = -1;
        let mut audio_par: *mut AVCodecParameters = std::ptr::null_mut();
        for i in 0..nb_streams {
            let stream = *streams_arr.add(i);
            if stream.is_null() { continue; }
            let par = std::ptr::read((stream as *const u8).add(16) as *const *mut AVCodecParameters);
            if par.is_null() { continue; }
            let ct = std::ptr::read((par as *const u8).add(0) as *const c_int);
            if ct == 1 { audio_idx = i as isize; audio_par = par; break; }
        }
        if audio_idx < 0 { close_input(&mut ic); return Err("没有音频流".to_string()); }

        let codec_id = std::ptr::read((audio_par as *const u8).add(4) as *const c_int);
        let channels = std::ptr::read((audio_par as *const u8).add(132) as *const i32);
        let sample_rate = std::ptr::read((audio_par as *const u8).add(152) as *const i32);
        let ch = channels.max(1) as usize;

        let codec = find_decoder(codec_id);
        let mut ctx = alloc_ctx(codec);
        params_to_ctx(ctx, audio_par);
        open2(ctx, codec, std::ptr::null_mut());

        let pkt = pkt_alloc();
        let frame = frame_alloc();

        // 流式写 WAV：先写占位 header，边解码边追加 PCM
        use std::io::{Write, Seek, SeekFrom};
        let mut file = std::fs::File::create(output).map_err(|e| e.to_string())?;
        // 写 44 字节占位 header
        let header: [u8; 44] = [0; 44];
        file.write_all(&header).map_err(|e| e.to_string())?;

        let mut total_samples: usize = 0;
        let mut buf: Vec<u8> = Vec::with_capacity(8192);

        loop {
            if read_frame(ic, pkt) < 0 { break; }
            let stream_idx = std::ptr::read((pkt as *const u8).add(36) as *const c_int);
            if stream_idx != audio_idx as c_int { continue; }
            if send_pkt(ctx, pkt) < 0 { continue; }
            loop {
                if recv_frame(ctx, frame) < 0 { break; }
                let nb_samples = std::ptr::read((frame as *const u8).add(112) as *const c_int) as usize;
                let sample_fmt = std::ptr::read((frame as *const u8).add(116) as *const c_int);
                if nb_samples == 0 { continue; }
                let d0 = std::ptr::read((frame as *const u8).add(0) as *const *const u8);
                let d1 = std::ptr::read((frame as *const u8).add(8) as *const *const u8);

                buf.clear();
                match sample_fmt {
                    1 => {
                        let ptr = d0 as *const i16;
                        for i in 0..(nb_samples * ch) {
                            let v = std::ptr::read(ptr.add(i));
                            buf.extend_from_slice(&v.to_le_bytes());
                        }
                    }
                    6 => {
                        let p0 = d0 as *const i16;
                        let p1 = if ch > 1 { d1 as *const i16 } else { p0 };
                        for i in 0..nb_samples {
                            buf.extend_from_slice(&std::ptr::read(p0.add(i)).to_le_bytes());
                            if ch > 1 { buf.extend_from_slice(&std::ptr::read(p1.add(i)).to_le_bytes()); }
                        }
                    }
                    8 => {
                        let p0 = d0 as *const f32;
                        let p1 = if ch > 1 { d1 as *const f32 } else { p0 };
                        for i in 0..nb_samples {
                            let v0 = (std::ptr::read(p0.add(i)) * 32767.0).clamp(-32768.0, 32767.0) as i16;
                            buf.extend_from_slice(&v0.to_le_bytes());
                            if ch > 1 {
                                let v1 = (std::ptr::read(p1.add(i)) * 32767.0).clamp(-32768.0, 32767.0) as i16;
                                buf.extend_from_slice(&v1.to_le_bytes());
                            }
                        }
                    }
                    _ => {
                        let p0 = d0 as *const i16;
                        let p1 = if ch > 1 { d1 as *const i16 } else { p0 };
                        for i in 0..nb_samples {
                            buf.extend_from_slice(&std::ptr::read(p0.add(i)).to_le_bytes());
                            if ch > 1 { buf.extend_from_slice(&std::ptr::read(p1.add(i)).to_le_bytes()); }
                        }
                    }
                }
                file.write_all(&buf).map_err(|e| e.to_string())?;
                total_samples += nb_samples;
            }
        }

        let mut p2 = pkt; pkt_free(&mut p2);
        let mut f2 = frame; frame_free(&mut f2);
        let mut c2 = ctx; free_ctx(&mut c2);
        close_input(&mut ic);

        if total_samples == 0 { return Err("没解码到音频".to_string()); }

        // 回来填 header
        let data_size = (total_samples * ch * 2) as u32;
        let file_size = 36 + data_size;
        let byte_rate = (sample_rate as u32) * (ch as u32) * 2;
        let block_align = (ch as u16) * 2;
        file.seek(SeekFrom::Start(0)).map_err(|e| e.to_string())?;
        file.write_all(b"RIFF").map_err(|e| e.to_string())?;
        file.write_all(&file_size.to_le_bytes()).map_err(|e| e.to_string())?;
        file.write_all(b"WAVE").map_err(|e| e.to_string())?;
        file.write_all(b"fmt ").map_err(|e| e.to_string())?;
        file.write_all(&16u32.to_le_bytes()).map_err(|e| e.to_string())?;
        file.write_all(&1u16.to_le_bytes()).map_err(|e| e.to_string())?;
        file.write_all(&(ch as u16).to_le_bytes()).map_err(|e| e.to_string())?;
        file.write_all(&(sample_rate as u32).to_le_bytes()).map_err(|e| e.to_string())?;
        file.write_all(&byte_rate.to_le_bytes()).map_err(|e| e.to_string())?;
        file.write_all(&block_align.to_le_bytes()).map_err(|e| e.to_string())?;
        file.write_all(&16u16.to_le_bytes()).map_err(|e| e.to_string())?;
        file.write_all(b"data").map_err(|e| e.to_string())?;
        file.write_all(&data_size.to_le_bytes()).map_err(|e| e.to_string())?;
    }
    Ok(())
}

// ==== 缩略图网格：mp4 -> 3x3 网格 JPEG ====

pub fn av_to_collage(input: &Path, output: &Path) -> Result<(), String> {
    let bin = find_ffmpeg_bin()?;
    std::env::set_var("PATH", format!("{};{}", bin.display(), std::env::var("PATH").unwrap_or_default()));

    let _avutil = unsafe { Library::new(bin.join("avutil-61.dll")) }.map_err(|e| e.to_string())?;
    let avcodec = unsafe { Library::new(bin.join("avcodec-63.dll")) }.map_err(|e| e.to_string())?;
    let avformat = unsafe { Library::new(bin.join("avformat-63.dll")) }.map_err(|e| e.to_string())?;
    let swscale = unsafe { Library::new(bin.join("swscale-10.dll")) }.map_err(|e| e.to_string())?;

    macro_rules! s {
        ($lib:expr, $name:expr, $ty:ty) => {
            unsafe { *$lib.get::<$ty>($name.as_bytes()).map_err(|e| format!("找不到符号 {}：{e}", $name))? }
        };
    }

    let open_input = s!(avformat, "avformat_open_input", AvformatOpenInput);
    let find_stream_info = s!(avformat, "avformat_find_stream_info", AvformatFindStreamInfo);
    let close_input = s!(avformat, "avformat_close_input", AvformatCloseInput);
    let alloc_fmt = s!(avformat, "avformat_alloc_context", AvformatAllocContext);
    let read_frame = s!(avformat, "av_read_frame", ReadFrame);
    let seek_frame = s!(avformat, "av_seek_frame", unsafe extern "C" fn(*mut AVFormatContext, c_int, i64, c_int) -> c_int);

    let find_decoder = s!(avcodec, "avcodec_find_decoder", FindDecoder);
    let alloc_ctx = s!(avcodec, "avcodec_alloc_context3", AllocCodecCtx);
    let params_to_ctx = s!(avcodec, "avcodec_parameters_to_context", ParamsToCtx);
    let open2 = s!(avcodec, "avcodec_open2", OpenCodec2);
    let free_ctx = s!(avcodec, "avcodec_free_context", FreeCodecCtx);
    let pkt_alloc = s!(avcodec, "av_packet_alloc", PacketAlloc);
    let pkt_free = s!(avcodec, "av_packet_free", PacketFree);
    let frame_alloc = s!(_avutil, "av_frame_alloc", FrameAlloc);
    let frame_free = s!(_avutil, "av_frame_free", FrameFree);
    let send_pkt = s!(avcodec, "avcodec_send_packet", SendPacket);
    let recv_frame = s!(avcodec, "avcodec_receive_frame", ReceiveFrame);
    let flush = s!(avcodec, "avcodec_flush_buffers", unsafe extern "C" fn(*mut AVCodecContext));

    let sws_get = s!(swscale, "sws_getContext", SwsGetCtx);
    let sws_scale = s!(swscale, "sws_scale", SwsScale);
    let sws_free = s!(swscale, "sws_freeContext", SwsFreeCtx);

    let path_c = CString::new(input.to_string_lossy().as_ref()).map_err(|e| e.to_string())?;

    unsafe {
        let mut ic: *mut AVFormatContext = alloc_fmt();
        open_input(&mut ic, path_c.as_ptr(), std::ptr::null_mut(), std::ptr::null_mut());
        find_stream_info(ic, std::ptr::null_mut());

        let nb_streams = std::ptr::read((ic as *const u8).add(44) as *const u32) as usize;
        let streams_arr: *const *const AVStream = std::ptr::read((ic as *const u8).add(48) as *const *const *const AVStream);
        let duration_us = std::ptr::read((ic as *const u8).add(104) as *const i64);

        let mut video_idx: isize = -1;
        let mut codecpar_ptr: *mut AVCodecParameters = std::ptr::null_mut();
        for i in 0..nb_streams {
            let stream = *streams_arr.add(i);
            if stream.is_null() { continue; }
            let par = std::ptr::read((stream as *const u8).add(16) as *const *mut AVCodecParameters);
            if par.is_null() { continue; }
            let ct = std::ptr::read((par as *const u8).add(0) as *const c_int);
            if ct == 0 { video_idx = i as isize; codecpar_ptr = par; break; }
        }
        if video_idx < 0 { close_input(&mut ic); return Err("没有视频流".to_string()); }

        let codec_id = std::ptr::read((codecpar_ptr as *const u8).add(4) as *const c_int);
        let codec = find_decoder(codec_id);
        let mut ctx = alloc_ctx(codec);
        params_to_ctx(ctx, codecpar_ptr);
        open2(ctx, codec, std::ptr::null_mut());

        let pkt = pkt_alloc();
        let frame = frame_alloc();

        // 抽 9 帧，3x3
        let mut thumbs: Vec<image::RgbImage> = Vec::new();
        for n in 0..9 {
            let frac = n as f64 / 8.0;
            let target_us = (duration_us as f64 * frac) as i64;
            seek_frame(ic, -1, target_us, 1);
            flush(ctx);
            for _ in 0..50 {
                if read_frame(ic, pkt) < 0 { break; }
                if send_pkt(ctx, pkt) < 0 { continue; }
                loop {
                    if recv_frame(ctx, frame) < 0 { break; }
                    let fw = std::ptr::read((frame as *const u8).add(104) as *const c_int);
                    let fh = std::ptr::read((frame as *const u8).add(108) as *const c_int);
                    let fmt = std::ptr::read((frame as *const u8).add(112) as *const c_int);
                    let mut src_data: [*const u8; 8] = [std::ptr::null(); 8];
                    let mut src_stride: [c_int; 8] = [0; 8];
                    for k in 0..8 {
                        src_data[k] = std::ptr::read((frame as *const u8).add(k * 8) as *const *const u8);
                        src_stride[k] = std::ptr::read((frame as *const u8).add(64 + k * 4) as *const c_int);
                    }
                    let mut rgb = vec![0u8; (fw * 3 * fh) as usize];
                    let dst_stride: [c_int; 8] = [fw * 3, 0, 0, 0, 0, 0, 0, 0];
                    let mut dst_data: [*mut u8; 8] = [std::ptr::null_mut(); 8];
                    dst_data[0] = rgb.as_mut_ptr();
                    let sws = sws_get(fw, fh, fmt, fw, fh, 2, 2, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null());
                    if !sws.is_null() {
                        sws_scale(sws, src_data.as_ptr(), src_stride.as_ptr(), 0, fh, dst_data.as_ptr() as *const *mut u8, dst_stride.as_ptr());
                        sws_free(sws);
                    }
                    if let Some(img) = image::RgbImage::from_raw(fw as u32, fh as u32, rgb) {
                        // 缩放到 160x120
                        let thumb = image::imageops::resize(&img, 160, 120, image::imageops::FilterType::Nearest);
                        thumbs.push(thumb);
                    }
                    break;
                }
                if thumbs.len() > n { break; }
            }
        }

        let mut p2 = pkt; pkt_free(&mut p2);
        let mut f2 = frame; frame_free(&mut f2);
        let mut c2 = ctx; free_ctx(&mut c2);
        close_input(&mut ic);

        if thumbs.is_empty() { return Err("没抽到帧".to_string()); }

        // 拼 3x3 网格，每张 160x120，间距 2px
        let cell_w = 160u32;
        let cell_h = 120u32;
        let gap = 2u32;
        let grid_w = cell_w * 3 + gap * 4;
        let grid_h = cell_h * 3 + gap * 4;
        let mut canvas = image::RgbImage::from_pixel(grid_w, grid_h, image::Rgb([30, 30, 30]));
        for (i, thumb) in thumbs.iter().enumerate() {
            let row = (i / 3) as u32;
            let col = (i % 3) as u32;
            let x = gap + col * (cell_w + gap);
            let y = gap + row * (cell_h + gap);
            image::imageops::overlay(&mut canvas, thumb, x as i64, y as i64);
        }
        canvas.save(output).map_err(|e| e.to_string())?;
    }
    Ok(())
}