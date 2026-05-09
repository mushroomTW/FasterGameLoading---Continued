$managedDir = 'C:\Program Files (x86)\Steam\steamapps\common\RimWorld\RimWorldWin64_Data\Managed'
$asmPath = Join-Path $managedDir 'Assembly-CSharp.dll'
Add-Type -Path (Join-Path $managedDir 'UnityEngine.CoreModule.dll') -ErrorAction SilentlyContinue
Add-Type -Path (Join-Path $managedDir 'UnityEngine.dll') -ErrorAction SilentlyContinue
$asm = [System.Reflection.Assembly]::LoadFrom($asmPath)
$type = $asm.GetType('Verse.StaticTextureAtlas')
$method = $type.GetMethod('Bake', [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::Public)
if ($method) {
    Write-Host "Method found."
    # Dump the methods that Bake calls
    $body = $method.GetMethodBody()
    $il = $body.GetILAsByteArray()
    Write-Host "IL Length: $($il.Length)"
} else {
    Write-Host "Method not found."
}
