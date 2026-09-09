# verify-no-cross-assembly-event.Tests.ps1
# Pester 测试：verify-no-cross-assembly-event.ps1 门禁脚本（兼容 Pester 3.x）
# 用法：pwsh -NoProfile -Command "Invoke-Pester scripts/verify-no-cross-assembly-event.Tests.ps1"

$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'verify-no-cross-assembly-event.ps1'

function New-TestRoot {
    $root = Join-Path ([System.IO.Path]::GetTempPath()) "gate-test-$([System.Guid]::NewGuid().ToString('N').Substring(0,8))"
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    return $root
}

function New-TestProj([string]$root, [string]$name, [string]$code) {
    $dir = Join-Path $root $name
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    Set-Content (Join-Path $dir "$name.cs") $code -Encoding UTF8
    Set-Content (Join-Path $dir "$name.csproj") '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>' -Encoding UTF8
    return $dir
}

Describe 'verify-no-cross-assembly-event' {
    Context '无跨程序集 event 时' {
        It '应返回退出码 0（通过）' {
            $root = New-TestRoot
            try {
                New-TestProj $root 'ProjA' 'namespace A; public interface IFoo { void Do(); }'
                New-TestProj $root 'ProjB' 'namespace B; using A; public class C { public void Use(IFoo f) { f.Do(); } }'
                $output = & pwsh -NoProfile -ExecutionPolicy Bypass $scriptPath -RepoRoot $root 2>&1
                $LASTEXITCODE | Should Be 0
                ($output -join "`n") | Should Match 'PASS'
            } finally { Remove-Item $root -Recurse -Force }
        }
    }

    Context '存在跨程序集 interface event 订阅时' {
        It '应返回退出码 1（违规）' {
            $root = New-TestRoot
            try {
                New-TestProj $root 'ProjA2' 'namespace A2; using System; public interface IBar { event EventHandler? SomethingHappened; }'
                New-TestProj $root 'ProjB2' 'namespace B2; using A2; public class S { public void Hook(IBar b) { b.SomethingHappened += (_, _) => { }; } }'
                $output = & pwsh -NoProfile -ExecutionPolicy Bypass $scriptPath -RepoRoot $root 2>&1
                $LASTEXITCODE | Should Be 1
                ($output -join "`n") | Should Match 'SomethingHappened'
            } finally { Remove-Item $root -Recurse -Force }
        }
    }

    Context '同程序集 event 订阅时' {
        It '不应报违规（白名单）' {
            $root = New-TestRoot
            try {
                New-TestProj $root 'ProjA3' @'
namespace A3; using System;
public interface ILocal { event EventHandler? LocalEvent; }
public class LocalSub { public void Hook(ILocal l) { l.LocalEvent += (_, _) => { }; } }
'@
                $output = & pwsh -NoProfile -ExecutionPolicy Bypass $scriptPath -RepoRoot $root 2>&1
                $LASTEXITCODE | Should Be 0
            } finally { Remove-Item $root -Recurse -Force }
        }
    }
}
