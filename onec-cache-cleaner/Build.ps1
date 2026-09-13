param(
    [Parameter(Mandatory=$true)][string]$ReferenceDirectory,
    [string]$Output = (Join-Path $PSScriptRoot '../Clear-1CUserCache.exe')
)
$ErrorActionPreference = 'Stop'
# PowerShell 7 ships Roslyn. Compile against .NET Framework reference assemblies,
# not the .NET runtime of the build machine.
Add-Type -TypeDefinition 'internal static class OneCCompilerBootstrap {}'
$trees = [Microsoft.CodeAnalysis.SyntaxTree[]]@(foreach ($name in @('CacheEngine.cs', 'ProcessGuard.cs', 'Program.cs')) {
    [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot $name)))
})
$refs = [Microsoft.CodeAnalysis.MetadataReference[]]@(foreach ($name in @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Management.dll')) {
    [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile((Join-Path $ReferenceDirectory $name))
})
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::WindowsApplication).
    WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release).
    WithPlatform([Microsoft.CodeAnalysis.Platform]::AnyCpu).
    WithDeterministic($true)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('Clear-1CUserCache', $trees, $refs, $options)
$manifest = [IO.File]::OpenRead((Join-Path $PSScriptRoot 'app.manifest'))
$icon = [IO.File]::OpenRead((Join-Path $PSScriptRoot 'app.ico'))
$resources = $compilation.CreateDefaultWin32Resources($true, $false, $manifest, $icon)
$stream = [IO.File]::Create([IO.Path]::GetFullPath($Output))
try {
    $result = $compilation.Emit($stream, $null, $null, $resources)
    foreach ($diagnostic in $result.Diagnostics) { Write-Host $diagnostic }
    if (-not $result.Success) { throw 'Compilation failed.' }
} finally { $stream.Dispose(); $resources.Dispose(); $manifest.Dispose(); $icon.Dispose() }
Get-FileHash -Algorithm SHA256 $Output
