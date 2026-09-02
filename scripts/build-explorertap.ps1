# build-explorertap.ps1 — 本地编译 ExplorerTAP.dll（Win11 任务栏注入桥）
# 依据：参考\TranslucentTB-release（TTB 完整 C++ 源码，含 ExplorerTAP.vcxproj）。
# 2026-09-01 实测跑通。产物部署到 packages/shell/Taskbar/native/（csproj 随构建分发）。
#
# 依赖：VS2022（含 MSVC v143 + Windows SDK 10.0.26100）、nuget.exe、git。
# 网络受限时的取源路径（github 443 不通时）：
#   - NuGet 包走 nuget.org（可达）
#   - Detours / wil 源码走 jsdelivr CDN：data.jsdelivr.com 列文件树 + cdn.jsdelivr.net 逐文件拉
#
# 用法：powershell -File scripts\build-explorertap.ps1

$ErrorActionPreference = "Stop"

$RepoRoot   = Split-Path $PSScriptRoot -Parent            # better-desktop-cordis
$TtbSrc     = "C:\ttb-src"                                # ASCII 路径（中文路径会触发 MDMERGE MDM2012）
$VcVars     = "D:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
$MSBuild    = "D:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
$MSVCVer    = "14.44.35207"                               # 本机已装的 MSVC 工具集
$NuGet      = "C:\Users\17822\Desktop\betterdt\参考\nuget.exe"
$DetoursRef = "4b8c659f549b0ab21cf649377c7a84eb708f5e68"  # TTB overlay port 固定的 Detours commit

# ---------- 0. 源码就位（从 参考\TranslucentTB-release 复制到 ASCII 路径） ----------
$src = "C:\Users\17822\Desktop\betterdt\参考\TranslucentTB-release\TranslucentTB-release"
if (-not (Test-Path "$TtbSrc\ExplorerTAP\ExplorerTAP.vcxproj")) {
    robocopy $src $TtbSrc /E /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "源码复制失败" }
}

# ---------- 1. NuGet 还原（CppWinRT / Windows SDK CPP 26100 等） ----------
& $NuGet restore "$TtbSrc\ExplorerTAP\packages.config" `
    -PackagesDirectory "$TtbSrc\packages" -NonInteractive

# ---------- 2. Detours / wil 源码（jsdelivr CDN，绕过 github 直连） ----------
function Pull-Tree($repo, $commit, $subdir, $dest) {
    if (Test-Path $dest) { return }
    $tree = Invoke-RestMethod -Uri "https://data.jsdelivr.com/v1/packages/gh/$repo@$commit" -TimeoutSec 30
    $all = New-Object System.Collections.Generic.List[string]
    function Walk($nodes, $prefix) {
        foreach ($n in $nodes) {
            $p = if ($prefix) { "$prefix/$($n.name)" } else { $n.name }
            if ($n.type -eq "directory") { Walk $n.files $p } else { $all.Add($p) }
        }
    }
    Walk $tree.files ""
    $picked = $all | Where-Object { $_.StartsWith($subdir) }
    foreach ($f in $picked) {
        $d = Join-Path $dest ($f.Substring($subdir.Length).TrimStart('/'))
        $dir = Split-Path $d -Parent
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        Invoke-WebRequest -Uri "https://cdn.jsdelivr.net/gh/$repo@$commit/$f" -OutFile $d -UseBasicParsing -TimeoutSec 30
    }
    Write-Host "$repo -> $($picked.Count) 文件"
}
Pull-Tree "microsoft/Detours" $DetoursRef "src"      "C:\ttb-src\_deps\Detours\src"
Pull-Tree "microsoft/wil"     "master"       "include" "C:\ttb-src\_deps\wil\include"
# cppwinrt 专用头若缺失可从 wil release tag 补（jsdelivr 对 tag 索引更全）

# ---------- 3. 手动编译 detours.lib（detours.h 布局需 vcpkg 风格子目录） ----------
$msvc    = "D:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\MSVC\$MSVCVer"
$sdk     = "C:\Program Files (x86)\Windows Kits\10"
$sdkVer  = "10.0.26100.0"
$cl      = "$msvc\bin\Hostx64\x64"
$out     = "C:\ttb-src\_deps\Detours\build"
New-Item -ItemType Directory -Force -Path "$out\include\detours" | Out-Null
Copy-Item "C:\ttb-src\_deps\Detours\src\detours.h","C:\ttb-src\_deps\Detours\src\detver.h" "$out\include\detours\" -Force

$env:INCLUDE = "$msvc\include;$sdk\Include\$sdkVer\shared;$sdk\Include\$sdkVer\um;$sdk\Include\$sdkVer\winrt;$sdk\Include\$sdkVer\cppwinrt;$sdk\Include\$sdkVer\ucrt"
$env:LIB     = "$msvc\lib\x64;$sdk\Lib\$sdkVer\um\x64;$sdk\Lib\$sdkVer\ucrt\x64"

Push-Location "C:\ttb-src\_deps\Detours\src"
# ⚠️ /guard:ehcont 必须与 ExplorerTAP 工程一致，否则链接 LNK4291/LNK1218；
#    /Qspectre 需要 SDK 的 guardcfw.h，本机 SDK 无此头故不加（仅影响 spectre 缓解深度）。
& "$cl\cl.exe" /nologo /c /O2 /W3 /EHsc /Zi /guard:cf /guard:ehcont /ZH:SHA_256 `
    "/DDETOURS_64BIT=1" "/DDETOURS_32BIT=0" "/D_WIN32_WINNT=0x0601" "/DDETOURS_VERSION=4.0.1" `
    creatwth.cpp detours.cpp disasm.cpp disolarm.cpp disolarm64.cpp disolia64.cpp disolx64.cpp disolx86.cpp image.cpp modules.cpp "/Fo$out\"
& "$cl\lib.exe" /nologo "/OUT:$out\detours.lib" "$out\*.obj"
Pop-Location

# ---------- 4. 编译 ExplorerTAP.dll（经环境变量注入 include/lib 路径） ----------
$env:IncludePath = "C:\ttb-src\_deps\wil\include;C:\ttb-src\_deps\Detours\build\include;$msvc\include;$sdk\Include\$sdkVer\shared;$sdk\Include\$sdkVer\um;$sdk\Include\$sdkVer\winrt;$sdk\Include\$sdkVer\cppwinrt;$sdk\Include\$sdkVer\ucrt"
$env:LibraryPath = "C:\ttb-src\_deps\Detours\build;$msvc\lib\x64;$sdk\Lib\$sdkVer\um\x64;$sdk\Lib\$sdkVer\ucrt\x64"

$rsp = "C:\ttb-src\build.rsp"
@(
    "$TtbSrc\ExplorerTAP\ExplorerTAP.vcxproj"
    "/p:Configuration=Release"
    "/p:Platform=x64"
    "/p:SpectreMitigation=false"   # 本机未装 Spectre 缓解库组件（MSB8040 时必需）
    "/m"; "/v:m"; "/nologo"
) | Set-Content $rsp
& $MSBuild "@$rsp"

# ---------- 5. 部署 ----------
$product = "$TtbSrc\ExplorerTAP\x64\Release\ExplorerTAP.dll"
if (-not (Test-Path $product)) { throw "编译产物缺失" }
$dst = Join-Path $RepoRoot "packages\shell\Taskbar\native\ExplorerTAP.dll"
Copy-Item $product $dst -Force
Write-Host "✔ ExplorerTAP.dll 已部署: $dst"
Write-Host "注意：host bin 根目录的那份若被 explorer 锁定（注入中）无法覆盖，属正常；"
Write-Host "桥检索顺序 BaseDirectory→native→x64，explorer 重启后轮换到新版本。"
