// BetterDesktopShellMenu — 极简 JSON 解析（只读、无第三方依赖）
//
// 为什么自己写：本 DLL 由 explorer / dllhost 加载，要求「零第三方依赖 + 极小体积 + 不抛异常」。
// 只支持 JSON 标准子集（对象/数组/字符串/数字/true/false/null），足够解析宿主写出的
// shellmenu.json；解析失败一律返回 false 并把原因写进 error，绝不抛异常出边界。

#pragma once

#include <string>
#include <vector>
#include <memory>
#include <cstdint>

namespace bdshell::json
{
    class Value;
    using ValuePtr = std::shared_ptr<Value>;

    class Value
    {
    public:
        enum class Type { Null, Bool, Number, String, Array, Object };

        Value() = default;

        Type type = Type::Null;
        bool boolean = false;
        double number = 0.0;
        std::wstring str;
        std::vector<ValuePtr> array;
        std::vector<std::pair<std::wstring, ValuePtr>> object;

        bool IsObject() const noexcept { return type == Type::Object; }
        bool IsArray() const noexcept { return type == Type::Array; }
        bool IsString() const noexcept { return type == Type::String; }
        bool IsBool() const noexcept { return type == Type::Bool; }

        // 对象取成员；不存在或类型不符返回 nullptr。
        const Value* Find(const wchar_t* key) const noexcept;
        const Value* Find(const std::wstring& key) const noexcept { return Find(key.c_str()); }

        // 便捷取值（缺省即默认值，绝不抛）。
        std::wstring GetString(const wchar_t* key, const std::wstring& fallback = L"") const;
        bool GetBool(const wchar_t* key, bool fallback = false) const noexcept;
        int GetInt(const wchar_t* key, int fallback = 0) const noexcept;
        const Value* GetArray(const wchar_t* key) const noexcept;
    };

    // 解析 UTF-8 文本。失败返回 nullptr，error 填原因（中文，便于直接落日志）。
    ValuePtr Parse(const std::string& utf8, std::wstring* error);

    // UTF-8 → UTF-16（配置文件用无 BOM UTF-8 写入）。
    std::wstring Utf8ToWide(const std::string& text);

    // UTF-16 → UTF-8（写临时批文件用）。
    std::string WideToUtf8(const std::wstring& text);
}
