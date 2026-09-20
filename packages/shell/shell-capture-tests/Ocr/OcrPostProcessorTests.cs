using System.Collections.Generic;
using System.Linq;
using BetterDesktop.Capture.Contracts;
using BetterDesktop.Ocr.Contracts;
using BetterDesktop.Shell.Capture.Ocr;
using Xunit;

namespace BetterDesktop.Shell.Capture.Tests.Ocr;

/// <summary>
/// OCR 后处理纯函数测试（M2，T4/D4：合成语料实跑断言输出）。
/// 全部输入为带像素框+置信度的合成词表，不触引擎。
/// </summary>
public class OcrPostProcessorTests
{
    private static OcrWord W(string text, int x, int y, int w, int h, float conf = 0.95f)
        => new(text, new PixelRect(x, y, w, h), conf);

    // ---- 多栏切分 ----

    [Fact]
    public void SplitColumns_two_columns_interleaved_lines()
    {
        // 左栏两行（X 10-30），右栏一行（X 100-120，与左栏首行同 Y）——交错排布考验归并
        var words = new List<OcrWord>
        {
            W("左", 10, 10, 20, 15),
            W("列", 10, 40, 20, 15),
            W("右", 100, 10, 20, 15),
        };
        var columns = OcrPostProcessor.SplitColumns(words);
        Assert.Equal(2, columns.Count);
        Assert.Contains(columns, c => c.All(w => w.Text == "左" || w.Text == "列"));
        Assert.Contains(columns, c => c.All(w => w.Text == "右"));
    }

    [Fact]
    public void SplitColumns_single_column_when_lines_share_x_band()
    {
        var words = new List<OcrWord>
        {
            W("一", 10, 10, 20, 15),
            W("行", 35, 10, 20, 15),
            W("两", 10, 40, 20, 15),
            W("行", 35, 40, 20, 15),
        };
        var columns = OcrPostProcessor.SplitColumns(words);
        Assert.Single(columns);
        Assert.Equal(4, columns[0].Count);
    }

    [Fact]
    public void Reconstruct_two_columns_reports_column_count_and_joins_texts()
    {
        var words = new List<OcrWord>
        {
            W("左", 10, 10, 20, 15),
            W("列", 10, 40, 20, 15),
            W("右", 100, 10, 20, 15),
        };
        var result = OcrPostProcessor.Reconstruct(words);
        Assert.Equal(2, result.ColumnCount);
        Assert.Contains("左", result.Text);
        Assert.Contains("右", result.Text);
    }

    // ---- 行聚合 ----

    [Fact]
    public void GroupLines_joins_latin_words_with_space_and_cjk_without()
    {
        var words = new List<OcrWord>
        {
            W("Hello", 10, 10, 40, 15),
            W("world", 60, 10, 40, 15),
            W("你好", 10, 40, 40, 15),
            W("世界", 50, 40, 40, 15),
        };
        var lines = OcrPostProcessor.GroupLines(words);
        Assert.Equal(2, lines.Count);
        Assert.Equal("Hello world", lines[0]);
        Assert.Equal("你好世界", lines[1]);
    }

    [Fact]
    public void GroupLines_two_y_bands_produce_two_lines()
    {
        var words = new List<OcrWord>
        {
            W("上", 10, 10, 20, 15),
            W("下", 10, 60, 20, 15),
        };
        Assert.Equal(2, OcrPostProcessor.GroupLines(words).Count);
    }

    // ---- 竖排断行 ----

    [Fact]
    public void DetectVerticalText_reads_top_to_bottom()
    {
        var words = new List<OcrWord>
        {
            W("春", 100, 10, 20, 40),
            W("眠", 100, 55, 20, 40),
            W("不", 100, 100, 20, 40),
            W("觉", 100, 145, 20, 40),
        };
        Assert.Equal("春眠不觉", OcrPostProcessor.DetectVerticalText(words));
    }

    [Fact]
    public void DetectVerticalText_returns_null_for_wide_layout()
    {
        var words = new List<OcrWord>
        {
            W("春", 100, 10, 20, 40),
            W("眠", 200, 55, 20, 40), // X 分布过宽 → 非竖排
            W("不", 300, 100, 20, 40),
        };
        Assert.Null(OcrPostProcessor.DetectVerticalText(words));
    }

    [Fact]
    public void Reconstruct_vertical_text_marks_is_vertical()
    {
        var words = new List<OcrWord>
        {
            W("春", 100, 10, 20, 40),
            W("眠", 100, 55, 20, 40),
        };
        var result = OcrPostProcessor.Reconstruct(words);
        Assert.True(result.IsVertical);
        Assert.Equal("春眠", result.Text);
    }

    // ---- 中英空格归一 ----

    [Theory]
    [InlineData("你好 world", "你好world")]
    [InlineData("Hello 世界", "Hello世界")]
    [InlineData("你好 世界", "你好世界")]
    [InlineData("Hello   world", "Hello world")]
    [InlineData("版本 2.0 发布", "版本2.0发布")]
    public void NormalizeCjkSpacing_removes_cjk_adjacent_whitespace(string input, string expected)
        => Assert.Equal(expected, OcrPostProcessor.NormalizeCjkSpacing(input));

    // ---- 标点归一 ----

    [Fact]
    public void NormalizePunctuation_fullwidth_ascii_to_halfwidth()
    {
        Assert.Equal("F5,(test)", OcrPostProcessor.NormalizePunctuation("Ｆ５，（test）"));
    }

    [Fact]
    public void NormalizePunctuation_removes_zero_width_and_fullwidth_space()
    {
        Assert.Equal("ab ", OcrPostProcessor.NormalizePunctuation("a\u200Bb\u3000"));
    }

    // ---- 表格 → Markdown ----

    [Fact]
    public void ToMarkdownTable_two_by_two_grid()
    {
        var words = new List<OcrWord>
        {
            W("名称", 10, 10, 30, 15),
            W("数量", 60, 10, 30, 15),
            W("苹果", 10, 35, 30, 15),
            W("3", 60, 35, 10, 15),
        };
        var table = OcrPostProcessor.ToMarkdownTable(words);
        Assert.NotNull(table);
        Assert.Equal(
            "| 名称 | 数量 |\n| --- | --- |\n| 苹果 | 3 |",
            table);
    }

    [Fact]
    public void ToMarkdownTable_returns_null_for_single_column_prose()
    {
        // 单栏正文：所有词 X 带重合 → 列中心仅 1 簇 → 非表格
        var words = new List<OcrWord>
        {
            W("这", 10, 10, 20, 15),
            W("是", 15, 10, 20, 15),
            W("正文", 20, 10, 30, 15),
            W("第二", 12, 40, 30, 15),
            W("行", 18, 40, 20, 15),
        };
        Assert.Null(OcrPostProcessor.ToMarkdownTable(words));
    }

    [Fact]
    public void Reconstruct_table_marks_is_table()
    {
        var words = new List<OcrWord>
        {
            W("名称", 10, 10, 30, 15),
            W("数量", 60, 10, 30, 15),
            W("苹果", 10, 35, 30, 15),
            W("3", 60, 35, 10, 15),
        };
        var result = OcrPostProcessor.Reconstruct(words);
        Assert.True(result.IsTable);
        Assert.StartsWith("| 名称 | 数量 |", result.Text);
    }

    // ---- 去噪 ----

    [Fact]
    public void Denoise_removes_low_conf_single_tall_artifact()
    {
        var words = new List<OcrWord>
        {
            W("真词", 10, 10, 30, 15),
            W("|", 100, 100, 2, 20, conf: 0.3f), // 光标残片：低置信+单字符+高瘦
        };
        var denoised = OcrPostProcessor.Denoise(words);
        Assert.Single(denoised);
        Assert.Equal("真词", denoised[0].Text);
    }

    [Fact]
    public void Denoise_keeps_low_conf_real_word()
    {
        var words = new List<OcrWord>
        {
            W("低清词", 10, 10, 60, 15, conf: 0.3f), // 多字符 → 非伪影，保留
        };
        var denoised = OcrPostProcessor.Denoise(words);
        Assert.Single(denoised);
    }

    [Fact]
    public void Denoise_deduplicates_overlapping_boxes_keeping_higher_confidence()
    {
        var words = new List<OcrWord>
        {
            W("ab", 10, 10, 20, 15, conf: 0.9f),
            W("ab", 12, 11, 20, 15, conf: 0.6f), // 与上框重叠 >80% → 删除低置信
        };
        var denoised = OcrPostProcessor.Denoise(words);
        var only = Assert.Single(denoised);
        Assert.Equal(0.9f, only.Confidence);
    }

    // ---- 低置信标记 ----

    [Fact]
    public void LowConfidenceWords_lists_below_threshold_only()
    {
        var words = new List<OcrWord>
        {
            W("可靠", 10, 10, 30, 15, conf: 0.9f),
            W("模糊", 50, 10, 30, 15, conf: 0.4f),
        };
        var low = OcrPostProcessor.LowConfidenceWords(words, 0.6f);
        var only = Assert.Single(low);
        Assert.Equal("模糊", only.Text);
    }

    [Fact]
    public void Reconstruct_keeps_low_confidence_words_in_text()
    {
        var words = new List<OcrWord>
        {
            W("可靠", 10, 10, 30, 15, conf: 0.9f),
            W("模糊", 50, 10, 30, 15, conf: 0.4f),
        };
        var result = OcrPostProcessor.Reconstruct(words);
        Assert.Contains("模糊", result.Text);      // 正文保留原文
        Assert.Single(result.LowConfidence);       // 另附低置信列表供 UI 底色
    }
}

/// <summary>paths-only 降级策略测试（D4：三分支，红线=可读原因）。</summary>
public class PathsOnlyPolicyTests
{
    [Fact]
    public void Full_mode_allows_without_warning()
    {
        var d = PathsOnlyPolicy.Evaluate(StorageModes.Full, thumbnailAvailable: false);
        Assert.True(d.Allowed);
        Assert.False(d.SuggestSwitchToFull);
    }

    [Fact]
    public void Unknown_mode_treated_as_full()
    {
        var d = PathsOnlyPolicy.Evaluate(null, thumbnailAvailable: false);
        Assert.True(d.Allowed);
    }

    [Fact]
    public void Paths_only_with_thumbnail_allows_but_warns_and_suggests_switch()
    {
        var d = PathsOnlyPolicy.Evaluate(StorageModes.PathsOnly, thumbnailAvailable: true);
        Assert.True(d.Allowed);
        Assert.True(d.SuggestSwitchToFull);
        Assert.Contains("精度下降", d.Reason); // 可读原因必须明说限制
    }

    [Fact]
    public void Paths_only_without_thumbnail_rejects_with_reason()
    {
        var d = PathsOnlyPolicy.Evaluate(StorageModes.PathsOnly, thumbnailAvailable: false);
        Assert.False(d.Allowed);
        Assert.True(d.SuggestSwitchToFull);
        Assert.Contains("无法识别", d.Reason);
        Assert.Contains("切换存储档", d.Reason);
    }
}
