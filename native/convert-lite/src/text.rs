//! 文本/结构化格式互转（零外部二进制）。
//!
//! 诚实进度策略：
//!  - json <-> yaml：必须解析成完整 Value 树，中间没有可观测的字节进度，
//!    所以只推阶段（Running/Publishing），percent=None，不编造。
//!  - csv <-> json：逐行流式，按已处理字节/总字节给真实百分比。
//!  - md -> html：pulldown-cmark 流式事件，按已消费字节给真实百分比。

use std::fs::File;
use std::io::{BufReader, BufWriter, Read, Write};
use std::path::Path;

use serde::{de::DeserializeOwned, Serialize};
use serde_json::Value;

use crate::guard;
use crate::protocol::{Emitter, Phase};

/// 剥掉 UTF-8 BOM（EF BB BF）。Windows 下编辑器/导出常带 BOM，
/// serde_json / serde_yaml 默认不接受，这是真实场景最常见的解析失败。
fn strip_bom<B: std::io::BufRead>(bufr: &mut B) -> Result<(), String> {
    let head = bufr.fill_buf().map_err(|e| e.to_string())?;
    if head.starts_with(&[0xEF, 0xBB, 0xBF]) {
        bufr.consume(3);
    }
    Ok(())
}

/// 通用：从 reader 反序列化为 T，再序列化到 writer。用于 json<->yaml。
fn transcode_tree<R: Read, W: Write, T: DeserializeOwned + Serialize>(
    reader: R,
    writer: W,
    to_json: bool,
) -> Result<(), String> {
    let v: T = if to_json {
        // 进来是 yaml，出去是 json：yaml -> Value -> json
        serde_yaml::from_reader(reader).map_err(|e| format!("YAML 解析失败：{e}"))?
    } else {
        serde_json::from_reader(reader).map_err(|e| format!("JSON 解析失败：{e}"))?
    };
    if to_json {
        serde_json::to_writer_pretty(writer, &v).map_err(|e| format!("JSON 写出失败：{e}"))?;
    } else {
        serde_yaml::to_writer(writer, &v).map_err(|e| format!("YAML 写出失败：{e}"))?;
    }
    Ok(())
}

/// json <-> yaml。树结构，无中间字节进度 -> 阶段式。
pub fn json_yaml(
    src: &Path,
    out: &Path,
    from_json: bool, // true: json->yaml; false: yaml->json
    em: &Emitter,
) -> Result<(), String> {
    let tmp = guard::tmp_path(out);
    let target = if from_json { "yaml" } else { "json" };
    em.progress(
        &src.to_string_lossy(),
        target,
        "managed-tree",
        Phase::Running,
        None,
        Some("全量解析为结构化树（无中间字节进度）"),
    );

    let r = (|| -> Result<(), String> {
        let fin = File::open(src).map_err(|e| e.to_string())?;
        let mut bufr = BufReader::new(fin);
        strip_bom(&mut bufr)?;
        let fout = File::create(&tmp).map_err(|e| e.to_string())?;
        // Value 通用；树互转无法诚实报百分比，故 percent=None。
        transcode_tree::<_, _, Value>(bufr, fout, !from_json)
    })();

    match r {
        Ok(()) => {
            em.progress(
                &src.to_string_lossy(),
                target,
                "managed-tree",
                Phase::Finalizing,
                None,
                Some("原子落盘"),
            );
            guard::commit(&tmp, out).map_err(|e| format!("提交产物失败：{e}"))?;
            Ok(())
        }
        Err(e) => {
            guard::discard(&tmp);
            Err(e)
        }
    }
}

/// csv -> jsonl：流式逐行。jsonl（每行一个 JSON 对象）比 json 数组更适合大 CSV。
/// 诚实性：csv crate 内部缓冲，无法精确观测已消费字节，故中间只推 Running(None)，
/// 完成时一次性给 100% 并带行数。绝不编造中间百分比。
pub fn csv_to_jsonl(
    src: &Path,
    out: &Path,
    em: &Emitter,
) -> Result<(), String> {
    let tmp = guard::tmp_path(out);
    em.progress(
        &src.to_string_lossy(),
        "jsonl",
        "csv-stream",
        Phase::Running,
        None,
        Some("逐行流式转换（无中间字节进度）"),
    );

    let r = (|| -> Result<u64, String> {
        let in_f = File::open(src).map_err(|e| e.to_string())?;
        let mut rdr = csv::Reader::from_reader(BufReader::new(in_f));
        let headers = rdr
            .headers()
            .map_err(|e| format!("CSV 表头读取失败：{e}"))?
            .clone();
        let fout = File::create(&tmp).map_err(|e| e.to_string())?;
        let mut w = BufWriter::new(fout);
        let mut row_count: u64 = 0;
        for result in rdr.records() {
            let record = result.map_err(|e| format!("CSV 行读取失败：{e}"))?;
            let map: serde_json::Map<String, Value> = headers
                .iter()
                .zip(record.iter())
                .map(|(h, v)| (h.to_string(), Value::String(v.to_string())))
                .collect();
            serde_json::to_writer(&mut w, &Value::Object(map))
                .map_err(|e| format!("JSONL 行序列化失败：{e}"))?;
            writeln!(w).map_err(|e| format!("JSONL 换行写出失败：{e}"))?;
            row_count += 1;
        }
        w.flush().map_err(|e| e.to_string())?;
        Ok(row_count)
    })();

    match r {
        Ok(row_count) => {
            em.progress(
                &src.to_string_lossy(),
                "jsonl",
                "csv-stream",
                Phase::Finalizing,
                Some(100),
                Some(&format!("共 {row_count} 行")),
            );
            guard::commit(&tmp, out).map_err(|e| format!("提交产物失败：{e}"))?;
            Ok(())
        }
        Err(e) => {
            guard::discard(&tmp);
            Err(e)
        }
    }
}

/// markdown -> html。pulldown-cmark 流式事件；这里按源文件总字节给阶段进度。
pub fn md_to_html(
    src: &Path,
    out: &Path,
    em: &Emitter,
) -> Result<(), String> {
    let tmp = guard::tmp_path(out);
    em.progress(
        &src.to_string_lossy(),
        "html",
        "cmark-stream",
        Phase::Running,
        None,
        Some("流式渲染 Markdown（单文档，无分段进度）"),
    );

    let r = (|| -> Result<(), String> {
        let raw = std::fs::read(src).map_err(|e| e.to_string())?;
        let text = match raw.strip_prefix(&[0xEF, 0xBB, 0xBF]) {
            Some(rest) => std::str::from_utf8(rest).map_err(|e| format!("Markdown 非 UTF-8：{e}"))?,
            None => std::str::from_utf8(&raw).map_err(|e| format!("Markdown 非 UTF-8：{e}"))?,
        };
        let fout = File::create(&tmp).map_err(|e| e.to_string())?;
        let mut w = BufWriter::new(fout);
        use pulldown_cmark::{html, Options, Parser};
        let mut opts = Options::empty();
        opts.insert(Options::ENABLE_TABLES);
        opts.insert(Options::ENABLE_FOOTNOTES);
        // 输出完整 HTML 骨架并声明 UTF-8，避免浏览器按系统 ANSI 码表读中文乱码。
        write!(
            w,
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>document</title></head><body>"
        )
        .map_err(|e| e.to_string())?;
        let parser = Parser::new_ext(&text, opts);
        html::write_html_io(&mut w, parser).map_err(|e| format!("HTML 写出失败：{e}"))?;
        write!(w, "</body></html>").map_err(|e| e.to_string())?;
        w.flush().map_err(|e| e.to_string())?;
        Ok(())
    })();

    match r {
        Ok(()) => {
            em.progress(
                &src.to_string_lossy(),
                "html",
                "cmark-stream",
                Phase::Finalizing,
                Some(100),
                None,
            );
            guard::commit(&tmp, out).map_err(|e| format!("提交产物失败：{e}"))?;
            Ok(())
        }
        Err(e) => {
            guard::discard(&tmp);
            Err(e)
        }
    }
}
