# 归档说明（Archived Agent Notes）

> 归档 = 冻结。进入本目录的记录是历史现场，**不可修改、不可删除**。

## 归档步骤

1. 确认记录不再指导未来工作，将其移入本目录对应类别子目录。
2. 运行 `scripts\verify-archived-notes.ps1 -Write`，把每个文件的 SHA-256 追加密封进 `manifest.json`。
3. 提交（同一变更落地），此后该条目成为只读历史。

## 密封与防篡改（由门禁 `verify-archived-notes` 强制）

- `manifest.json` 记录 `file → sha256`；文件内容与哈希不符即红。
- 与上一版 HEAD 比对：已密封条目**不得**从清单消失、哈希**不得**被改写（追加保护）。
- 目录下出现未登记的 `.md` 即红（必须先 `-Write` 密封）。

## 目录结构

`architecture/`、`feature/`、`bug-fix/`、`process/`、`testing/`、`simplification/` 六类，与活跃区一致。
