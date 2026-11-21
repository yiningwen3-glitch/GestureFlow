using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace GestureFlow.Desktop.Applications;

public sealed class WindowsShellApplicationSource : IApplicationDiscoverySource
{
    private sealed record ShellRow(string Key, string Name, string Kind, string? Path = null,
        string? Aumid = null, string? Family = null);

    private sealed record ShellResult(ShellRow[] Items, string[] Failures);

    public async Task<DiscoveryBatch> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var command = Convert.ToBase64String(Encoding.Unicode.GetBytes(DiscoveryScript));
        var powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var start = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", command })
            start.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动应用清单查询。");
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            using var stopOnCancellation = timeout.Token.Register(() => StopQuery(process));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await errors.ConfigureAwait(false);
                var json = await output.ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                    return Unavailable();

                var result = JsonSerializer.Deserialize<ShellResult>(json);
                if (result is null)
                    return Unavailable();

                return new(result.Items.Select(ToObservation).OfType<ApplicationObservation>().ToArray(), result.Failures);
            }
            catch (OperationCanceledException)
            {
                await Task.WhenAll(output, errors).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new([], ["桌面、开始菜单与 MSIX/Appx 查询超时。"]);
            }
        }
        catch (Exception error) when (error is Win32Exception or IOException or JsonException or InvalidOperationException)
        {
            return Unavailable();
        }
    }

    private static void StopQuery(Process process)
    {
        try { process.Kill(); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
    }

    private static DiscoveryBatch Unavailable() => new([], ["桌面、开始菜单或 MSIX/Appx 查询不可用。"]);

    private static ApplicationObservation? ToObservation(ShellRow row)
    {
        var path = ApplicationIdentity.NormalizeExecutable(row.Path);
        if (row.Kind != "package" && !RegistryApplicationSource.IsLocalFile(path))
            path = null;
        if ((row.Kind == "startPath" || (row.Kind == "desktop" && string.IsNullOrWhiteSpace(row.Aumid))) && path is null)
            return null;

        var source = row.Kind switch
        {
            "desktop" => DiscoverySource.Desktop,
            "package" => DiscoverySource.Appx,
            _ => DiscoverySource.StartMenu
        };
        var icon = row.Aumid is { Length: > 0 } aumid
            ? new ApplicationIcon(@"shell:AppsFolder\" + aumid, IsShellItem: true)
            : path is not null ? new ApplicationIcon(path) : null;
        return new(row.Key, row.Name, source, path,
            PackageFamilyName: row.Family ?? ForegroundApplicationService.FamilyFromAumid(row.Aumid),
            Aumid: row.Aumid, Icon: icon);
    }

    private const string DiscoveryScript = """
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $items = [Collections.Generic.List[object]]::new()
        $failures = [Collections.Generic.List[string]]::new()
        $startNames = @{}
        try {
            $links = New-Object -ComObject WScript.Shell
            $shell = New-Object -ComObject Shell.Application
            $folders = @([Environment]::GetFolderPath('DesktopDirectory'), [Environment]::GetFolderPath('CommonDesktopDirectory'))
            foreach ($folder in $folders | Where-Object { $_ } | Select-Object -Unique) {
                foreach ($file in @(Get-ChildItem -LiteralPath $folder -File -ErrorAction Continue)) {
                    if ($file.Extension -eq '.exe') {
                        $items.Add(@{Key="desktop:$($file.FullName)";Name=$file.BaseName;Kind='desktop';Path=$file.FullName})
                        continue
                    }
                    if ($file.Extension -ne '.lnk') { continue }

                    try {
                        $shortcut = $links.CreateShortcut($file.FullName)
                        $target = [string]$shortcut.TargetPath
                        $item = $shell.Namespace($folder).ParseName($file.Name)
                        $id = [string]$item.ExtendedProperty('System.AppUserModel.ID')
                        if ($shortcut.Arguments -match '^"?shell:AppsFolder\\([^"]+)"?$') {
                            $id = $Matches[1]
                            $target = ''
                        } elseif ($target -notmatch '(?i)\.exe$' -or
                            $target -match '(?i)\\(msiexec|rundll32|cmd|powershell|pwsh|wscript|cscript|uninstall|unins\d*|setup|update)\.exe$' -or
                            ($shortcut.Arguments -and $target -match '(?i)\\explorer\.exe$')) {
                            $target = ''
                        }
                        if ($target -or $id) {
                            $items.Add(@{Key="desktop:$($file.FullName)";Name=$file.BaseName;Kind='desktop';Path=$target;Aumid=$id})
                        }
                    } catch { $failures.Add("无法读取桌面快捷方式：$($file.BaseName)") }
                }
            }
        } catch { $failures.Add('部分桌面项目无法读取。') }
        try {
            foreach ($entry in @(Get-StartApps)) {
                $id = [string]$entry.AppID
                $name = [string]$entry.Name
                if (!$id -or !$name) { continue }
                $startNames[$id] = $name
                if ($id -match '^([^!]+_[A-Za-z0-9]{13})!.+$') {
                    $items.Add(@{Key="start:$id";Name=$name;Kind='start';Aumid=$id;Family=$Matches[1]})
                } elseif ($id -match '^[A-Za-z]:\\.+\.exe$') {
                    $items.Add(@{Key="start:$id";Name=$name;Kind='startPath';Path=$id})
                }
            }
        } catch { $failures.Add('部分 Windows 开始菜单条目无法读取。') }
        try {
            $links = New-Object -ComObject WScript.Shell
            $shell = New-Object -ComObject Shell.Application
            foreach ($folder in @([Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('CommonPrograms'))) {
                if (!$folder) { continue }
                foreach ($file in @(Get-ChildItem -LiteralPath $folder -Filter '*.lnk' -File -Recurse -ErrorAction SilentlyContinue)) {
                    try {
                        $shortcut = $links.CreateShortcut($file.FullName)
                        $target = [string]$shortcut.TargetPath
                        $item = $shell.Namespace($file.DirectoryName).ParseName($file.Name)
                        $id = [string]$item.ExtendedProperty('System.AppUserModel.ID')
                        if ($target -match '(?i)\\(msiexec|rundll32|cmd|powershell|pwsh|wscript|cscript|uninstall|unins\d*|setup|update)\.exe$' -or
                            ($shortcut.Arguments -and $target -match '(?i)\\explorer\.exe$')) {
                            $target = ''
                        }
                        $items.Add(@{Key="link:$($file.FullName)";Name=$file.BaseName;Kind='start';Path=$target;Aumid=$id})
                    } catch {
                        $items.Add(@{Key="link:$($file.FullName)";Name=$file.BaseName;Kind='start'})
                    }
                }
            }
        } catch { $failures.Add('部分开始菜单快捷方式无法读取。') }
        try {
            foreach ($package in @(Get-AppxPackage -PackageTypeFilter Main,Optional)) {
                $family = [string]$package.PackageFamilyName
                if (!$family) { continue }
                try {
                    $manifest = Get-AppxPackageManifest -Package $package.PackageFullName
                    foreach ($app in @($manifest.Package.Applications.Application)) {
                        if (!$app -or !$app.Id) { continue }
                        $id = "$family!$($app.Id)"
                        $name = $startNames[$id]
                        if (!$name) {
                            $name = [string]$app.VisualElements.DisplayName
                            if (!$name -or $name.StartsWith('ms-resource:', [StringComparison]::OrdinalIgnoreCase)) {
                                $name = if (@($manifest.Package.Applications.Application).Count -gt 1) {
                                    "$($package.Name) · $($app.Id)"
                                } else { [string]$package.Name }
                            }
                        }
                        $exe = $null
                        if ($package.InstallLocation -and $app.Executable -and $app.Executable -notmatch '[$%]') {
                            $exe = [IO.Path]::Combine([string]$package.InstallLocation, [string]$app.Executable)
                        }
                        $items.Add(@{Key="package:$id";Name=$name;Kind='package';Path=$exe;Aumid=$id;Family=$family})
                    }
                } catch {
                    if (!($startNames.Keys -like "$family!*")) {
                        $items.Add(@{Key="family:$family";Name=[string]$package.Name;Kind='package';Family=$family})
                    }
                }
            }
        } catch { $failures.Add('MSIX/Appx 安装清单无法读取。') }
        ConvertTo-Json -InputObject @{Items=$items.ToArray();Failures=$failures.ToArray()} -Depth 4 -Compress
        """;
}
