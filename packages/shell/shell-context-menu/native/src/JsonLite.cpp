// BetterDesktopShellMenu — 极简 JSON 解析实现（见 JsonLite.h 说明）

#include "JsonLite.h"

#include <windows.h>
#include <cstdlib>
#include <cmath>

namespace bdshell::json
{
    namespace
    {
        // 解析器：严格递归下降。深度上限防恶意/损坏文件造成栈溢出（本文件只读我们自己的配置）。
        constexpr int kMaxDepth = 32;

        class Parser
        {
        public:
            Parser(const std::string& text) : _text(text) {}

            ValuePtr Run(std::wstring* error)
            {
                SkipWs();
                ValuePtr root = ParseValue(0);
                if (!root)
                {
                    *error = _error.empty() ? L"JSON 解析失败" : _error;
                    return nullptr;
                }
                SkipWs();
                if (_pos != _text.size())
                {
                    *error = L"JSON 根值之后存在多余内容";
                    return nullptr;
                }
                return root;
            }

        private:
            ValuePtr ParseValue(int depth)
            {
                if (depth > kMaxDepth)
                {
                    Fail(L"JSON 嵌套层数超限");
                    return nullptr;
                }
                if (_pos >= _text.size())
                {
                    Fail(L"JSON 意外结束");
                    return nullptr;
                }

                const char c = _text[_pos];
                switch (c)
                {
                case '{': return ParseObject(depth);
                case '[': return ParseArray(depth);
                case '"': return ParseStringValue();
                case 't': case 'f': return ParseBool();
                case 'n': return ParseNull();
                default: return ParseNumber();
                }
            }

            ValuePtr ParseObject(int depth)
            {
                ++_pos; // '{'
                auto value = std::make_shared<Value>();
                value->type = Value::Type::Object;
                SkipWs();
                if (_pos < _text.size() && _text[_pos] == '}')
                {
                    ++_pos;
                    return value;
                }
                for (;;)
                {
                    SkipWs();
                    if (_pos >= _text.size() || _text[_pos] != '"')
                    {
                        Fail(L"JSON 对象的键必须是字符串");
                        return nullptr;
                    }
                    std::wstring key;
                    if (!ParseString(key))
                    {
                        return nullptr;
                    }
                    SkipWs();
                    if (_pos >= _text.size() || _text[_pos] != ':')
                    {
                        Fail(L"JSON 对象缺少 ':'");
                        return nullptr;
                    }
                    ++_pos;
                    SkipWs();
                    ValuePtr child = ParseValue(depth + 1);
                    if (!child)
                    {
                        return nullptr;
                    }
                    value->object.emplace_back(std::move(key), std::move(child));
                    SkipWs();
                    if (_pos < _text.size() && _text[_pos] == ',')
                    {
                        ++_pos;
                        continue;
                    }
                    if (_pos < _text.size() && _text[_pos] == '}')
                    {
                        ++_pos;
                        return value;
                    }
                    Fail(L"JSON 对象缺少 ',' 或 '}'");
                    return nullptr;
                }
            }

            ValuePtr ParseArray(int depth)
            {
                ++_pos; // '['
                auto value = std::make_shared<Value>();
                value->type = Value::Type::Array;
                SkipWs();
                if (_pos < _text.size() && _text[_pos] == ']')
                {
                    ++_pos;
                    return value;
                }
                for (;;)
                {
                    SkipWs();
                    ValuePtr child = ParseValue(depth + 1);
                    if (!child)
                    {
                        return nullptr;
                    }
                    value->array.push_back(std::move(child));
                    SkipWs();
                    if (_pos < _text.size() && _text[_pos] == ',')
                    {
                        ++_pos;
                        continue;
                    }
                    if (_pos < _text.size() && _text[_pos] == ']')
                    {
                        ++_pos;
                        return value;
                    }
                    Fail(L"JSON 数组缺少 ',' 或 ']'");
                    return nullptr;
                }
            }

            ValuePtr ParseStringValue()
            {
                std::wstring text;
                if (!ParseString(text))
                {
                    return nullptr;
                }
                auto value = std::make_shared<Value>();
                value->type = Value::Type::String;
                value->str = std::move(text);
                return value;
            }

            bool ParseString(std::wstring& out)
            {
                ++_pos; // 开引号
                out.clear();

                // ⚠️ UTF-8 解码红线（2026-09-11 实测踩坑，勿回退）：
                //   配置是「无 BOM UTF-8」，一个中文字符占 3 字节。绝不能把字节逐个
                //   static_cast<wchar_t> 塞进宽串——那会把 E6 A1 8C 变成 U+00E6 U+00A1 U+008C，
                //   explorer 于是画出「æ¡Œé¢æŽ§åˆ¶」乱码（用户实际看到的形态）。
                //   正确做法：原始字节先进 pending，遇到段边界（收尾引号 / 转义前）整体走
                //   MultiByteToWideChar(CP_UTF8) 解码。
                //
                //   附带坑：printf("%ls") 在 MSVC 默认 "C" locale 下按字节截断输出，恰好把
                //   乱码宽串还原成原始 UTF-8 字节，于是**探针输出看着完全正确**。所以
                //   "探针显示对" 不能证明解析对——回归断言必须比宽字符串本身。
                std::string pending;
                const auto flushPending = [&out, &pending]() {
                    if (!pending.empty())
                    {
                        out += Utf8ToWide(pending);
                        pending.clear();
                    }
                };

                while (_pos < _text.size())
                {
                    const char c = _text[_pos++];
                    if (c == '"')
                    {
                        flushPending();
                        return true;
                    }
                    if (c != '\\')
                    {
                        pending.push_back(c);
                        continue;
                    }

                    // 转义序列恒为 ASCII；先收尾已攒字节，避免与 \uXXXX 产出的码元交错。
                    flushPending();

                    if (_pos >= _text.size())
                    {
                        break;
                    }
                    const char esc = _text[_pos++];
                    switch (esc)
                    {
                    case '"': out.push_back(L'"'); break;
                    case '\\': out.push_back(L'\\'); break;
                    case '/': out.push_back(L'/'); break;
                    case 'b': out.push_back(L'\b'); break;
                    case 'f': out.push_back(L'\f'); break;
                    case 'n': out.push_back(L'\n'); break;
                    case 'r': out.push_back(L'\r'); break;
                    case 't': out.push_back(L'\t'); break;
                    case 'u':
                    {
                        unsigned code = 0;
                        if (!ParseHex4(code))
                        {
                            return false;
                        }
                        // 代理对：高代理 D800-DBFF 需紧随低代理 DC00-DFFF
                        if (code >= 0xD800 && code <= 0xDBFF)
                        {
                            if (_pos + 1 < _text.size() && _text[_pos] == '\\' && _text[_pos + 1] == 'u')
                            {
                                _pos += 2;
                                unsigned low = 0;
                                if (!ParseHex4(low))
                                {
                                    return false;
                                }
                                if (low >= 0xDC00 && low <= 0xDFFF)
                                {
                                    const unsigned combined =
                                        0x10000 + ((code - 0xD800) << 10) + (low - 0xDC00);
                                    out.push_back(static_cast<wchar_t>(
                                        0xD800 + ((combined - 0x10000) >> 10)));
                                    out.push_back(static_cast<wchar_t>(
                                        0xDC00 + ((combined - 0x10000) & 0x3FF)));
                                    break;
                                }
                                out.push_back(static_cast<wchar_t>(code));
                                out.push_back(static_cast<wchar_t>(low));
                                break;
                            }
                        }
                        out.push_back(static_cast<wchar_t>(code));
                        break;
                    }
                    default:
                        Fail(L"JSON 字符串含非法转义");
                        return false;
                    }
                }
                Fail(L"JSON 字符串未闭合");
                return false;
            }

            bool ParseHex4(unsigned& out) noexcept
            {
                if (_pos + 4 > _text.size())
                {
                    Fail(L"JSON \\u 转义不完整");
                    return false;
                }
                unsigned value = 0;
                for (int i = 0; i < 4; ++i)
                {
                    const char c = _text[_pos++];
                    value <<= 4;
                    if (c >= '0' && c <= '9') { value |= static_cast<unsigned>(c - '0'); }
                    else if (c >= 'a' && c <= 'f') { value |= static_cast<unsigned>(c - 'a' + 10); }
                    else if (c >= 'A' && c <= 'F') { value |= static_cast<unsigned>(c - 'A' + 10); }
                    else
                    {
                        Fail(L"JSON \\u 转义含非十六进制字符");
                        return false;
                    }
                }
                out = value;
                return true;
            }

            ValuePtr ParseBool()
            {
                if (_text.compare(_pos, 4, "true") == 0)
                {
                    _pos += 4;
                    auto value = std::make_shared<Value>();
                    value->type = Value::Type::Bool;
                    value->boolean = true;
                    return value;
                }
                if (_text.compare(_pos, 5, "false") == 0)
                {
                    _pos += 5;
                    auto value = std::make_shared<Value>();
                    value->type = Value::Type::Bool;
                    value->boolean = false;
                    return value;
                }
                Fail(L"JSON 非法字面量");
                return nullptr;
            }

            ValuePtr ParseNull()
            {
                if (_text.compare(_pos, 4, "null") == 0)
                {
                    _pos += 4;
                    return std::make_shared<Value>();
                }
                Fail(L"JSON 非法字面量");
                return nullptr;
            }

            ValuePtr ParseNumber()
            {
                const size_t start = _pos;
                if (_pos < _text.size() && (_text[_pos] == '-' || _text[_pos] == '+'))
                {
                    ++_pos;
                }
                bool anyDigit = false;
                while (_pos < _text.size() && _text[_pos] >= '0' && _text[_pos] <= '9')
                {
                    ++_pos;
                    anyDigit = true;
                }
                if (_pos < _text.size() && _text[_pos] == '.')
                {
                    ++_pos;
                    while (_pos < _text.size() && _text[_pos] >= '0' && _text[_pos] <= '9')
                    {
                        ++_pos;
                        anyDigit = true;
                    }
                }
                if (anyDigit && _pos < _text.size() && (_text[_pos] == 'e' || _text[_pos] == 'E'))
                {
                    ++_pos;
                    if (_pos < _text.size() && (_text[_pos] == '-' || _text[_pos] == '+'))
                    {
                        ++_pos;
                    }
                    while (_pos < _text.size() && _text[_pos] >= '0' && _text[_pos] <= '9')
                    {
                        ++_pos;
                    }
                }
                if (!anyDigit)
                {
                    Fail(L"JSON 期望一个值");
                    return nullptr;
                }

                auto value = std::make_shared<Value>();
                value->type = Value::Type::Number;
                value->number = std::strtod(_text.substr(start, _pos - start).c_str(), nullptr);
                return value;
            }

            void SkipWs() noexcept
            {
                while (_pos < _text.size())
                {
                    const char c = _text[_pos];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                    {
                        ++_pos;
                        continue;
                    }
                    break;
                }
            }

            void Fail(const wchar_t* reason)
            {
                if (_error.empty())
                {
                    _error = reason;
                    _error += L"（偏移 ";
                    _error += std::to_wstring(_pos);
                    _error += L"）";
                }
            }

            const std::string& _text;
            size_t _pos = 0;
            std::wstring _error;
        };
    }

    const Value* Value::Find(const wchar_t* key) const noexcept
    {
        if (type != Type::Object || key == nullptr)
        {
            return nullptr;
        }
        for (const auto& pair : object)
        {
            if (pair.first == key)
            {
                return pair.second.get();
            }
        }
        return nullptr;
    }

    std::wstring Value::GetString(const wchar_t* key, const std::wstring& fallback) const
    {
        const Value* child = Find(key);
        return (child != nullptr && child->type == Type::String) ? child->str : fallback;
    }

    bool Value::GetBool(const wchar_t* key, bool fallback) const noexcept
    {
        const Value* child = Find(key);
        return (child != nullptr && child->type == Type::Bool) ? child->boolean : fallback;
    }

    int Value::GetInt(const wchar_t* key, int fallback) const noexcept
    {
        const Value* child = Find(key);
        return (child != nullptr && child->type == Type::Number)
            ? static_cast<int>(child->number)
            : fallback;
    }

    const Value* Value::GetArray(const wchar_t* key) const noexcept
    {
        const Value* child = Find(key);
        return (child != nullptr && child->type == Type::Array) ? child : nullptr;
    }

    ValuePtr Parse(const std::string& utf8, std::wstring* error)
    {
        if (error == nullptr)
        {
            return nullptr;
        }
        error->clear();
        // 容忍宿主写入时可能带的 UTF-8 BOM（防上游实现变动导致整份配置被静默判为损坏）。
        std::string text = utf8;
        if (text.size() >= 3 && static_cast<unsigned char>(text[0]) == 0xEF &&
            static_cast<unsigned char>(text[1]) == 0xBB && static_cast<unsigned char>(text[2]) == 0xBF)
        {
            text.erase(0, 3);
        }
        Parser parser(text);
        return parser.Run(error);
    }

    std::wstring Utf8ToWide(const std::string& text)
    {
        if (text.empty())
        {
            return std::wstring();
        }
        const int needed = ::MultiByteToWideChar(
            CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), nullptr, 0);
        if (needed <= 0)
        {
            return std::wstring();
        }
        std::wstring wide(static_cast<size_t>(needed), L'\0');
        ::MultiByteToWideChar(
            CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), wide.data(), needed);
        return wide;
    }

    std::string WideToUtf8(const std::wstring& text)
    {
        if (text.empty())
        {
            return std::string();
        }
        const int needed = ::WideCharToMultiByte(
            CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
        if (needed <= 0)
        {
            return std::string();
        }
        std::string utf8(static_cast<size_t>(needed), '\0');
        ::WideCharToMultiByte(
            CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), utf8.data(), needed, nullptr, nullptr);
        return utf8;
    }
}
