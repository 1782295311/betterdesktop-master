# 门禁 architecture-guard 单测（Pester）
# 覆盖：拒绝业务 UI / 豁免 App.xaml / 排除 obj、bin
$sut = Join-Path $PSScriptRoot 'verify-architecture-guard.ps1'
. $sut

Describe 'Get-BusinessXamlPaths' {
    It '返回业务 UI，豁免 App.xaml' {
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('gates-arch-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path (Join-Path $tmp 'Views') -Force | Out-Null
        New-Item -ItemType File -Path (Join-Path $tmp 'App.xaml') -Force | Out-Null
        New-Item -ItemType File -Path (Join-Path $tmp 'Views\DesktopWindow.xaml') -Force | Out-Null
        New-Item -ItemType File -Path (Join-Path $tmp 'Views\MainWindow.xaml') -Force | Out-Null

        $result = @(Get-BusinessXamlPaths $tmp)
        @($result | Where-Object { $_ -like '*DesktopWindow.xaml' }).Count | Should Be 1
        @($result | Where-Object { $_ -like '*MainWindow.xaml' }).Count | Should Be 1
        @($result | Where-Object { $_ -like '*App.xaml' }).Count | Should Be 0

        Remove-Item -Recurse -Force $tmp
    }

    It '排除 obj、bin 下的 .xaml' {
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('gates-arch-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path (Join-Path $tmp 'obj') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $tmp 'bin') -Force | Out-Null
        New-Item -ItemType File -Path (Join-Path $tmp 'App.xaml') -Force | Out-Null
        New-Item -ItemType File -Path (Join-Path $tmp 'obj\Foo.g.xaml') -Force | Out-Null
        New-Item -ItemType File -Path (Join-Path $tmp 'bin\Bar.xaml') -Force | Out-Null

        @(Get-BusinessXamlPaths $tmp).Count | Should Be 0

        Remove-Item -Recurse -Force $tmp
    }

    It '空目录返回空数组' {
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('gates-arch-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $tmp -Force | Out-Null

        @(Get-BusinessXamlPaths $tmp).Count | Should Be 0

        Remove-Item -Recurse -Force $tmp
    }
}