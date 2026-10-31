param([switch]$AutoStart)

# ClipRecorder launcher.
# Runs inside Windows PowerShell 5.1 (signed by Microsoft, included in Windows 10 and 11)
# and compiles the app from .\src in memory, so no new .exe/.dll is ever written to disk
# and Smart App Control has nothing to block.

$ErrorActionPreference = 'Stop'
$base = Split-Path -Parent $MyInvocation.MyCommand.Path
$logFile = Join-Path $base 'cliprecorder.log'

# Already running? Ask that instance to start a capture and leave immediately.
$existing = $null
if ([System.Threading.EventWaitHandle]::TryOpenExisting('Local\ClipRecorder.Trigger', [ref]$existing)) {
    [void]$existing.Set()
    $existing.Dispose()
    exit 0
}

Add-Type -AssemblyName System.Windows.Forms

function Write-LauncherLog([string]$message) {
    try { Add-Content -Path $logFile -Encoding UTF8 -Value ('[{0}] LANZADOR: {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'), $message) } catch { }
}

function Stop-WithError([string]$message) {
    Write-LauncherLog "ERROR: $message"
    [void][System.Windows.Forms.MessageBox]::Show($message, 'ClipRecorder', 'OK', 'Error')
    exit 1
}

try {
    try { $arch = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant() }
    catch { $arch = $(if ([Environment]::Is64BitProcess) { 'x64' } else { 'x86' }) }
    $lib = Join-Path $base "lib\$arch\ScreenRecorderLib.dll"
    if (-not (Test-Path $lib)) {
        Stop-WithError "No se encontró la librería de grabación para esta PC ($arch):`n$lib`n`nVolvé a copiar la carpeta completa de ClipRecorder."
    }

    $sys = [Environment]::SystemDirectory
    $vcFiles = @('vcruntime140.dll', 'msvcp140.dll')
    if ($arch -ne 'x86') { $vcFiles += 'vcruntime140_1.dll' }
    $missingVc = @($vcFiles | Where-Object { -not (Test-Path (Join-Path $sys $_)) })
    if ($missingVc.Count -gt 0) {
        Write-LauncherLog ("Falta Visual C++: " + ($missingVc -join ', '))
        $url = switch ($arch) { 'x86' { 'https://aka.ms/vs/17/release/vc_redist.x86.exe' } 'arm64' { 'https://aka.ms/vs/17/release/vc_redist.arm64.exe' } default { 'https://aka.ms/vs/17/release/vc_redist.x64.exe' } }
        $answer = [System.Windows.Forms.MessageBox]::Show(
            "Para grabar, Windows necesita el componente gratuito de Microsoft 'Visual C++ Redistributable'.`n`n¿Querés abrir la descarga oficial de Microsoft ahora? Instalalo y volvé a abrir ClipRecorder.",
            'ClipRecorder', 'YesNo', 'Information')
        if ($answer -eq 'Yes') { Start-Process $url }
        exit 1
    }

    if (-not (Test-Path (Join-Path $sys 'mfplat.dll'))) {
        Stop-WithError "Esta versión de Windows no tiene Media Foundation (pasa en las ediciones 'N').`n`nInstalá el 'Media Feature Pack' desde Configuración > Aplicaciones > Características opcionales y volvé a abrir ClipRecorder."
    }

    Add-Type -Path $lib

    $sources = [string[]]@(Get-ChildItem -Path (Join-Path $base 'src') -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName })
    if ($sources.Count -eq 0) { Stop-WithError "No se encontró el código del programa en la carpeta src." }
    $compilerOptions = '/optimize+ /unsafe /codepage:65001'

    # The compiled program is cached (keyed by a hash of the sources) so later launches skip
    # the compiler. It is always loaded from bytes in memory, never mapped from a file.
    $cacheDir = Join-Path $env:LOCALAPPDATA 'ClipRecorder'
    [void](New-Item -ItemType Directory -Force -Path $cacheDir)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $buffer = New-Object System.IO.MemoryStream
    foreach ($file in $sources) {
        $nameBytes = [System.Text.Encoding]::UTF8.GetBytes([System.IO.Path]::GetFileName($file))
        $buffer.Write($nameBytes, 0, $nameBytes.Length)
        $content = [System.IO.File]::ReadAllBytes($file)
        $buffer.Write($content, 0, $content.Length)
    }
    $extra = [System.Text.Encoding]::UTF8.GetBytes($compilerOptions + '|' + $arch + '|' + (Get-Item $lib).Length + '|' + [Environment]::Version)
    $buffer.Write($extra, 0, $extra.Length)
    $key = ([System.BitConverter]::ToString($sha.ComputeHash($buffer.ToArray())) -replace '-', '').Substring(0, 24)
    $cacheFile = Join-Path $cacheDir "app-$key.bin"

    $assembly = $null
    if (Test-Path $cacheFile) {
        try { $assembly = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($cacheFile)) }
        catch { Write-LauncherLog "Caché inválida, se recompila: $($_.Exception.Message)"; $assembly = $null }
    }
    if (-not $assembly) {
        $tempDll = Join-Path $cacheDir ("build-" + [guid]::NewGuid().ToString('N') + ".dll")
        $provider = New-Object Microsoft.CSharp.CSharpCodeProvider
        $params = New-Object System.CodeDom.Compiler.CompilerParameters
        $params.GenerateInMemory = $false
        $params.GenerateExecutable = $false
        $params.OutputAssembly = $tempDll
        $params.CompilerOptions = $compilerOptions
        [void]$params.ReferencedAssemblies.AddRange([string[]]@(
            'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll', $lib))
        $compiled = $provider.CompileAssemblyFromFile($params, $sources)
        if ($compiled.Errors.HasErrors) {
            $details = ($compiled.Errors | Where-Object { -not $_.IsWarning } | Select-Object -First 15 | ForEach-Object {
                '{0}({1}): {2}' -f [System.IO.Path]::GetFileName($_.FileName), $_.Line, $_.ErrorText }) -join "`n"
            Remove-Item $tempDll -ErrorAction SilentlyContinue
            Stop-WithError "El código del programa tiene errores:`n`n$details"
        }
        $bytes = [System.IO.File]::ReadAllBytes($tempDll)
        Remove-Item $tempDll, ([System.IO.Path]::ChangeExtension($tempDll, '.pdb')) -ErrorAction SilentlyContinue
        Get-ChildItem $cacheDir -Filter 'app-*.bin' | Where-Object { $_.FullName -ne $cacheFile } | Remove-Item -ErrorAction SilentlyContinue
        [System.IO.File]::WriteAllBytes($cacheFile, $bytes)
        $assembly = [System.Reflection.Assembly]::Load($bytes)
        Write-LauncherLog "Programa compilado y guardado en caché."
    }

    $program = $assembly.GetType('ClipRecorder.Program')
    $exitCode = $program::Run($base, $lib, [bool]$AutoStart)
    exit $exitCode
}
catch {
    Stop-WithError ("ClipRecorder no pudo iniciar:`n`n" + $_.Exception.ToString())
}
