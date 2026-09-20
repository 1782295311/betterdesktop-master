// convert-engine 入口：CLI 子命令分发。
// 契约：argv 只带子命令，请求体走 stdin JSON（路径/密码不拼命令行）。
//   capabilities   → stdout 单行 JSON：{engines, matrix}
//   run            → stdin JSON 请求 → stdout NDJSON（进度行 + result 末行）
//   probe [--refresh] → 刷新探测缓存并输出 capabilities（供 C# ResetProbeCache 语义接线）
mod capabilities;
mod constants;
mod contract;
mod engines;
mod engines_impl;
mod error;
mod exec;
mod heic_engine;
mod image_engine;
mod lite;
mod managed;
mod matrix;
mod pdf_ops;
mod pdf_security;
mod pdf_text;
mod raw_engine;
mod run;
mod service;

use std::io::Read;

fn main() {
    let args: Vec<String> = std::env::args().collect();
    if args.len() < 2 {
        eprintln!("用法: convert-engine <capabilities|run|probe>");
        std::process::exit(2);
    }

    let matrix = matrix::ConversionMatrix::default();
    let probe = engines::EngineProbe::new();

    match args[1].as_str() {
        "capabilities" => {
            let engines = capabilities::engines_json(&probe);
            println!("{}", capabilities::capabilities_json(&matrix, &engines));
        }
        "probe" => {
            // probe [--refresh]：--refresh 先失效缓存再全量探测（引擎安装/卸载后语义）。
            if args.get(2).map(|s| s.as_str()) == Some("--refresh") {
                probe.reset();
            }
            let engines = capabilities::engines_json(&probe);
            println!("{}", capabilities::capabilities_json(&matrix, &engines));
        }
        "run" => {
            if let Err(code) = run_dispatch(&matrix, &probe) {
                std::process::exit(code);
            }
        }
        other => {
            eprintln!("未知子命令: {other}");
            std::process::exit(2);
        }
    }
}

/// run 分发：读 stdin JSON 请求 → 执行链 → 逐行输出 NDJSON（progress + result）。
/// 退出码：0=已输出结果（成功或业务失败均在 NDJSON result 行）；2=请求体非法。
fn run_dispatch(matrix: &matrix::ConversionMatrix, probe: &engines::EngineProbe) -> Result<(), i32> {
    let mut buf = String::new();
    if std::io::stdin().read_to_string(&mut buf).is_err() {
        eprintln!("读取 stdin 请求失败");
        return Err(2);
    }
    let body = buf.trim();
    if body.is_empty() {
        eprintln!("run 缺少 stdin JSON 请求体");
        return Err(2);
    }
    let req: run::RunRequest = match serde_json::from_str(body) {
        Ok(r) => r,
        Err(e) => {
            eprintln!("run 请求体非法: {e}");
            return Err(2);
        }
    };
    // 引擎注册表：6 子进程引擎（S6/S7 追加托管引擎与 TwoHop/PdfCompose/PdfSecurity）。
    let engines: Vec<Box<dyn run::Engine>> = engines_impl::subprocess_engines();
    let lines = run::run_conversion(&req, matrix, probe, &engines);
    for line in lines {
        println!("{line}");
    }
    Ok(())
}
