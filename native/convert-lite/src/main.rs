//! convert-lite：轻量化格式转换实验的 CLI 入口（薄壳）。
//!
//! 用法：
//!   convert-lite <input...> --to <ext> [-o <output>]
//!
//! 转换核心在 lib.rs（`convert_lite::convert`，完全静默）；本壳只做参数解析，
//! 并输出 result NDJSON 行（回归与宿主协议兼容）。

use std::path::Path;
use std::process::ExitCode;

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().collect();

    // rename 模式：convert-lite --rename <dir> --find x --replace y [--prefix p] [--suffix s] [--number 1]
    if args.get(1).map(|s| s.as_str()) == Some("--rename") {
        let mut dir: Option<&str> = None;
        let mut find: Option<&str> = None;
        let mut replace: Option<&str> = None;
        let mut prefix: Option<&str> = None;
        let mut suffix: Option<&str> = None;
        let mut number: u32 = 0;
        let mut recursive = false;
        let mut i = 2;
        while i < args.len() {
            match args[i].as_str() {
                "--find" => { i += 1; find = args.get(i).map(|s| s.as_str()); }
                "--replace" => { i += 1; replace = args.get(i).map(|s| s.as_str()); }
                "--prefix" => { i += 1; prefix = args.get(i).map(|s| s.as_str()); }
                "--suffix" => { i += 1; suffix = args.get(i).map(|s| s.as_str()); }
                "--number" => { i += 1; number = args.get(i).and_then(|s| s.parse().ok()).unwrap_or(0); }
                "--recursive" | "-r" => { recursive = true; }
                other if !other.starts_with('-') && dir.is_none() => { dir = Some(other); }
                _ => {}
            }
            i += 1;
        }
        let Some(dir) = dir else {
            eprintln!("用法: convert-lite --rename <dir> --find x --replace y [--prefix p] [--suffix s] [--number 1] [-r]");
            return ExitCode::from(2);
        };
        match convert_lite::rename::batch_rename(
            Path::new(dir), find, replace, prefix, suffix, number, recursive,
        ) {
            Ok(r) => {
                println!("重命名 {} 个文件，错误 {} 个", r.renamed.len(), r.errors.len());
                for (from, to) in &r.renamed {
                    println!("  {} → {}", from.display(), to.display());
                }
                for e in &r.errors {
                    eprintln!("  错误：{e}");
                }
            }
            Err(e) => eprintln!("失败：{e}"),
        }
        return ExitCode::SUCCESS;
    }

    let mut inputs: Vec<&str> = Vec::new();
    let mut target: Option<&str> = None;
    let mut output: Option<&str> = None;
    let mut key: Option<&str> = None;

    let mut i = 1;
    while i < args.len() {
        match args[i].as_str() {
            "--to" => {
                i += 1;
                target = args.get(i).map(|s| s.as_str());
            }
            "-o" | "--output" => {
                i += 1;
                output = args.get(i).map(|s| s.as_str());
            }
            "-k" | "--key" => {
                i += 1;
                key = args.get(i).map(|s| s.as_str());
            }
            h if h.starts_with('-') => {
                eprintln!("未知参数：{h}");
                return ExitCode::from(2);
            }
            other => inputs.push(other),
        }
        i += 1;
    }

    let (Some(target), Some(_)) = (target, inputs.first()) else {
        eprintln!("用法: {} <input...> --to <ext> [-o <output>]", args[0]);
        eprintln!(
            "能力: 图片互转 / json<->yaml / csv->jsonl / md->html / docx->html / \
             xlsx->csv|html / 多图->pdf / 文档互转（md/docx/epub/html/rtf/odt/...） / PDF 文本"
        );
        return ExitCode::from(2);
    };

    let paths: Vec<&Path> = inputs.iter().map(Path::new).collect();
    let out_path = output.map(Path::new);
    let input_repr = paths[0].to_string_lossy().replace('\\', "/");

    match convert_lite::convert(&paths, target, out_path, key) {
        Ok(Some(out)) => {
            let out_repr = out.to_string_lossy().replace('\\', "/");
            println!(
                "{{\"kind\":\"result\",\"ok\":true,\"input\":\"{}\",\"output\":\"{}\",\"error\":null}}",
                json_escape(&input_repr),
                json_escape(&out_repr)
            );
            ExitCode::SUCCESS
        }
        Ok(None) => ExitCode::FAILURE,
        Err(e) => {
            println!(
                "{{\"kind\":\"result\",\"ok\":false,\"input\":\"{}\",\"output\":null,\"error\":\"{}\"}}",
                json_escape(&input_repr),
                json_escape(&e)
            );
            ExitCode::FAILURE
        }
    }
}

fn json_escape(s: &str) -> String {
    s.replace('\\', "\\\\")
        .replace('"', "\\\"")
        .replace('\n', " ")
        .replace('\r', " ")
}
