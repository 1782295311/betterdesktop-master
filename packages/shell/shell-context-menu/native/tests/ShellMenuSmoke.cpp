// BetterDesktopShellMenu — 纯函数冒烟测试（无框架、无 COM，独立控制台进程）
//
// 覆盖：配置解析（正常/坏 JSON/缺字段/超长标题）、谓词选枝（场景/扩展名/同扩展名/数量下限）、
// 以及批文件 JSON 序列化（转义）。
//
// 用法：ShellMenuSmoke.exe           （用内置样例，全部断言）
//       ShellMenuSmoke.exe <path>    （额外解析指定配置文件并打印结果）

#include "JsonLite.h"
#include "Launcher.h"
#include "MenuModel.h"

#include <windows.h>

#include <cstdio>
#include <string>

namespace
{
    int g_failures = 0;
    int g_checks = 0;

    void Check(bool condition, const char* what)
    {
        ++g_checks;
        if (condition)
        {
            std::printf("  [ok]   %s\n", what);
        }
        else
        {
            ++g_failures;
            std::printf("  [FAIL] %s\n", what);
        }
    }

    const char* kSampleConfig = R"JSON({
      "version": 1,
      "extensionEnabled": true,
      "items": [
        {
          "id": "desktopControls",
          "title": "桌面控制",
          "kind": "submenu",
          "scenes": ["background"],
          "children": [
            { "id": "icons", "title": "桌面图标显隐", "kind": "toggle", "checked": true,
              "action": "toggle-key", "args": ["icons"] },
            { "id": "taskbar", "title": "隐藏任务栏", "kind": "toggle", "checked": false,
              "action": "toggle-key", "args": ["taskbar"] }
          ]
        },
        {
          "id": "convert",
          "title": "格式转换",
          "kind": "submenu",
          "scenes": ["files"],
          "filter": { "extensions": [".docx", ".md", ".png", ".pdf"], "selection": "sameExtension" },
          "children": [
            { "id": "convert-to-pdf", "title": "转为 PDF", "action": "convert-to",
              "args": ["pdf"], "highlight": true, "default": true },
            { "id": "convert-to-md", "title": "转为 Markdown", "action": "convert-to",
              "args": ["md"], "highlight": true },
            { "id": "mergePdf", "title": "合并 PDF", "action": "convert-to", "args": ["pdf"],
              "requiresAllExtIn": [".pdf"], "requiresCountMin": 2 }
          ]
        }
      ]
    })JSON";

    bool LoadSample(bdshell::MenuConfig* config)
    {
        std::wstring error;
        if (!bdshell::ParseConfigText(kSampleConfig, config, &error))
        {
            std::printf("  样例配置解析失败: %ls\n", error.c_str());
            return false;
        }
        return true;
    }

    const bdshell::MenuItemSpec* FindItem(const bdshell::MenuConfig& config, const wchar_t* id)
    {
        for (const auto& item : config.items)
        {
            if (item.id == id)
            {
                return &item;
            }
        }
        return nullptr;
    }
}

int wmain(int argc, wchar_t** argv)
{
    std::printf("== ShellMenuSmoke ==\n");

    bdshell::MenuConfig config;
    if (!LoadSample(&config))
    {
        return 1;
    }

    std::printf("[1] 解析与字段\n");
    Check(config.items.size() == 2, "顶级项数量 = 2");
    const auto* convert = FindItem(config, L"convert");
    Check(convert != nullptr, "命中 convert 项");
    Check(convert != nullptr && convert->children.size() == 3, "convert 子项 = 3");
    Check(convert != nullptr && convert->filterExtensions.size() == 4, "filter.extensions 已归一化");
    Check(convert != nullptr && convert->filterSameExtension, "selection=sameExtension 生效");
    const auto* controls = FindItem(config, L"desktopControls");
    Check(controls != nullptr && controls->scenes.size() == 1, "桌面控制 scenes=1");

    std::printf("[2] 场景选枝\n");
    {
        bdshell::SelectionContext background;
        background.scene = bdshell::MenuScene::Background;
        const auto visible = bdshell::SelectVisibleItems(config, background);
        Check(visible.size() == 1 && visible[0]->id == L"desktopControls",
            "背景场景只出现「桌面控制」");
    }
    {
        bdshell::SelectionContext files;
        files.scene = bdshell::MenuScene::Files;
        files.paths = { L"C:\\a\\报告.docx" };
        const auto visible = bdshell::SelectVisibleItems(config, files);
        Check(visible.size() == 1 && visible[0]->id == L"convert",
            "文件场景出现「格式转换」");
    }
    {
        bdshell::SelectionContext files;
        files.scene = bdshell::MenuScene::Files;
        files.paths = { L"C:\\a\\未知.xyz" };
        const auto visible = bdshell::SelectVisibleItems(config, files);
        Check(visible.empty(), "未登记扩展名 → 不出现（不弹半可用菜单）");
    }

    std::printf("[3] 子项谓词\n");
    {
        bdshell::SelectionContext single;
        single.scene = bdshell::MenuScene::Files;
        single.paths = { L"C:\\a\\报告.docx" };
        const auto children = bdshell::SelectVisibleChildren(*convert, single);
        Check(children.size() == 2, "单文件 .docx → 2 个转换目标（合并 PDF 被 requiresCountMin 挡掉）");

        // 多选 2 个 PDF（在 filter.extensions 内）→ 父项可见，mergePdf 因 requiresCountMin=2 出现
        bdshell::SelectionContext twoPdfs;
        twoPdfs.scene = bdshell::MenuScene::Files;
        twoPdfs.paths = { L"C:\\a\\1.pdf", L"C:\\a\\2.pdf" };
        Check(bdshell::MatchesSelection(*convert, twoPdfs), "多选 .pdf 在 filter.extensions 内 → 父项可见");
        const auto pdfChildren = bdshell::SelectVisibleChildren(*convert, twoPdfs);
        Check(pdfChildren.size() == 3, "多选 .pdf → 3 个子项（含 requiresCountMin 触发的合并 PDF）");
        bool hasMerge = false;
        for (const auto* child : pdfChildren) { hasMerge = hasMerge || child->id == L"mergePdf"; }
        Check(hasMerge, "多选 .pdf → 合并 PDF 出现");

        // 单选 PDF：count 不足，合并项必须消失
        bdshell::SelectionContext onePdf;
        onePdf.scene = bdshell::MenuScene::Files;
        onePdf.paths = { L"C:\\a\\1.pdf" };
        const auto singlePdfChildren = bdshell::SelectVisibleChildren(*convert, onePdf);
        Check(singlePdfChildren.size() == 2, "单选 .pdf → 合并 PDF 被 requiresCountMin 挡掉");
    }
    {
        // 混合扩展名：filterSameExtension 必须挡住父项
        bdshell::SelectionContext mixed;
        mixed.scene = bdshell::MenuScene::Files;
        mixed.paths = { L"C:\\a\\1.docx", L"C:\\a\\2.md" };
        Check(!bdshell::MatchesSelection(*convert, mixed), "混合扩展名 → 格式转换不出现");
    }
    {
        bdshell::SelectionContext dirs;
        dirs.scene = bdshell::MenuScene::Directory;
        dirs.paths = { L"C:\\a\\folder" };
        Check(bdshell::SelectVisibleItems(config, dirs).empty(), "目录场景两项都不出现（本批设计）");
    }

    std::printf("[4] 坏输入\n");
    {
        bdshell::MenuConfig dummy;
        std::wstring error;
        Check(!bdshell::ParseConfigText("{ not json", &dummy, &error), "坏 JSON → 解析失败");
        Check(!error.empty(), "坏 JSON → 带回错误原因");
        Check(!bdshell::ParseConfigText("[]", &dummy, &error), "根节点非对象 → 失败");
        Check(bdshell::ParseConfigText("{\"items\":[]}", &dummy, &error), "空 items → 合法（菜单不显示）");
        Check(dummy.items.empty(), "空 items → 无项");
    }
    {
        // 超长标题必须被截断（MUIVerb ≤80 字符红线）
        std::wstring longTitle(200, L'x');
        std::string text = "{\"items\":[{\"id\":\"a\",\"title\":\"";
        for (size_t i = 0; i < longTitle.size(); ++i) { text += 'x'; }
        text += "\",\"action\":\"x\"}]}";

        bdshell::MenuConfig dummy;
        std::wstring error;
        const bool ok = bdshell::ParseConfigText(text, &dummy, &error);
        Check(ok, "超长标题配置仍可解析");
        Check(ok && !dummy.items.empty() && dummy.items[0].title.size() <= 80, "超长标题被截断到 80");
    }

    std::printf("[5] 批文件序列化\n");
    {
        bdshell::LaunchRequest request;
        request.action = L"convert-to";
        request.args = { L"pdf" };
        request.paths = { L"C:\\a\\含\"引号\".docx", L"C:\\b\\2.docx" };
        const std::string json = bdshell::BuildBatchJson(request);
        Check(json.find("\\\"") != std::string::npos, "路径引号被转义");
        Check(json.find("\"action\":\"convert-to\"") != std::string::npos, "action 字段正确");

        std::wstring roundTripError;
        const auto parsed = bdshell::json::Parse(json, &roundTripError);
        Check(parsed != nullptr && parsed->IsObject(), "序列化结果可被解析器读回");
        const auto* paths = parsed != nullptr ? parsed->GetArray(L"paths") : nullptr;
        Check(paths != nullptr && paths->array.size() == 2, "读回后 paths 数量一致");
    }

    std::printf("[6] 扩展名工具\n");
    {
        Check(bdshell::ExtensionOf(L"C:\\a\\B.DOCX") == L".docx", "扩展名小写化");
        Check(bdshell::ExtensionOf(L"C:\\a.b\\noext") == L"", "无扩展名返回空");
        Check(bdshell::ExtensionOf(L"C:\\a\\dir.b\\x.tar.gz") == L".gz", "取最后一段扩展名");
    }

    // =====================================================================
    // [7] UTF-8 解码回归（2026-09-11 线上乱码事故的唯一防线）
    //
    // 事故：ParseString 曾把 UTF-8 字节逐个 static_cast<wchar_t> 塞进宽串，
    //       于是 "桌面控制" 的 E6 A1 8C… 变成 U+00E6 U+00A1 U+008C…，菜单里
    //       显示成「æ¡Œé¢æŽ§åˆ¶」。旧测试只比 id 与数量、不比标题文本，全部通过。
    //
    // 教训：断言必须**比宽字符串本身**。任何 "打印出来看看" 都不算验证——
    //       printf("%ls") 在 MSVC 默认 "C" locale 下按字节截断输出，恰好把乱码宽串
    //       还原成原始 UTF-8 字节，看起来完全正常。
    // =====================================================================
    std::printf("[7] UTF-8 解码（中文/转义/代理对）\n");
    {
        // ① 配置内嵌中文（无 BOM UTF-8）逐码点还原
        Check(convert != nullptr && convert->title == L"格式转换", "顶级标题 == L\"格式转换\"");
        Check(controls != nullptr && controls->title == L"桌面控制", "顶级标题 == L\"桌面控制\"");
        Check(convert != nullptr && convert->children.size() > 0 &&
              convert->children[0].title == L"转为 PDF", "子项标题 == L\"转为 PDF\"");
        Check(convert != nullptr && convert->children.size() > 2 &&
              convert->children[2].title == L"合并 PDF", "子项标题 == L\"合并 PDF\"");
        Check(controls != nullptr && controls->children.size() > 1 &&
              controls->children[1].title == L"隐藏任务栏", "孙项标题 == L\"隐藏任务栏\"");

        // ② 乱码指纹：正确解码后绝不该出现 C1 控制符（U+0080..U+009F）。
        //    "UTF-8 字节按 Latin-1 解读" 的必然产物。
        bool c1Found = false;
        for (const auto& item : config.items)
        {
            for (const wchar_t ch : item.title)
            {
                c1Found = c1Found || (ch >= 0x80 && ch <= 0x9F);
            }
            for (const auto& child : item.children)
            {
                for (const wchar_t ch : child.title)
                {
                    c1Found = c1Found || (ch >= 0x80 && ch <= 0x9F);
                }
            }
        }
        Check(!c1Found, "标题不含 C1 控制符（UTF-8 误读指纹）");

        // ③ 字面 UTF-8 与 \uXXXX 混排（验证 pending 缓冲在转义前正确收尾）
        bdshell::MenuConfig mixed;
        std::wstring error;
        const bool mixedOk = bdshell::ParseConfigText(
            "{\"items\":[{\"id\":\"a\",\"title\":\"中\\u6587-中\",\"action\":\"x\"}]}",
            &mixed, &error);
        Check(mixedOk && !mixed.items.empty() && mixed.items[0].title == L"中文-中",
            "字面 UTF-8 与 \\uXXXX 混排 == L\"中文-中\"");

        // ④ \uXXXX 代理对 → U+1F600
        bdshell::MenuConfig surrogate;
        const bool surrogateOk = bdshell::ParseConfigText(
            "{\"items\":[{\"id\":\"a\",\"title\":\"\\uD83D\\uDE00\",\"action\":\"x\"}]}",
            &surrogate, &error);
        Check(surrogateOk && !surrogate.items.empty() &&
              surrogate.items[0].title == L"\U0001F600",
            "\\uXXXX 代理对 == U+1F600");

        // ⑤ 字面 4 字节 UTF-8（emoji 直接写在配置里）→ U+1F600
        std::string rawEmoji = "{\"items\":[{\"id\":\"a\",\"title\":\"";
        rawEmoji += "\xF0\x9F\x98\x80";
        rawEmoji += "\",\"action\":\"x\"}]}";
        bdshell::MenuConfig rawEmojiConfig;
        const bool rawEmojiOk = bdshell::ParseConfigText(rawEmoji, &rawEmojiConfig, &error);
        Check(rawEmojiOk && !rawEmojiConfig.items.empty() &&
              rawEmojiConfig.items[0].title == L"\U0001F600",
            "字面 4 字节 UTF-8 == U+1F600");

        // ⑥ 非法 UTF-8 不得抛异常、不得中断解析（替换为 U+FFFD 后继续）
        bdshell::MenuConfig broken;
        const bool brokenOk = bdshell::ParseConfigText(
            "{\"items\":[{\"id\":\"a\",\"title\":\"a\xFF\xFE" "b\",\"action\":\"x\"}]}",
            &broken, &error);
        Check(brokenOk && !broken.items.empty() && broken.items[0].title.size() >= 3,
            "非法 UTF-8 字节 → 容错替换，不失败不崩溃");
    }

    // 可选：解析外部配置文件（诊断用）
    if (argc >= 2 && argv[1] != nullptr)
    {
        std::printf("[8] 外部配置: %ls\n", argv[1]);
        const HANDLE file = ::CreateFileW(argv[1], GENERIC_READ, FILE_SHARE_READ, nullptr,
            OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            std::printf("  读取失败\n");
            ++g_failures;
        }
        else
        {
            DWORD size = ::GetFileSize(file, nullptr);
            std::string bytes;
            if (size > 0 && size < (1u << 20))
            {
                bytes.resize(size);
                DWORD read = 0;
                ::ReadFile(file, bytes.data(), size, &read, nullptr);
                bytes.resize(read);
            }
            ::CloseHandle(file);

            bdshell::MenuConfig external;
            std::wstring error;
            if (bdshell::ParseConfigText(bytes, &external, &error))
            {
                std::printf("  解析成功: 顶级项=%zu extensionEnabled=%s\n",
                    external.items.size(), external.extensionEnabled ? "true" : "false");
                for (const auto& item : external.items)
                {
                    std::printf("    - %ls (%zu 子项)\n", item.title.c_str(), item.children.size());
                }
            }
            else
            {
                std::printf("  解析失败: %ls\n", error.c_str());
                ++g_failures;
            }
        }
    }

    std::printf("\n合计: %d 项断言, 失败 %d\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
