param(
    [Parameter(Mandatory=$true)][string]$FrameworkDirectory,
    [Parameter(Mandatory=$true)][string]$PluginPath,
    [Parameter(Mandatory=$true)][string]$CecilPath,
    [Parameter(Mandatory=$true)][string]$OutputFile
)
$ErrorActionPreference='Stop'
Add-Type -LiteralPath $CecilPath
$framework=@{}
$plugin=$null
try {
    foreach($name in @('mscorlib','System','System.Core')) {
        $framework[$name]=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $FrameworkDirectory ($name+'.dll')))
    }
    $plugin=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($PluginPath)
    $count=0
    $missing=@()
    foreach($type in $plugin.MainModule.GetTypeReferences()) {
        if($framework.ContainsKey($type.Scope.Name)) {
            $count++
            if($null -eq $framework[$type.Scope.Name].MainModule.GetType($type.FullName)) {
                $missing+=$type.FullName+' @ '+$type.Scope.Name
            }
        }
    }
    $result=[pscustomobject]@{
        PluginPath=[IO.Path]::GetFullPath($PluginPath)
        FrameworkDirectory=[IO.Path]::GetFullPath($FrameworkDirectory)
        AssemblyVersion=$plugin.Name.Version.ToString()
        CheckedFrameworkTypeReferences=$count
        MissingFrameworkTypes=$missing
        ManagedPipeReferences=@($plugin.MainModule.GetTypeReferences() | Where-Object {$_.FullName.StartsWith('System.IO.Pipes.')} | ForEach-Object {$_.FullName})
        SHA256=(Get-FileHash -LiteralPath $PluginPath -Algorithm SHA256).Hash
    }
    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutputFile -Encoding utf8
    if($missing.Count){throw ('Missing framework types: '+($missing -join ', '))}
    if($result.ManagedPipeReferences.Count){throw 'The legacy plugin still statically depends on System.IO.Pipes.'}
    Write-Output ('PASS '+$count+' framework type references; no managed pipe dependency.')
}
finally {
    if($plugin){$plugin.Dispose()}
    foreach($assembly in $framework.Values){$assembly.Dispose()}
}
